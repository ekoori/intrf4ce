// intrf4ce's hidden renderer.
//
// Runs inside a web view nobody sees. Turns Mermaid, TeX and Vega-Lite into SVG that is
// drawn natively by Svg.Skia, so every result has to be self-contained: computed styles are
// written onto each element as presentation attributes and the <style> blocks are dropped,
// because a native SVG renderer is not a CSS engine. Mermaid node boxes are measured here,
// where layout is authoritative, and handed back so the stage camera can zoom to them.
//
// Results go back through invokeCSharpAction, which Avalonia's web view injects on every
// platform. Requests are serialised by the C# side; nothing here is re-entrant.
(function () {
  'use strict';

  var theme = {
    fontFamily: 'Segoe UI, Arial, sans-serif',
    fontSize: 16,
    text: '#E8ECF1',
    muted: '#9AA5B1',
    accent: '#FFB547',
    cool: '#5CE1E6',
    surface: '#121820',
    surface2: '#1B2430',
    line: '#3A4656',
    palette: ['#5CE1E6', '#FFB547', '#FF7A90', '#9D8CFF', '#7BE08A', '#F2D06B', '#6AA8FF', '#E58CF0'],
    dark: true
  };

  var counter = 0;

  function post(message) {
    invokeCSharpAction(JSON.stringify(message));
  }

  function hexToRgb(hex) {
    var h = hex.replace('#', '');
    return [parseInt(h.substring(0, 2), 16), parseInt(h.substring(2, 4), 16), parseInt(h.substring(4, 6), 16)];
  }

  function mix(a, b, t) {
    var x = hexToRgb(a);
    var y = hexToRgb(b);
    var out = '#';
    for (var i = 0; i < 3; i++) {
      var v = Math.round(x[i] * (1 - t) + y[i] * t);
      out += (v < 16 ? '0' : '') + v.toString(16);
    }
    return out;
  }

  function configureMermaid() {
    var vars = {
      darkMode: theme.dark,
      background: theme.surface,
      fontFamily: theme.fontFamily,
      fontSize: theme.fontSize + 'px',
      primaryColor: theme.surface2,
      primaryTextColor: theme.text,
      primaryBorderColor: theme.line,
      secondaryColor: theme.surface2,
      secondaryTextColor: theme.text,
      secondaryBorderColor: theme.line,
      tertiaryColor: theme.surface,
      tertiaryTextColor: theme.text,
      tertiaryBorderColor: theme.line,
      lineColor: theme.muted,
      textColor: theme.text,
      mainBkg: theme.surface2,
      nodeBorder: theme.line,
      nodeTextColor: theme.text,
      clusterBkg: theme.surface,
      clusterBorder: theme.line,
      titleColor: theme.text,
      edgeLabelBackground: theme.surface,
      actorBkg: theme.surface2,
      actorBorder: theme.line,
      actorTextColor: theme.text,
      actorLineColor: theme.muted,
      signalColor: theme.muted,
      signalTextColor: theme.text,
      labelBoxBkgColor: theme.surface2,
      labelBoxBorderColor: theme.line,
      labelTextColor: theme.text,
      loopTextColor: theme.text,
      noteBkgColor: theme.surface2,
      noteTextColor: theme.text,
      noteBorderColor: theme.accent,
      activationBkgColor: theme.surface2,
      activationBorderColor: theme.line,
      pie1: theme.palette[0], pie2: theme.palette[1], pie3: theme.palette[2], pie4: theme.palette[3],
      pie5: theme.palette[4], pie6: theme.palette[5], pie7: theme.palette[6], pie8: theme.palette[7],
      pieTitleTextColor: theme.text,
      pieSectionTextColor: theme.surface,
      pieLegendTextColor: theme.text,
      pieStrokeColor: theme.surface
    };

    // The colour scale behind mindmap sections, timelines and journeys. Left to Mermaid it
    // derives these from the primary colour, which on a dark theme comes out black.
    for (var c = 0; c < 12; c++) {
      var hue = theme.palette[c % theme.palette.length];
      vars['cScale' + c] = mix(hue, theme.surface, 0.62);
      vars['cScalePeer' + c] = mix(hue, theme.surface, 0.3);
      vars['cScaleLabel' + c] = theme.text;
      vars['cScaleInv' + c] = hue;
    }
    vars.git0 = theme.palette[0];

    var flat = { useMaxWidth: false };
    mermaid.initialize({
      startOnLoad: false,
      securityLevel: 'strict',
      htmlLabels: false,
      theme: 'base',
      fontFamily: theme.fontFamily,
      themeVariables: vars,
      flowchart: { htmlLabels: false, useMaxWidth: false, curve: 'basis', padding: 12 },
      sequence: flat, class: flat, state: flat, er: flat, gantt: flat, pie: flat,
      mindmap: flat, timeline: flat, journey: flat, quadrantChart: flat, xyChart: flat,
      block: flat, requirement: flat, gitGraph: flat, sankey: flat, architecture: flat
    });
  }

  // ---------------------------------------------------------------------------------------
  // Style inlining

  // Inherited in SVG: written only where an element differs from its parent, so a diagram
  // does not carry a font-family on every <tspan>.
  // The root writes only what differs from the initial value.
  var INHERITED = {
    'fill': 'rgb(0, 0, 0)', 'fill-opacity': '1', 'fill-rule': 'nonzero', 'stroke': 'none',
    'stroke-width': '1px', 'stroke-opacity': '1', 'stroke-dasharray': 'none',
    'stroke-dashoffset': '0px', 'stroke-linecap': 'butt', 'stroke-linejoin': 'miter',
    'stroke-miterlimit': '4', 'font-family': '', 'font-size': '', 'font-weight': '400',
    'font-style': 'normal', 'text-anchor': 'start', 'letter-spacing': 'normal',
    'visibility': 'visible', 'dominant-baseline': 'auto'
  };

  // Not inherited: written wherever they differ from the initial value.
  var LOCAL = {
    'opacity': '1', 'display': 'inline', 'clip-path': 'none', 'mask': 'none', 'filter': 'none',
    'marker-start': 'none', 'marker-mid': 'none', 'marker-end': 'none', 'stop-color': 'rgb(0, 0, 0)',
    'stop-opacity': '1', 'alignment-baseline': 'auto', 'baseline-shift': '0px'
  };

  var SKIP = { style: 1, title: 1, desc: 1, metadata: 1, script: 1 };

  function inline(root) {
    var parentStyles = new Map();
    var walk = function (el, parentComputed) {
      var tag = el.tagName.toLowerCase();
      if (SKIP[tag]) { return; }
      var cs = getComputedStyle(el);
      var mine = {};
      var i;
      for (var p in INHERITED) {
        var v = cs.getPropertyValue(p);
        mine[p] = v;
        var differs = parentComputed ? parentComputed[p] !== v : v !== INHERITED[p];
        if (v && differs) {
          el.setAttribute(p, v);
        }
      }
      for (var q in LOCAL) {
        var lv = cs.getPropertyValue(q);
        if (lv && lv !== LOCAL[q] && !(q === 'baseline-shift' && lv === 'baseline')) {
          el.setAttribute(q, lv);
        }
      }
      el.removeAttribute('style');
      parentStyles.set(el, mine);
      for (i = 0; i < el.children.length; i++) {
        walk(el.children[i], mine);
      }
    };
    walk(root, null);
    var styles = root.querySelectorAll('style');
    for (var s = 0; s < styles.length; s++) {
      styles[s].remove();
    }
  }

  // ---------------------------------------------------------------------------------------
  // Text flattening
  //
  // Mermaid lays labels out as nested <tspan>s positioned with dy, em units and
  // dominant-baseline, and native renderers each get a different subset of that wrong
  // (Svg.Skia overlaps sibling words and puts every label at the top of its box). The
  // browser already knows where each glyph went, so every label is rewritten here as plain
  // <text> runs at absolute baseline positions, which any SVG renderer draws identically.

  var TEXT_STYLE = ['font-family', 'font-size', 'font-weight', 'font-style', 'fill', 'fill-opacity',
    'stroke', 'stroke-width', 'opacity', 'letter-spacing'];
  var SVG_NS = 'http://www.w3.org/2000/svg';

  function styleKey(cs) {
    var parts = [];
    for (var i = 0; i < TEXT_STYLE.length; i++) { parts.push(cs.getPropertyValue(TEXT_STYLE[i])); }
    return parts.join('|');
  }

  function runsOf(text) {
    var leaves = Array.prototype.filter.call(text.querySelectorAll('tspan'), function (t) {
      return t.children.length === 0;
    });
    if (leaves.length === 0) { leaves = [text]; }
    var runs = [];
    for (var i = 0; i < leaves.length; i++) {
      var leaf = leaves[i];
      var content = leaf.textContent || '';
      var trimmed = content.replace(/^\s+/, '').replace(/\s+$/, '');
      if (!trimmed) { continue; }
      var count = leaf.getNumberOfChars();
      if (count === 0) { continue; }
      var lead = Math.min(content.length - content.replace(/^\s+/, '').length, count - 1);
      var start, end;
      try {
        start = leaf.getStartPositionOfChar(lead);
        end = leaf.getEndPositionOfChar(count - 1);
      } catch (e) {
        continue;
      }
      var cs = getComputedStyle(leaf);
      runs.push({ x: start.x, y: start.y, endX: end.x, text: trimmed, space: lead > 0, cs: cs, key: styleKey(cs) });
    }
    // Words on one line with one style become one run again, so kerning and spacing come
    // from a single shaping pass rather than being stitched together.
    var merged = [];
    for (var j = 0; j < runs.length; j++) {
      var r = runs[j];
      var prev = merged[merged.length - 1];
      var size = parseFloat(r.cs.getPropertyValue('font-size')) || 16;
      if (prev && prev.key === r.key && Math.abs(prev.y - r.y) < 0.01 &&
          r.x >= prev.endX - 0.5 && r.x - prev.endX < size * 0.8) {
        prev.text += (r.space || r.x - prev.endX > size * 0.15 ? ' ' : '') + r.text;
        prev.endX = r.endX;
      } else {
        merged.push(r);
      }
    }
    return merged;
  }

  function flattenText(svg) {
    var texts = Array.prototype.slice.call(svg.querySelectorAll('text'));
    for (var i = 0; i < texts.length; i++) {
      var text = texts[i];
      var runs = runsOf(text);
      var parent = text.parentNode;
      if (!parent) { continue; }
      var transform = text.getAttribute('transform');
      for (var j = 0; j < runs.length; j++) {
        var run = runs[j];
        var out = document.createElementNS(SVG_NS, 'text');
        if (transform) { out.setAttribute('transform', transform); }
        out.setAttribute('x', String(run.x));
        out.setAttribute('y', String(run.y));
        for (var k = 0; k < TEXT_STYLE.length; k++) {
          var v = run.cs.getPropertyValue(TEXT_STYLE[k]);
          if (v) { out.setAttribute(TEXT_STYLE[k], v); }
        }
        // Inline style, not attributes: Mermaid's stylesheet targets text directly, and a
        // CSS rule outranks a presentation attribute. The position is already the start of
        // the first glyph on its baseline, so nothing may re-anchor or re-align it.
        out.style.textAnchor = 'start';
        out.style.dominantBaseline = 'alphabetic';
        out.textContent = run.text;
        parent.insertBefore(out, text);
      }
      parent.removeChild(text);
    }
  }

  function viewBoxOf(svg) {
    var vb = svg.viewBox && svg.viewBox.baseVal;
    if (vb && vb.width > 0 && vb.height > 0) {
      return { x: vb.x, y: vb.y, w: vb.width, h: vb.height };
    }
    var b = svg.getBBox();
    return { x: b.x, y: b.y, w: b.width, h: b.height };
  }

  // A box in the root's user space (viewBox coordinates), whatever transforms sit between.
  function boxIn(root, el) {
    var b = el.getBBox();
    var m = root.getScreenCTM().inverse().multiply(el.getScreenCTM());
    var pts = [[b.x, b.y], [b.x + b.width, b.y], [b.x, b.y + b.height], [b.x + b.width, b.y + b.height]];
    var minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    for (var i = 0; i < pts.length; i++) {
      var x = m.a * pts[i][0] + m.c * pts[i][1] + m.e;
      var y = m.b * pts[i][0] + m.d * pts[i][1] + m.f;
      minX = Math.min(minX, x); minY = Math.min(minY, y);
      maxX = Math.max(maxX, x); maxY = Math.max(maxY, y);
    }
    return { x: minX, y: minY, w: maxX - minX, h: maxY - minY };
  }

  function mount(svgText) {
    var host = document.createElement('div');
    // In the layout, so getBBox and computed styles are real, but far off the page. Not
    // visibility:hidden, which is inherited and would be copied onto every element.
    host.style.cssText = 'position:absolute;left:-40000px;top:0;';
    host.innerHTML = svgText;
    document.body.appendChild(host);
    return host;
  }

  function finish(svg, vb) {
    svg.setAttribute('width', String(vb.w));
    svg.setAttribute('height', String(vb.h));
    svg.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
    return new XMLSerializer().serializeToString(svg);
  }

  // ---------------------------------------------------------------------------------------
  // Mermaid

  var DOM_ID = /(?:^|-)(?:flowchart|state|classId|entity|id)-(.+?)-\d+$/;

  function mermaidParts(svg, renderId) {
    var parts = [];
    var seen = {};
    // Some diagram types prefix node ids with the render id ("i4m7-apps"); the agent wrote "apps".
    var prefix = renderId + '-';
    var clean = function (id) {
      return id && id.indexOf(prefix) === 0 ? id.substring(prefix.length) : id;
    };
    var nodes = svg.querySelectorAll('g.node, g.cluster, g.mindmap-node, g.entity');
    for (var i = 0; i < nodes.length; i++) {
      var g = nodes[i];
      var id = g.getAttribute('data-id');
      if (!id) {
        var m = DOM_ID.exec(g.id || '');
        id = m ? m[1] : (g.id || null);
      }
      id = clean(id);
      if (!id || seen[id]) { continue; }
      seen[id] = 1;
      var b = boxIn(svg, g);
      if (b.w > 0 && b.h > 0) {
        parts.push({ id: id, x: b.x, y: b.y, w: b.w, h: b.h });
      }
    }
    // Sequence diagrams have no node groups; their actors are the useful targets.
    var actors = svg.querySelectorAll('rect.actor');
    for (var a = 0; a < actors.length; a++) {
      var name = actors[a].getAttribute('name');
      if (!name || seen[name]) { continue; }
      seen[name] = 1;
      var ab = boxIn(svg, actors[a]);
      parts.push({ id: name, x: ab.x, y: ab.y, w: ab.w, h: ab.h });
    }
    return parts;
  }

  async function renderMermaid(req) {
    var id = 'i4m' + (++counter);
    var result;
    try {
      result = await mermaid.render(id, req.source);
    } finally {
      // A failed render leaves its scratch element behind in the body.
      var stale = document.getElementById('d' + id);
      if (stale) { stale.remove(); }
    }
    var host = mount(result.svg);
    try {
      var svg = host.querySelector('svg');
      var vb = viewBoxOf(svg);
      var parts = mermaidParts(svg, id);
      flattenText(svg);
      inline(svg);
      var out = { svg: finish(svg, vb), viewBox: vb, parts: parts, width: vb.w, height: vb.h };
      if (svg.querySelector('foreignObject')) {
        out.warning = 'diagram uses HTML labels, which the native renderer cannot draw';
      }
      if (req.debug) {
        // The self-test keeps Mermaid's own output to compare against the native drawing.
        out.original = result.svg;
      }
      return out;
    } finally {
      host.remove();
    }
  }

  // ---------------------------------------------------------------------------------------
  // TeX

  async function renderTex(req) {
    var node = await MathJax.tex2svgPromise(req.source, { display: req.display !== false });
    var svg = node.querySelector('svg');
    var err = svg.querySelector('[data-mjx-error]');
    if (err) {
      throw new Error(err.getAttribute('data-mjx-error'));
    }
    var vb = svg.viewBox.baseVal;
    var em = req.fontSize || theme.fontSize;
    var w = vb.width / 1000 * em;
    var h = vb.height / 1000 * em;
    // MathJax puts the baseline at y = 0 in its viewBox; what is below it is the depth.
    var depth = (vb.y + vb.height) / 1000 * em;
    svg.setAttribute('width', String(w));
    svg.setAttribute('height', String(h));
    svg.removeAttribute('style');
    svg.removeAttribute('focusable');
    svg.removeAttribute('role');
    var text = new XMLSerializer().serializeToString(svg)
      .replace(/currentColor/g, req.color || theme.text);
    return { svg: text, viewBox: { x: vb.x, y: vb.y, w: vb.width, h: vb.height }, parts: [],
      width: w, height: h, depth: depth };
  }

  // ---------------------------------------------------------------------------------------
  // Vega-Lite

  function merge(base, over) {
    var out = {};
    var k;
    for (k in base) { out[k] = base[k]; }
    for (k in over) {
      if (over[k] && typeof over[k] === 'object' && !Array.isArray(over[k]) &&
          base[k] && typeof base[k] === 'object' && !Array.isArray(base[k])) {
        out[k] = merge(base[k], over[k]);
      } else {
        out[k] = over[k];
      }
    }
    return out;
  }

  function vegaTheme() {
    var axis = {
      labelColor: theme.muted, titleColor: theme.text, gridColor: theme.line, gridOpacity: 0.5,
      domainColor: theme.line, tickColor: theme.line, labelFontSize: 13, titleFontSize: 14,
      titleFontWeight: 600, labelFont: theme.fontFamily, titleFont: theme.fontFamily
    };
    return {
      background: null,
      font: theme.fontFamily,
      padding: 8,
      view: { stroke: null },
      axis: axis,
      legend: { labelColor: theme.text, titleColor: theme.text, labelFontSize: 13, titleFontSize: 13,
        labelFont: theme.fontFamily, titleFont: theme.fontFamily, symbolType: 'circle' },
      title: { color: theme.text, fontSize: 18, font: theme.fontFamily, anchor: 'start' },
      header: { labelColor: theme.text, titleColor: theme.text },
      range: { category: theme.palette },
      mark: { color: theme.palette[0] },
      arc: { stroke: theme.surface, strokeWidth: 2 },
      bar: { cornerRadiusEnd: 3 },
      line: { strokeWidth: 3 },
      point: { size: 70, filled: true },
      text: { color: theme.text, font: theme.fontFamily }
    };
  }

  async function renderVegaLite(req) {
    var spec = typeof req.source === 'string' ? JSON.parse(req.source) : req.source;
    spec = merge({}, spec);
    spec.config = merge(vegaTheme(), spec.config || {});
    if (req.width && spec.width === undefined) { spec.width = req.width; }
    if (req.height && spec.height === undefined) { spec.height = req.height; }
    if (spec.autosize === undefined) { spec.autosize = { type: 'fit', contains: 'padding' }; }
    var compiled = vegaLite.compile(spec).spec;
    var view = new vega.View(vega.parse(compiled), { renderer: 'none' });
    try {
      var text = await view.toSVG();
      var host = mount(text);
      try {
        var svg = host.querySelector('svg');
        var w = parseFloat(svg.getAttribute('width'));
        var h = parseFloat(svg.getAttribute('height'));
        var vb = { x: 0, y: 0, w: w, h: h };
        if (!svg.getAttribute('viewBox')) { svg.setAttribute('viewBox', '0 0 ' + w + ' ' + h); }
        inline(svg);
        return { svg: finish(svg, vb), viewBox: vb, parts: [], width: w, height: h };
      } finally {
        host.remove();
      }
    } finally {
      view.finalize();
    }
  }

  // ---------------------------------------------------------------------------------------

  async function run(req) {
    switch (req.kind) {
      case 'mermaid': return renderMermaid(req);
      case 'tex': return renderTex(req);
      case 'vegalite': return renderVegaLite(req);
      default: throw new Error('unknown render kind: ' + req.kind);
    }
  }

  window.i4 = {
    ready: function () {
      return typeof mermaid !== 'undefined' && typeof MathJax !== 'undefined' &&
        typeof MathJax.tex2svgPromise === 'function' && typeof vega !== 'undefined' &&
        typeof vegaLite !== 'undefined';
    },
    configure: function (t) {
      for (var k in t) { theme[k] = t[k]; }
      configureMermaid();
      return true;
    },
    render: function (req) {
      run(req).then(function (res) {
        res.id = req.id;
        res.ok = true;
        post(res);
      }, function (err) {
        post({ id: req.id, ok: false, error: String((err && err.message) || err) });
      });
      return true;
    }
  };
})();
