# Vendoring DjvuNet

This directory holds a **modified copy** of [DjvuNet](https://github.com/DjvuNet/DjvuNet),
used for pure-managed DjVu decoding so noPDF needs no external `ddjvu` tool and no native
libraries. DjvuNet is MIT-licensed; its notice is in [LICENSE.md](LICENSE.md) and must stay
here — noPDF ships this code compiled into every released binary.

- **Upstream:** https://github.com/DjvuNet/DjvuNet
- **Vendored from commit:** `c6f70f2` ("graphics: improve Bitmap architecture and validate
  parameters")
- **Vendored on:** 2026-07-19

## What was taken

Only `DjvuNet/` and `System.Attributes/`, and only their `.cs` files. The upstream test
projects, benchmarks, DjvuLibre interop, Skia and Drawing bindings, and solution files are not
used and were not copied.

`LICENSE.md` was originally dropped by that "`.cs` files only" filter and restored on
2026-07-19 — it is required, not optional. If you re-vendor, copy it first.

## Local modifications

Upstream is otherwise unchanged. Every deviation is listed here; each is marked in the source
with a `LOCAL MODIFICATION (noPDF)` comment.

| File | Change | Why |
|---|---|---|
| `DjvuNet.csproj` | Replaced wholesale | Upstream's build imports `DjvuNetBuild.props`/`.targets` and needs a bootstrapped `DjvuNet.Build.Tasks.dll`. The replacement is a plain SDK project listing the same sources. |
| `DjvuNet/DjvuDocument.cs` | Added `Load(Stream, string, int)` overload | Pages are decoded in parallel and a `DjvuDocument` is not thread-safe, so each worker needs its own. Re-opening the file per worker is punishing on network/cloud-mounted libraries, so the file is read once and each worker gets a stream over the same bytes. Safe because DjVu resolves includes inside the file and `Location` is informational only. |

Nothing else in the vendored sources is edited. Behaviour differences noPDF needs beyond that
are handled on the caller's side, in `src/NoPdf.Core/Import/DjvuDecoder.cs` — notably choosing
a subsample factor the decoder can actually render, and rendering bitonal pages from the JB2
mask, both of which are worked around rather than patched here.

## Re-vendoring

1. Clone upstream and check out the commit you want. On Windows, check out only the paths you
   need — some files under `Specs/` exceed `MAX_PATH` and will abort a full checkout:
   `git checkout <rev> -- DjvuNet/ System.Attributes/ LICENSE.md`
2. Copy `LICENSE.md`, then `DjvuNet/**/*.cs` and `System.Attributes/**/*.cs` over this
   directory. Do not copy `bin/`, `obj/` or `Properties/`.
3. Keep `DjvuNet.csproj` from this directory (do not take upstream's).
4. Re-apply the local modifications in the table above.
5. Build and run the DjVu tests. They cover the failure modes that motivated the caller-side
   workarounds: blank pages from an unsupported subsample, inverted colours, squashed
   double-page spreads, and bitonal documents that decode to nothing.
