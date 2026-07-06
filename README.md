# NoPdf

A cross-platform PDF **viewer and editor** built with C# / .NET 10 and Avalonia.

## Goals / feature set

- **Multi-tab viewing** — each open document is a tab.
- **View tools** — hand (pan), text select, zoom, highlight.
- **Comment tools** — sticky notes, text boxes, callouts, line/box, arrow, polyline.
- **Bookmarks** — the document outline plus user bookmarks.
- **Page manipulation** — export, import/merge, reorder, rotate, delete, insert.

Annotations are saved as **native PDF annotation objects** so they remain
readable in Acrobat and other viewers.

## Tech stack

| Concern            | Choice                                                        |
|--------------------|---------------------------------------------------------------|
| UI                 | Avalonia 12 (cross-platform: Windows / macOS / Linux)         |
| MVVM               | CommunityToolkit.Mvvm                                         |
| PDF rendering/text | [PDFiumCore](https://github.com/sungaila/PDFiumCore) (PDFium) |
| PDF editing / save | [PDFsharp](https://docs.pdfsharp.net/) 6                      |

PDFium rasterizes pages to BGRA and provides per-character geometry (for text
selection, highlight, search). PDFsharp owns document-level editing: writing
annotations and manipulating pages on save.

## Project layout

```
NoPdf.slnx
src/
  NoPdf.Core/        UI-agnostic PDF engine
    Rendering/       PdfiumLibrary (init + global lock), PdfDocument, RenderedPage
  NoPdf.App/         Avalonia desktop app (MVVM)
    ViewModels/      MainWindow / Document / Page view models
    Views/           DocumentView (page list + pan/zoom), PageView (one page)
    Rendering/       BitmapConverter (BGRA -> Avalonia WriteableBitmap)
    Editing/         EditorTool enum
```

### Key design notes

- **PDFium is not thread-safe.** Every native call goes through
  `PdfiumLibrary.Sync`. Rendering runs on background threads but is serialized
  by that lock.
- **Lazy, virtualized page rendering.** Pages live in a virtualizing `ListBox`;
  each `PageView` renders its page only while realized in the visual tree, and
  re-renders when the zoom changes. Page dimensions are known up front so
  layout/scroll are correct before pixels arrive.
- **Coordinate spaces.** PDFium page space is points (1/72"), origin bottom-left.
  The viewer works in DIPs at `Scale = zoom * 96/72`.

## Running

```bash
dotnet run --project src/NoPdf.App            # empty window
dotnet run --project src/NoPdf.App -- file.pdf # open a file
```

Requires the .NET 10 SDK.

## Keys, commands & config

Three ways to trigger a command (qutebrowser/vim style):

- **`:cmd`** — press `:` to open the command line, type a command.
- **`xy`** — a normal-mode multi-key hotkey (no `:`); e.g. `hl` highlights the
  selection, `gg`/`G` jump to first/last page. While typing a sequence, matching
  bindings are shown in a **which-key** hint above the command bar.
- **`/text`** — `/` opens search; then `n` / `N` jump to next / previous match.

The `:` command line shows a **history** of previous commands (click to reuse,
↑/↓ to cycle). Bindings and app parameters live in a YAML config written on
first run to `%APPDATA%/NoPdf/config.yaml` — it lists **every** command and lets
you remap any of them. Keys support modifiers and special keys
(`<c-r>`, `<a-x>`, `<c-s-p>`, `<up>`, `<pagedown>`, …) and an `aliases:` section
(so `:w` runs `save`). Text-box defaults (font size, frame colour, frame
opacity) are configurable too.

The app **remembers your open tabs** — order and active tab — and restores them
on the next launch (unless you open a file directly).

**Default hotkeys:** `hl` highlight · `gg`/`G` first/last page · `j`/`k` +
arrows scroll · `<pageup>`/`<pagedown>` page scroll · `H`/`L` (and `gt`/`gT`)
prev/next tab · `T` new tab · `X` close tab · `u`/`U` (or `<c-z>`/`<c-r>`)
undo/redo · `d` delete annotation · `zi`/`zo`/`zz` zoom · `zw`/`zp` fit
width/page · `b` bookmarks · `P` pages · `yy` copy. In normal mode focus stays
on the page (no Tab-cycling through the toolbar).

### Command reference

| Command | Aliases | Example | Action |
|---------|---------|---------|--------|
| `open <path\|mark>…` | `o` | `open report.pdf` | Open file(s); reuses an open tab for the same file |
| `O <path>` / `tabnew` | `opentab` | `O b.pdf` | Open in a **new** tab; `tabnext`/`tabprev`/`tabclose` |
| `page <n\|first\|last\|next\|prev>` | `p`, `goto` | `page 20` | Go to page |
| `zoom <pct\|in\|out\|reset>` | `z` | `zoom 150%` | Set zoom |
| `find <text>` | `f`, `/…` | `find invoice` | Search all pages |
| `findnext` / `findprev` | `n` / `N` | | Next/previous match |
| `highlight` | `hl` | | Highlight the selection, else pick the tool |
| tool names | | `hand`, `select`, `arrow`, `box`… | Select a tool |
| `print [range]` | | `print 2-3` | Export range → OS print |
| `save [path]` / `saveas <path>` | `w` | `save` | Write annotations to PDF |
| `m <name>` / `go <name>` | `mark` / `gm` | `m receipt-may` | Save / open a quickmark |
| `bookmark <name>` | `bm` | `bm intro` | Bookmark the current page |
| `toc` / `pages` | | | Toggle bookmarks / pages panel |
| `rotate <range> [cw\|ccw\|180]` | | `rotate 2-3 cw` | Rotate pages |
| `delete <range>` | `del` | `delete 4` | Delete pages |
| `insert <path> [at]` / `merge <path>` | | `merge b.pdf` | Insert / append another PDF |
| `extract <range> <path>` | | `extract 1-3 out.pdf` | Export pages to a new PDF |
| `zoom width` / `zoom page` | | | Fit width / whole page |
| `scrollup`/`scrolldown`/`scrollleft`/`scrollright` | | | Scroll by a few rows |
| `scrollpageup` / `scrollpagedown` | | | Scroll by a viewport |
| `delannot` | | | Delete the selected annotation |
| `undo` / `redo` | | | Undo / redo (annotations, pages, bookmarks) |
| `close` / `quit` | `q` / `qa` | | Close tab / exit |

Keyboard: `Ctrl+O` open · `Ctrl+S` save · `Ctrl+F` find · `Ctrl+C` copy
selection · `Ctrl +/-/0` zoom · `Ctrl+W` close tab · `Esc` clear selection.

## Roadmap / status

- [x] **M1 — Viewer shell**: multi-tab, open (dialog + CLI), PDFium render,
      lazy/virtualized page list, zoom (buttons + Ctrl-wheel), hand pan.
- [x] **M2 — Text layer**: char-box extraction, text selection, copy, find.
- [x] **M3 — Highlight**: native `Highlight` annotation (with Multiply-blend
      appearance stream) over selected text, saved via PDFsharp.
- [x] **Command line**: qutebrowser-style `:`/`/` bar, quickmarks, vector
      toolbar icons (dark toolbar, active-tool highlight).
- [x] **M4 — Annotation overlay & model**: sticky notes, text box, callout,
      line, rectangle, arrow, polyline; select/move/resize/delete; inline text
      editing; saved as native PDF annotations (with appearance streams).
- [x] **M5 — Bookmarks**: PDF outline panel (tree) + session bookmarks +
      click-to-navigate.
- [x] **M6 — Page manipulation**: thumbnails panel (rotate / move / delete /
      insert / merge), reorder, export page ranges. Rotation-aware rendering &
      coordinate mapping so text/annotations stay correct on rotated pages.
- [x] **M7 — Polish**: DPI-aware crisp rendering, fit-width / fit-page,
      undo/redo (annotations + page ops), recent files, and reading existing
      annotations back into the editable model (parsed & stripped so they
      round-trip without duplication).

## Pages & editing

Open the **pages panel** (toolbar grid icon or `:pages`) to rotate, reorder
(move up/down), delete, and insert/merge PDFs via the thumbnail toolbar; click a
thumbnail to jump there. **Undo/redo** (`Ctrl+Z` / `Ctrl+Y`) covers both
annotations and page operations. On load, existing highlights/notes/shapes are
parsed into the editable model, so they can be moved, resized, or deleted and
re-saved cleanly.

## Annotating

Pick a tool from the toolbar: **line / rectangle / arrow** drag to draw;
**polyline** click each vertex then double-click (or Enter) to finish;
**text box / callout** drag (or click) then type, Enter to commit; **sticky
note** click to place then type. With the **select** tool, click an annotation
to move it, drag its handles to resize, and press **Delete** to remove it.
`Ctrl+S` (or `:save`) writes them into the PDF.
