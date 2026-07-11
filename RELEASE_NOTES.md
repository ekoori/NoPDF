# noPDF release notes

Version scheme `v0.0.X-beta.YY`: **X** is the public release line, **YY** the
local build number (auto-incremented on every Debug build). Public releases are
cut with `scripts/release.ps1 -Publish`, which bumps **X**, resets **YY** to `00`,
and publishes `v0.0.X-beta.00` to GitHub. Newest first.

## Unreleased (0.0.2 line)

Formats & view:
- Open **CBZ/CBR/CB7/CBT** comic archives and **DjVu** documents (converted to PDF on
  load; DjVu needs DjVuLibre's `ddjvu` on PATH).
- **Parallel / book view**: `:view scroll [X]` (continuous, X across) and
  `:view book [X]` (X-page spread fit to the window).
- **Annotations panel** (`:annots`) listing every annotation; click to jump to it.

Editing & selection:
- **Select** is the default tool; marquee-select on empty space (drag down = enclose,
  drag up = touch). Middle-click a tab to close it.
- Nested grouping (group a group; ungroup peels one level). Shift-resize keeps aspect
  ratio. Grab-hand cursor while panning.

Editor & navigation:
- Multi-select annotations (shift-click); edit colour/width/font/opacity for the
  whole selection at once.
- Move the selection together; **Shift** constrains to one axis, **Ctrl+drag**
  duplicates.
- Group / ungroup (`Ctrl+G` / `Ctrl+Shift+G`, `:group` / `:ungroup`).
- Copy / paste annotation objects (`Ctrl+C` / `Ctrl+V`, `:yank` / `:paste`),
  shared across tabs.
- Paste a clipboard **screenshot** as a resizable image annotation with an
  optional frame and adjustable opacity; baked into the PDF on save.
- **Follow-links hint mode** (`f`): label every on-screen link, type the label to
  follow it (internal page jumps or web/mail links).
- Middle-mouse-button panning; Ctrl+wheel zoom now stays anchored to the cursor.
- Find (`/`, `n`, `N`) scrolls the match into view.

Signatures & fixes (earlier in the 0.0.2 line):
- Per-signature certificates: a *Use certificate* toggle with browse/generate
  (self-signed `.pfx`); `:sign <alias>` selects a preset; type the signing reason
  into the stamp and a certified signature prompts to save a signed copy.
- Signature stamp logo is transparent and left-aligned.
- Imported markup no longer gets a spurious default frame.
- Closing the last tab returns to the start page.

## v0.0.2-beta.00

- Release-process versioning refinements (X/YY scheme) and four-platform build.

## v0.0.1-beta.01

- First tagged prerelease. Self-contained single-file binaries for Windows,
  Linux, and macOS (Intel + Apple Silicon). Keyboard-driven PDF viewer/editor:
  annotations, bookmarks, page ops, visible + cryptographic signatures,
  qutebrowser-style command line, theming, session persistence.
