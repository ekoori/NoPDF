# noPDF release notes

Version scheme `v0.0.X-beta.YY`: **X** is the public release line, **YY** the
local build number (raised on every non-debug build). Public releases are cut with
`scripts/release.ps1 -Publish`, which bumps **X**, resets **YY** to `00`, and
publishes `v0.0.X-beta.00` to GitHub. Newest first.

## Unreleased (0.0.2 line)

Printing:
- **`:print` actually prints.** It used to hand the PDF to the default handler's
  "print" verb — which is noPDF itself — so it always failed. Pages are now
  rasterised and sent to the printer, annotations included. Windows only.
- Defaults live in the config (`print_printer`, `print_copies`, `print_fit_to_page`,
  `print_grayscale`, `print_landscape`); **`:printdialog`** picks them per-print and
  can save the choices back as the `:print` defaults.

Commands & docs:
- One command catalogue now drives **`:help`** (a landscape table per menu group:
  Command / Aliases / Keys / Description), the **config file's comments**, and the
  status-bar usage hint — so they can't drift apart.
- `:open "path with spaces"` works, and `:open` loads into the current tab.
- `:copypath` reports the path it copied.

Session & recovery:
- **Autosave**: unsaved edits are cached to `%AppData%/NoPdf/autosave` every
  `autosave_minutes` (default 5, 0 = off) and on exit, then recovered on the next
  start — still linked to the original file, so saving writes back to it.
- **Lazy tabs**: restoring a session no longer reads every file up front; a tab
  loads the first time you open it.

Tabs, help & bookmarks:
- Opening a file that's already open focuses its tab instead of duplicating it
  (paths are normalised; drag-drop / OS-launch / command all agree).
- `:showtabs` is now **`:tabspanel`** — no args toggles it; position it `top`,
  `bottom`, `left` or `right` of the view. `title_buttons_in_tabs` (default off)
  makes the min/close buttons ride with the panel.
- **`:help` opens a real help document in a tab** (commands, hotkeys, aliases).
- **`:bookmark` now writes into the PDF's outline**, so bookmarks are saved in the
  file and show up in other viewers; the never-persisted "My bookmarks" list is gone.
- Status bar always shows **page / pages**; the pages panel has a button to jump to
  the page shown in the view.
- A binding like `o: ":open"` pre-fills the command line instead of running it.
- Clicking an annotation in the annotations panel focuses that annotation.
- In view mode the cursor becomes a finger over links and a click follows them.

Formats & view:
- Open **CBZ/CBR/CB7/CBT** comic archives and **DjVu** documents (converted to PDF on
  load; DjVu needs DjVuLibre's `ddjvu` on PATH).
- **View modes**: `:view scroll [X]` (vertical scroll, X across), `:view full [X]`
  (only the X page(s) in focus fill the viewport — no other page can appear, at any
  zoom; scrolling turns pages) and `:view scrollh [X]` (horizontal scroll, X rows,
  filling each column downwards).
- The tabs panel can sit top/bottom/left/right, and the top bar collapses when it
  would otherwise be an empty strip (`title_buttons_in_tabs: true`).
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
