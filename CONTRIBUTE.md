# Contributing to noPDF

Everything development-related lives here. User documentation is in
[README.md](README.md); the changelog is [RELEASE_NOTES.md](RELEASE_NOTES.md).

## Prerequisites

- **.NET 10 SDK**
- Optional: [GitHub CLI](https://cli.github.com) (`gh auth login`) — only needed to
  publish a release
- Optional: DjVuLibre's `ddjvu` on `PATH` — only to open `.djvu` files

## Build & run

```bash
dotnet run --project src/NoPdf.App             # empty window (restores session)
dotnet run --project src/NoPdf.App -- file.pdf # open a file
dotnet build src/NoPdf.App -c Debug
```

A self-contained single-file build:

```bash
dotnet publish src/NoPdf.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Building bumps the build number (see *Versioning*). To build without consuming
one — which you want for throwaway/test builds — pass `-p:RevSuppress=true`.

## Project layout

```
src/
  NoPdf.Core/   UI-agnostic engine. PDFium rendering, text extraction, form
                interaction, signature verification; the annotation model and
                its PDFsharp read/write; page ops and outline.
  NoPdf.App/    Avalonia MVVM app: viewer, command line, panels, config.
scripts/
  release.ps1   The one way to cut a build or a release.
```

**The two PDF libraries do different jobs.** PDFium (via `PDFiumCore`) *renders*
and handles live form interaction. PDFsharp *writes* — annotations, page ops,
outline entries. Anything that reads pixels is PDFium; anything that rewrites the
file is PDFsharp. The exception is form field values, which only PDFium can
produce (`FPDF_SaveAsCopy`) because it owns the edit state.

### Architecture notes worth knowing before you touch these

**Annotation model.** On load, `AnnotationReader.LoadAndStrip` pulls known markup
out of the file into editable models and hands back "cleaned" bytes; the app
renders those and draws its own annotations on top. `AnnotationWriter` puts them
back as standard PDF objects on save. This is why annotations don't double-draw,
and why unknown annotation types are left alone in the document.

**Form filling** (`PdfDocument`, form region) has three traps, each of which cost
real debugging:

- PDFium draws widget annotations — form fields *and* visible signature stamps —
  **only** through its form module. A plain `FPDF_RenderPageBitmap` with
  `FPDF_ANNOT` silently skips them, so `RenderPage` does the two-pass render:
  page, then `FPDF_FFLDraw`.
- **Backspace and Enter are character events** (`FORM_OnChar('\b')`), not
  key-downs. `FORM_OnKeyDown(VK_BACK)` is silently ignored. Delete, the arrows,
  Home/End and Tab *are* key-downs.
- `FORM_OnBeforeClosePage` destroys PDFium's page view **and the focused field**.
  So the page being filled is held open (`_formPage`) across keystrokes *and*
  re-renders, and `RenderPage` reuses that handle rather than loading its own.
  For the same reason a hit-test must never acquire the form page — the hover
  cursor asks on every mouse move, and doing so would drop focus just from moving
  the mouse. Field rects are cached in `DocumentViewModel.FormFieldsOn`.

Lifetime: the form environment holds the document, so it is torn down *before*
`FPDF_CloseDocument`, and the `FPDF_FORMFILLINFO` must stay referenced because
PDFium keeps a pointer to it.

**Form values live inside PDFium** until `FlushFormValues()` pulls them out into
`_workingBytes`. That runs on save and in `BeginChange()` — a page operation
snapshots the working bytes, so without it a rotate-after-filling would silently
discard what the user typed.

**Signature verification** (`SignatureVerifier`) reads the PKCS#7 from the
`/Contents` string, *not* from the byte-range gap, so a file whose byte ranges are
malformed still reports its signer and date. Note PDFsharp rewrites the whole file
on `Save()` (no incremental update), so signing a document twice invalidates the
first signature — a real file in the wild does exactly this.

**Input routing.** `DocumentView` handles pointer-press in the **tunnel** phase,
so it sees clicks before `PageView` does; it has to bail out over links and form
fields or it starts a pan and the click never lands. Keyboard: when a form field
has focus, `MainWindow.OnGlobalKeyDown` returns early so `:` and `j` are typed
rather than triggering bindings.

## Versioning

Version is `v0.0.X-beta.YY`:

- **X** — the public release line. `VersionPrefix` in `Directory.Build.props`
  (committed). Bumped only by `release.ps1 -Publish`.
- **YY** — the build number, in the gitignored `build-number.txt`.
  **Auto-incremented on every build** by `Directory.Build.targets`, so the version
  always names the build you're actually running. Pass `-p:RevSuppress=true` to
  build without consuming one.

`:version` and the start page show `noPDF v0.0.X-beta.YY`.

## Releases

Always via `scripts/release.ps1`, from the repo root (PowerShell):

```powershell
./scripts/release.ps1                          # local build at the next YY, into Release/
./scripts/release.ps1 -Publish                 # public release: X++, YY=00, tag + GitHub Release
./scripts/release.ps1 -Version 0.0.3-beta.00   # set an exact version, then build
```

It publishes self-contained single-file binaries for `win-x64`, `win-x86`,
`linux-x64` and `osx-x64` into `Release/` (gitignored) as
`noPDF-v0.0.X-beta.YY-<rid>`, drops the current default `config.yaml` beside them,
and refreshes `Release/NoPdf.App.exe` with the win-x64 build. A plain run does
**not** touch git.

`-Publish` rolls `RELEASE_NOTES.md` (promotes *Unreleased* to a dated heading),
commits the bump, tags, pushes, and uploads the binaries to a prerelease GitHub
Release using that file as the body. GitHub only ever holds the `-beta.00` build
of each `X`.

## Configuration

`AppConfig.DefaultYaml` is generated, not a static string: the `{COMMANDS}`
placeholder is filled from `CommandDocs`, so **the config's documentation and
`:help` come from one source and can't drift**. Add a command to `CommandDocs.All`
and it appears in both.

`NoPdf.App --write-default-config <path>` dumps that file without starting the
UI; the release script uses it to ship `config.yaml` alongside the binaries.

## Testing

There's no test project. Changes are verified with throwaway harnesses in the
scratch directory that reference `NoPdf.Core`/`NoPdf.App` and drive the real view
models — plus rendering to an image and looking at it when the change is visual.
Prefer that over asserting on internals: several of the bugs above (backspace,
missing widgets, focus loss) were only visible by exercising the real path and
checking the actual output.

## Conventions

- Match the surrounding style. Comments explain constraints the code can't show —
  the PDFium traps above are the sort of thing worth a comment; restating the next
  line is not.
- Never commit `bin/`, `obj/`, or anything in `Release/`.
- Keep `RELEASE_NOTES.md`'s *Unreleased* section current; `-Publish` promotes it.
