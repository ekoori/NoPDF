# noPDF

A fast, keyboard-driven **PDF viewer and editor** for Windows, macOS, and Linux,
built with C# / .NET 10 and [Avalonia](https://avaloniaui.net/). It pairs a
distraction-free chromeless viewer with a **qutebrowser/vim-style command line**,
so most of the app is driven by `:` commands and multi-key hotkeys rather than
toolbars and dialogs.

Rendering uses Google's **PDFium**; page manipulation and annotation writing use
**PDFsharp**. Annotations are saved as standard PDF objects, so they open in
Acrobat and every other viewer. Besides PDF, noPDF opens **CBZ/CBR/CB7/CBT**
comic archives and **DjVu** documents (converted to PDF on load).

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
- **Signatures** — reusable signature presets (name, alias, frame style, and an
  optional certificate) in the `:signatures` panel. Place a stamp with the
  Signature tool or `:sign <alias>`, then type the signing reason into the stamp.
  A preset marked *Use certificate* (browse an existing `.pfx` or **Generate** a
  self-signed one) cryptographically signs the document — when you finish typing
  the reason, noPDF prompts to save a signed copy. `:signatures` also lists
  embedded digital-signature fields.
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

## Releases & versioning

Version is `v0.0.X-beta.YY`. **X** (the public release line) is `VersionPrefix` in
`Directory.Build.props`; **YY** (the build number) lives in the gitignored
`build-number.txt` and is **auto-incremented on every Debug build** by
`Directory.Build.targets`, so each build is uniquely versioned. The `:version`
command and the start page show `noPDF v0.0.X-beta.YY`.

Release builds read the current `YY` without incrementing, so all platform
artifacts of one release share a version. To cut a release, run from the repo root
(PowerShell):

```powershell
./scripts/release.ps1                    # local build at the current version, into Release/
./scripts/release.ps1 -Publish           # public release: X++, YY=00, roll notes, tag + GitHub Release
./scripts/release.ps1 -Version 0.0.3-beta.00   # set an exact version, then build
```

It publishes self-contained single-file binaries for `win-x64`, `win-x86`,
`linux-x64`, and `osx-x64` into `Release/` (gitignored), named
`noPDF-v0.0.X-beta.YY-<rid>`.

`-Publish` rolls [`RELEASE_NOTES.md`](RELEASE_NOTES.md) (promotes the *Unreleased*
section to a dated release heading), commits the bump, tags, and uploads the
binaries to a prerelease GitHub Release (notes = that file). GitHub only ever holds
the `-beta.00` build of each `X`. Requires the [GitHub CLI](https://cli.github.com)
authenticated once with `gh auth login`.

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
| **View** | `view scroll [X]` (continuous, X pages across), `view book [X]` (X-page spread) |
| **Find** | `find <text>`, `findnext` (`n`), `findprev` (`N`) |
| **Tools** | `hand`, `select`, `highlight`, `note`, `textbox`, `callout`, `line`, `rect`, `arrow`, `polyline`, `signature` (`sign`) |
| **Edit** | `undo`, `redo`, `copy`, `delannot`, `yank`/`paste` (annotations), `group`/`ungroup` |
| **Signatures** | `sign [alias]` (place, optionally selecting a preset), `signatures` (panel), `siglist` (list) |
| **Links** | `hint` / `follow` (`f`) — label every on-screen link, type the label to follow it |
| **Panels** | `annots` (annotations list), `props` (properties), `toc`, `pages` |
| **Pages** | `rotate <range> [cw\|ccw\|180]`, `delete <range>`, `insert <path> [at]`, `merge <path>`, `extract <range> [path]` |
| **Panels / UI** | `toc`, `pages`, `props`, `toolbar` |
| **Marks** | `m`/`go <name>` (file quickmarks), `marks` (picker), `bookmark`/`bmdel <name>` (page bookmarks) |
| **Config** | `config` (open config file), `bind <key> <command>` (or `bind :w save` for an alias) |
| **Misc** | `print [range]`, `save [path]`, `saveas [path]`, `help` |

### Default hotkeys

`hl` highlight · `gg`/`G` first/last page · `j`/`k` + arrows scroll ·
`f` follow links · `H`/`L` prev/next tab · `T` new tab · `X` close ·
`u`/`U` (or `Ctrl+Z`/`Ctrl+R`) undo/redo · `zi`/`zo`/`zz`/`zw`/`zp` zoom ·
`b` bookmarks · `P` pages · `yy` copy selection · `YY` copy file path ·
`d` delete annotation.

### Mouse & editing

Ctrl+wheel zooms around the cursor; the middle mouse button pans (grab-hand
cursor). Middle-click a tab to close it. **Select** is the default tool: click or
shift-click annotations to build a selection, or marquee on empty space (drag
**down** to select fully-enclosed objects, **up** to select touched ones). Edit
colour / width / font / opacity for the whole selection at once in the properties
panel; drag moves it together, **Shift** constrains to one axis (or keeps aspect
ratio while resizing), **Ctrl+drag** duplicates. `Ctrl+C`/`Ctrl+V` copy & paste
annotations; `Ctrl+G` / `Ctrl+Shift+G` group / ungroup (groups nest). `Ctrl+V`
also pastes a **screenshot** from the clipboard as a resizable image (optional
frame, adjustable opacity).

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
