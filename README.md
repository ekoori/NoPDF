# noPDF

A fast, keyboard-driven **PDF viewer and editor** for Windows, macOS, and Linux.
It pairs a distraction-free chromeless viewer with a **qutebrowser/vim-style
command line**, so most of the app is driven by `:` commands and multi-key
hotkeys rather than toolbars and dialogs.

Annotations are saved as standard PDF objects, so they open in Acrobat and every
other viewer. Besides PDF, noPDF opens **CBZ/CBR/CB7/CBT** comic archives and
**DjVu** documents (decoded in-process — no external tools needed).

## Install

Grab a binary from the [latest release](https://github.com/ekoori/NoPDF/releases)
— `win-x64`, `win-x86`, `linux-x64`, or `osx-x64`. Each is a **self-contained
single file**: no runtime to install, nothing to unpack. Run it.

On Linux/macOS you may need to mark it executable first:

```bash
chmod +x noPDF-v0.0.3-beta.00-linux-x64 && ./noPDF-v0.0.3-beta.00-linux-x64
```

Want to build from source instead? See [CONTRIBUTE.md](CONTRIBUTE.md).

## Quick start

Open a file by passing it on the command line, dragging it onto the window, or
pressing `o`. Then:

| Key | |
|---|---|
| `:` | open the command line (`Esc` closes it) |
| `/` | search — `n` / `N` for next / previous match |
| `j` `k` / arrows | scroll · `gg` / `G` first / last page |
| `f` | label every link and form field on screen; type a label to jump to it |
| `zi` `zo` `zz` | zoom in / out / reset · `zw` `zp` fit width / page |
| `:help` | the full command table, in a tab |

Typing a command shows its usage in the status bar; `Tab` cycles matching
command names, and `↑`/`↓` walk the history. A half-finished hotkey shows a
which-key hint until you complete it.

## What it does

- **Multi-tab viewing** with session restore, per-file view position (zoom,
  scroll, and view mode), single-instance (files open as new tabs), drag-and-drop.
- **View modes** — `:view scroll [N]` (vertical, N across), `:view full [N]`
  (only the N page(s) in focus), `:view scrollh [N]` (horizontal, N rows).
- **Annotations** — highlight, sticky note, text box, callout, line, rectangle,
  arrow, polyline. Create, move, resize, delete; edit colour, width, fill, font
  and opacity in a properties panel. Existing annotations are read back and stay
  editable. Group them (`Ctrl+G`, groups nest and are saved in the file).
- **Fill in forms** — see below.
- **Signatures** — visible stamps and real cryptographic signing; `:siglist`
  verifies what's already in a document.
- **Bookmarks** — the PDF outline plus your own, written into the file itself.
- **Page operations** — a thumbnails panel with multi-select for rotate,
  reorder, delete, insert and merge; export page ranges. `:newfile` starts an empty
  document and `:newpage [size]` adds a blank page after the current one — `a4`, `a3`,
  `a5`, `letter`, `legal`, an `l` suffix for landscape, or millimetres as `200x150`.
- **Flatten** (`:flatten`) — bakes annotations and filled form fields into the page
  itself, so they're no longer separate objects anyone can move or delete.
- **Printing** with named presets.
- **Undo/redo** across annotations, page operations, bookmarks, form fields, and
  closed tabs.
- **Themeable** (dark / light / follow-OS), configured by one YAML file that
  hot-reloads as you save it.

## Filling in forms

View mode (`:hand`) is also form mode. Click a field — or press `f` and type its
label — and type. The pointer shows an I-beam over a field, and clicking one
places the caret instead of panning the page.

- Drag to select text in a field; `Ctrl+C` copies it.
- `Backspace` / `Delete` / arrows / `Home` / `End` edit; `Tab` moves on.
- `Ctrl+Z` undoes your typing inside the field.
- **`Esc`** leaves the field and gives the keyboard back to normal mode — while a
  field has focus, keys like `:` and `j` are just characters you're typing.
- Clicking elsewhere commits the field. `:save` writes the values into the PDF.

Checkboxes, radio buttons, dropdowns and list boxes all respond to clicks.
Signature fields are left alone — those are what `:sign` is for.

## Signatures

Reusable presets (name, alias, frame style, optional certificate) live in the
`:signatures` panel. Place a stamp with the Signature tool or `:sign <alias>`,
then type the signing reason into the stamp. A preset marked *Use certificate*
(browse a `.pfx`, or **Generate** a self-signed one) signs the document for real:
finish typing the reason and noPDF offers to save a signed copy.

**`:siglist` verifies** the signatures already in a document and lists them at the
bottom of the panel: who signed, when, whether the bytes are unchanged since, and
whether the certificate chains to a root your machine trusts. A signature can be
*valid*, *intact but untrusted* (e.g. self-signed), *appended to since signing*,
or outright **invalid** — and it says which.

## Printing

`:printdialog` opens a dialog; anything you set there can be saved as a **named
preset**, and one preset can be the default.

| | |
|---|---|
| `:print` | print with the default preset |
| `:print <preset> [range]` | print with a named preset |
| `:print 2-5,8` | a page range, default preset |
| `:printpreset list` | what presets exist |
| `:printpreset default <name>` | pick the default |
| `:printpreset del <name>` | remove one |

## Mouse & editing

Ctrl+wheel zooms around the cursor; the middle mouse button pans. Middle-click a
tab to close it. **Select** is the default tool: click or shift-click annotations
to build a selection, or marquee on empty space (drag **down** for fully-enclosed
objects, **up** for touched ones). Edit colour / width / font / opacity for the
whole selection at once; drag moves it together, **Shift** constrains to one axis
(or keeps the aspect ratio while resizing), **Ctrl+drag** duplicates.
`Ctrl+C`/`Ctrl+V` copy and paste annotations — `Ctrl+V` also pastes a
**screenshot** from the clipboard as a resizable image.

## Commands

`:help` opens the full table (command, aliases, keys, description) in a tab.
The highlights:

| Area | Commands |
|------|----------|
| **Files / tabs** | `open` (`o`), `newfile`, `O`/`tabnew`, `tabnext`, `tabprev`, `tabclose`, `close`, `quit`, `reopen`, `reload`, `copypath` |
| **Save / print** | `save` (`w`) `[path]`, `saveas [path]`, `print [preset] [range]`, `printdialog`, `printpreset` |
| **Sessions** | `session save\|load\|del\|list <name>` |
| **Navigate** | `page <n\|first\|last\|next\|prev>`, `scrollup`/`scrolldown`/`scrollleft`/`scrollright`, `scrollpageup`/`scrollpagedown`, `hint` (`f`) |
| **Zoom / view** | `zoom <pct\|in\|out\|reset\|width\|page>`, `fit`, `fitwidth`, `view <scroll\|full\|scrollh> [N]` |
| **Find** | `find <text>` (`/`), `findnext` (`n`), `findprev` (`N`) |
| **Tools** | `hand`, `select`, `highlight`, `note`, `textbox`, `callout`, `line`, `rect`, `arrow`, `polyline`, `sign` |
| **Edit** | `undo`, `redo`, `copy`, `delannot`, `yank`/`paste`, `group`/`ungroup` |
| **Pages** | `rotate <range> [cw\|ccw\|180]`, `delete <range>`, `insert <path> [at]`, `newpage [size]`, `merge <path>`, `extract <range> [path]`, `flatten` |
| **Panels** | `toc` (`b`), `pages` (`P`), `props`, `annots`, `signatures`, `toolbar`, `tabspanel [top\|bottom\|left\|right]` |
| **Marks** | `m`/`go <name>` (file quickmarks), `marks`, `bookmark`/`bmdel <name>` |
| **Config** | `config`, `bind <key> <command>`, `version`, `help` |

### Default hotkeys

`hl` highlight · `gg`/`G` first/last page · `j`/`k` + arrows scroll ·
`f` follow links & form fields · `H`/`L` prev/next tab · `T` new tab · `X` close ·
`u`/`U` (or `Ctrl+Z`/`Ctrl+R`) undo/redo · `zi`/`zo`/`zz`/`zw`/`zp` zoom ·
`b` bookmarks · `P` pages · `yy` copy selection · `YY` copy file path ·
`d` / `Del` delete annotation.

## Configuration

On first run noPDF writes `%APPDATA%/NoPdf/config.yaml` — a heavily commented
file listing every command and option. It **hot-reloads as you save it**. It sets
the theme, whether the toolbar and title bar are shown, the signer name, text-box
and print defaults, autosave behaviour, and every key binding: normal-mode
hotkeys, an `aliases:` section, and modifier/special keys (`<c-r>`, `<a-x>`,
`<c-s-p>`, `<up>`, `<pagedown>`, …).

Use `:config` to open it in your editor, or `:bind` to add a binding without
leaving the app. A copy of the default config ships next to each release as
`config.yaml`.

Unsaved edits are cached to `%APPDATA%/NoPdf/autosave` every `autosave_minutes`
(default 5, `0` = only on exit) and recovered on the next start — still linked to
the original file, so saving writes back to it. Cached edits expire after
`autosave_expiry_hours` (default 24).

## Contributing

Build instructions, architecture, and the release process are in
[CONTRIBUTE.md](CONTRIBUTE.md). Changes are logged in
[RELEASE_NOTES.md](RELEASE_NOTES.md).

Built with Claude Code.
