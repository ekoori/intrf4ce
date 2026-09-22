# Vendored code

Browser libraries that run inside the hidden renderer (`src/Intrf4ce.App/Web/vendor/`). They
are checked in rather than fetched at build time so a build works offline and always ships the
exact bytes that were tested. Their licences are copied beside them in `licenses/`.

| File | Package | Version | Licence | Taken from |
|---|---|---|---|---|
| `mermaid.min.js` | mermaid | 12.0.0 | MIT | `dist/mermaid.min.js` |
| `mathjax-tex-svg-full.js` | mathjax | 3.2.2 | Apache-2.0 | `es5/tex-svg-full.js` |
| `vega.min.js` | vega | 6.4.0 | BSD-3-Clause | `build/vega.min.js` |
| `vega-lite.min.js` | vega-lite | 6.4.3 | BSD-3-Clause | `build/vega-lite.min.js` |

To update one, `npm pack <package>@<version>`, copy the file named above over the old one,
update this table, and run `intrf4ce --selftest render <dir>`. Then look at the PNGs: the
self-test passing only proves the library loaded and returned SVG, not that Svg.Skia still
draws it faithfully.

**MathJax stays on 3.2.2 on purpose.** MathJax 4 loads its fonts on demand from separate
packages, which would mean serving files to the hidden page; 3.2.2's `tex-svg-full.js` is one
self-contained script.

**Mermaid is used with plain SVG labels** (`htmlLabels: false`). HTML labels are drawn with
`<foreignObject>`, which no native SVG renderer draws.
