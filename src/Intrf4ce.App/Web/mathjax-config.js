// Must run before the MathJax bundle is injected: MathJax reads window.MathJax as its
// configuration when it starts. No automatic typesetting (there is no page to typeset), and
// no glyph cache, so every SVG it returns carries its own paths and can be drawn on its own.
window.MathJax = {
  startup: { typeset: false },
  svg: { fontCache: 'none' },
  tex: {
    packages: { '[+]': ['ams', 'newcommand', 'boldsymbol', 'color', 'cancel'], '[-]': ['noundefined'] },
    // Throw instead of drawing the error in red: the agent that wrote the TeX has to hear
    // about it, and a red formula on a slide tells the user nothing useful.
    formatError: function (jax, err) { throw err; }
  }
};
