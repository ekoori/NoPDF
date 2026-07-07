# noPDF

A fast, keyboard-driven **PDF viewer and editor** for Windows, macOS, and Linux,
built with C# / .NET 10 and [Avalonia](https://avaloniaui.net/). It pairs a
distraction-free chromeless viewer with a **qutebrowser/vim-style command line**,
so most of the app is driven by `:` commands and multi-key hotkeys rather than
toolbars and dialogs.

Rendering uses Google's **PDFium**; page manipulation and annotation writing use
**PDFsharp**. Annotations are saved as standard PDF objects, so they open in
Acrobat and every other viewer.

## Features

- **Multi-tab viewing** with session restore (reopens your last tabs), per-file
  view position (zoom + scroll), single-instance (external files open as new
  tabs), and drag-and-drop.
- **Navigation & view** — hand pan, text selection / copy / find, zoom
  (incl. fit-width / fit-page), rotation-aware rendering, crisp HiDPI output.
- **Annotations** — highlight, sticky note, text box, callout, line, rectangle,
  arrow, polyline. Create, move, resize, delete; edit colour, line width, fill,
  font, and opacity in a properties panel. Existing annotations are read back
  and stay editable.
- **Signatures** — place one or more visible signature stamps (signer name,
  optional note, timestamp) over a faint noPDF watermark; `:signatures` lists
  them (including embedded digital-signature fields).
- **Bookmarks** — the PDF outline plus your own page bookmarks, in a side panel.
- **Page operations** — a thumbnails panel with multi-select for rotate,
  reorder, delete, insert, and merge; export page ranges.
- **Undo/redo** across annotations, page operations, bookmarks, and closed tabs.
- **Themeable** (dark / light / follow-OS) and configurable via a single YAML
  file that hot-reloads on save.

## Running

Requires the .NET 10 SDK.

```bash
dotnet run --project src/NoPdf.App            # empty window (restores session)
dotnet run --project src/NoPdf.App -- file.pdf # open a file
```

A self-contained single-file build (no runtime needed):

```bash
dotnet publish src/NoPdf.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## Using the command line

- **`:`** opens the command line; **`/`** opens search; **`Esc`** closes.
- Typing a command shows its **usage** in the status bar; **`Tab`** cycles
  matching command names; **`↑/↓`** walk the history (shown above the input).
- A multi-key hotkey shows a **which-key** hint until the sequence completes.

### Command summary

| Area | Commands |
|------|----------|
| **Files / tabs** | `open` (`o`), `O`/`tabnew` (new tab), `tabnext`, `tabprev`, `tabclose`, `close`, `quit`, `reopen`, `copypath` |
| **Sessions** | `session save\|load\|del\|list <name>` |
| **Navigate** | `page <n\|first\|last\|next\|prev>`, `scrollup`/`scrolldown`/`scrollleft`/`scrollright`, `scrollpageup`/`scrollpagedown` |
| **Zoom** | `zoom <pct\|in\|out\|reset\|width\|page>`, `fit`, `fitwidth` |
| **Find** | `find <text>`, `findnext` (`n`), `findprev` (`N`) |
| **Tools** | `hand`, `select`, `highlight`, `note`, `textbox`, `callout`, `line`, `rect`, `arrow`, `polyline`, `signature` (`sign`) |
| **Edit** | `undo`, `redo`, `copy`, `delannot` |
| **Signatures** | `sign` (place), `signatures` (list) |
| **Pages** | `rotate <range> [cw\|ccw\|180]`, `delete <range>`, `insert <path> [at]`, `merge <path>`, `extract <range> [path]` |
| **Panels / UI** | `toc`, `pages`, `props`, `toolbar` |
| **Marks** | `m`/`go <name>` (file quickmarks), `marks` (picker), `bookmark`/`bmdel <name>` (page bookmarks) |
| **Config** | `config` (open config file), `bind <key> <command>` (or `bind :w save` for an alias) |
| **Misc** | `print [range]`, `save [path]`, `saveas [path]`, `help` |

### Default hotkeys

`hl` highlight · `gg`/`G` first/last page · `j`/`k` + arrows scroll ·
`H`/`L` prev/next tab · `T` new tab · `X` close · `u`/`U` (or `Ctrl+Z`/`Ctrl+R`)
undo/redo · `zi`/`zo`/`zz`/`zw`/`zp` zoom · `b` bookmarks · `P` pages ·
`yy` copy selection · `YY` copy file path · `d` delete annotation.

## Configuration

On first run noPDF writes `%APPDATA%/NoPdf/config.yaml` (a documented default
listing every command). It hot-reloads on save. It controls the theme, whether
the toolbar / title bar are shown, the signer name, text-box defaults, and all
key bindings — normal-mode hotkeys, an `aliases:` section, and modifier/special
keys (`<c-r>`, `<a-x>`, `<c-s-p>`, `<up>`, `<pagedown>`, …). Use `:bind` to add a
binding without leaving the app, or `:config` to open the file in your editor.

## Project layout

```
src/
  NoPdf.Core/   UI-agnostic engine: PDFium rendering & text, annotation model,
                annotation read/write (PDFsharp), page ops, outline, signatures
  NoPdf.App/    Avalonia MVVM app: viewer, command line, panels, config
```

Built with Claude Code.
