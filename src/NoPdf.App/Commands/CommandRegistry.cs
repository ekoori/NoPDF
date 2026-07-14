using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NoPdf.App.Editing;
using NoPdf.App.ViewModels;
using NoPdf.Core.Annotations;
using NoPdf.Core.Editing;

namespace NoPdf.App.Commands;

/// <summary>
/// Parses and dispatches qutebrowser-style command-line input against the app.
/// Handlers return a status message (or null); thrown exceptions become errors.
/// </summary>
public sealed class CommandRegistry
{
    private readonly MainWindowViewModel _main;
    private readonly Quickmarks _quickmarks;
    private readonly Dictionary<string, Func<string[], string, Task<string?>>> _commands;

    private static readonly Dictionary<string, EditorTool> Tools = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hand"] = EditorTool.Hand,
        ["select"] = EditorTool.Select, ["sel"] = EditorTool.Select,
        ["note"] = EditorTool.Note,
        ["textbox"] = EditorTool.TextBox, ["tb"] = EditorTool.TextBox,
        ["callout"] = EditorTool.Callout,
        ["line"] = EditorTool.Line,
        ["box"] = EditorTool.Rectangle, ["rect"] = EditorTool.Rectangle, ["rectangle"] = EditorTool.Rectangle,
        ["arrow"] = EditorTool.Arrow,
        ["poly"] = EditorTool.Polyline, ["polyline"] = EditorTool.Polyline,
        // sign/signature are handled explicitly (they accept a preset alias).
    };

    public CommandRegistry(MainWindowViewModel main, Quickmarks quickmarks)
    {
        _main = main;
        _quickmarks = quickmarks;
        _commands = new(StringComparer.OrdinalIgnoreCase)
        {
            ["open"] = Open, ["o"] = Open,
            ["O"] = OpenNewTab, ["opentab"] = OpenNewTab, ["tabnew"] = OpenNewTab,
            ["tabnext"] = (_, _) => { _main.TabNext(); return Msg(null); },
            ["tabprev"] = (_, _) => { _main.TabPrev(); return Msg(null); },
            ["tabclose"] = Close,
            ["bmdel"] = BmDel,
            ["page"] = Page, ["p"] = Page, ["goto"] = Page,
            ["zoom"] = Zoom, ["z"] = Zoom,
            ["find"] = Find, ["f"] = Find, ["search"] = Find,
            ["findnext"] = (_, _) => Task.FromResult(FindStep(+1)), ["n"] = (_, _) => Task.FromResult(FindStep(+1)),
            ["findprev"] = (_, _) => Task.FromResult(FindStep(-1)), ["N"] = (_, _) => Task.FromResult(FindStep(-1)),
            ["highlight"] = Highlight, ["hl"] = Highlight,
            ["print"] = Print,
            ["save"] = Save, ["w"] = Save,
            ["saveas"] = SaveAs,
            ["close"] = Close, ["q"] = Close,
            ["quit"] = Quit, ["qa"] = Quit,
            ["m"] = Mark, ["mark"] = Mark,
            ["delmark"] = DelMark, ["dm"] = DelMark,
            ["go"] = Go, ["gm"] = Go,
            ["bookmark"] = Bookmark, ["bm"] = Bookmark,
            ["toc"] = (_, _) => Task.FromResult(ToggleToc()),
            ["pages"] = (_, _) => Task.FromResult(TogglePages()),
            ["toolbar"] = (_, _) => { _main.ToggleToolbarCommand.Execute(null); return Msg(_main.IsToolbarVisible ? "Toolbar shown" : "Toolbar hidden"); },
            ["config"] = (_, _) => Config(),
            ["bind"] = Bind,
            ["showtabs"] = (_, rest) => { _main.ShowTabs(rest); return Msg($"Tabs: {_main.Config.Tabs}"); },
            ["sign"] = Sign, ["signature"] = Sign,
            ["signatures"] = (_, _) => { _main.ToggleSignaturePanelCommand.Execute(null); return Msg(null); },
            ["siglist"] = (_, _) => Msg(ListSignatures()),
            ["props"] = (_, _) => { _main.ToggleAnnotationPanelCommand.Execute(null); return Msg(_main.IsAnnotationPanelOpen ? "Annotation panel shown" : "Annotation panel hidden"); },
            ["annot"] = (_, _) => { _main.ToggleAnnotationPanelCommand.Execute(null); return Msg(null); },
            ["annots"] = (_, _) => { _main.ToggleAnnotationListPanelCommand.Execute(null); return Msg(null); },
            ["annotations"] = (_, _) => { _main.ToggleAnnotationListPanelCommand.Execute(null); return Msg(null); },
            ["rotate"] = Rotate,
            ["delete"] = Delete, ["del"] = Delete,
            ["extract"] = Extract,
            ["insert"] = Insert,
            ["merge"] = Merge,
            ["undo"] = (_, _) => { _main.Undo(); return Msg(null); },
            ["redo"] = (_, _) => Task.FromResult(DoRedo()),
            ["reopen"] = (_, _) => { _main.ReopenClosedTab(); return Msg(null); },
            ["reload"] = (_, _) => { Doc?.ReloadFromDisk(); return Msg(Doc is null ? "No document" : "Reloaded from disk"); },
            ["fit"] = (_, _) => Task.FromResult(Fit(false)),
            ["fitwidth"] = (_, _) => Task.FromResult(Fit(true)),
            ["view"] = View,
            ["copy"] = (_, _) => { _main.RequestCopy(); return Msg(null); },
            ["copypath"] = (_, _) =>
            {
                var p = Doc?.FilePath;
                if (string.IsNullOrEmpty(p)) return Msg("No document");
                _main.RequestCopyText(p); return Msg("Copied path");
            },
            ["session"] = Session, ["sess"] = Session,
            ["scrolldown"] = (_, _) => Scroll(0, +1),
            ["scrollup"] = (_, _) => Scroll(0, -1),
            ["scrollleft"] = (_, _) => Scroll(-1, 0),
            ["scrollright"] = (_, _) => Scroll(+1, 0),
            ["scrollpagedown"] = (_, _) => { Doc?.ScrollPage(+1); return Msg(null); },
            ["scrollpageup"] = (_, _) => { Doc?.ScrollPage(-1); return Msg(null); },
            ["delannot"] = (_, _) => { Doc?.DeleteSelectedAnnotation(); return Msg(null); },
            ["group"] = (_, _) => { Doc?.GroupSelected(); return Msg("Grouped"); },
            ["ungroup"] = (_, _) => { Doc?.UngroupSelected(); return Msg("Ungrouped"); },
            ["yank"] = (_, _) => { Doc?.CopySelectedAnnotations(); return Msg("Copied annotation(s)"); },
            ["paste"] = (_, _) => { Doc?.PasteAnnotations(); return Msg(null); },
            ["marks"] = (_, _) => Task.FromResult(OpenMarks()),
            ["hint"] = (_, _) => Msg(Doc is null ? "No document" : (Doc.EnterHintMode() ? null : "No links on screen")),
            ["follow"] = (_, _) => Msg(Doc is null ? "No document" : (Doc.EnterHintMode() ? null : "No links on screen")),
            ["help"] = (_, _) => Help(),
            ["version"] = (_, _) => Task.FromResult<string?>(NoPdf.App.AppVersion.Display),
        };
        // Register tool commands.
        foreach (var kv in Tools)
        {
            var tool = kv.Value;
            _commands[kv.Key] = (_, _) => Task.FromResult(SetTool(tool));
        }
    }

    public IEnumerable<string> CommandNames() => _commands.Keys.OrderBy(k => k);

    /// <summary>Executes a full command line (e.g. "page 20"). Returns a status message.</summary>
    public async Task<string?> ExecuteAsync(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return null;

        // Resolve a configured alias on the first word (single level).
        {
            int a = input.IndexOf(' ');
            string first = a < 0 ? input : input[..a];
            if (_main.Config.Aliases.TryGetValue(first, out var target) && !string.IsNullOrWhiteSpace(target))
                input = a < 0 ? target : target + " " + input[(a + 1)..];
        }

        // Split into command + argument remainder.
        int sp = input.IndexOf(' ');
        string name = sp < 0 ? input : input[..sp];
        string rest = sp < 0 ? "" : input[(sp + 1)..].Trim();
        string[] args = rest.Length == 0
            ? Array.Empty<string>()
            : rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (!_commands.TryGetValue(name, out var handler))
            return $"Unknown command: {name}";

        try
        {
            return await handler(args, rest);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private DocumentViewModel? Doc => _main.SelectedTab;

    // ----- Handlers -----

    private async Task<string?> Open(string[] args, string rest)
    {
        if (args.Length == 0)
        {
            await _main.OpenCommand.ExecuteAsync(null);
            return null;
        }
        int opened = 0;
        foreach (var token in args)
        {
            string path = _quickmarks.Resolve(token) ?? token;
            if (!File.Exists(path)) return $"Not found: {token}";
            await _main.OpenPathAsync(path);
            opened++;
        }
        return opened == 1 ? null : $"Opened {opened} files";
    }

    private async Task<string?> OpenNewTab(string[] args, string rest)
    {
        if (args.Length == 0)
        {
            await _main.OpenNewTabCommand.ExecuteAsync(null);
            return null;
        }
        int opened = 0;
        foreach (var token in args)
        {
            string path = _quickmarks.Resolve(token) ?? token;
            if (!File.Exists(path)) return $"Not found: {token}";
            await _main.OpenPathAsync(path, forceNewTab: true);
            opened++;
        }
        return opened == 1 ? null : $"Opened {opened} files";
    }

    private Task<string?> BmDel(string[] args, string rest)
    {
        var doc = Doc;
        if (doc is null) return Msg("No document");
        if (string.IsNullOrWhiteSpace(rest)) return Msg("Usage: bmdel <name>");
        return Msg(doc.RemoveUserBookmark(rest) ? $"Removed bookmark '{rest}'" : $"No bookmark: {rest}");
    }

    private Task<string?> Page(string[] args, string rest)
    {
        var doc = Doc;
        if (doc is null) return Msg("No document");
        if (args.Length == 0) return Msg($"Page {doc.CurrentPage} of {doc.Pages.Count}");

        string a = args[0].ToLowerInvariant();
        int target = a switch
        {
            "first" or "home" => 1,
            "last" or "end" => doc.Pages.Count,
            "next" => doc.CurrentPage + 1,
            "prev" or "previous" => doc.CurrentPage - 1,
            _ => int.TryParse(a, out int n) ? n : -1,
        };
        if (target < 1) return Msg($"Invalid page: {args[0]}");
        return Msg(doc.GoToPage(target) ? null : $"Page out of range (1-{doc.Pages.Count})");
    }

    private Task<string?> View(string[] args, string rest)
    {
        var doc = Doc;
        if (doc is null) return Msg("No document");
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "scroll";
        int? count = null;
        if (args.Length > 1 && int.TryParse(args[1], out int n)) count = n;
        else if (args.Length == 1 && int.TryParse(args[0], out int n2)) { mode = "scroll"; count = n2; }
        if (mode is not ("scroll" or "full" or "scrollh" or "book"))
            return Msg("Usage: view <scroll|full|scrollh> [pages]");
        return Msg(doc.SetView(mode, count));
    }

    private Task<string?> Zoom(string[] args, string rest)
    {
        var doc = Doc;
        if (doc is null) return Msg("No document");
        if (args.Length == 0) return Msg($"Zoom {doc.ZoomPercent}%");

        string a = args[0].ToLowerInvariant().TrimEnd('%');
        switch (a)
        {
            case "in": doc.ZoomInCommand.Execute(null); break;
            case "out": doc.ZoomOutCommand.Execute(null); break;
            case "reset": doc.ZoomResetCommand.Execute(null); break;
            case "width": doc.RequestFitWidth(); return Msg("Fit width");
            case "page" or "fit": doc.RequestFitPage(); return Msg("Fit page");
            default:
                if (!double.TryParse(a, out double pct)) return Msg($"Invalid zoom: {args[0]}");
                doc.SetZoomPercent(pct);
                break;
        }
        return Msg($"Zoom {doc.ZoomPercent}%");
    }

    private Task<string?> Find(string[] args, string rest)
    {
        var doc = Doc;
        if (doc is null) return Msg("No document");
        if (string.IsNullOrWhiteSpace(rest)) return Msg("Usage: find <text>");
        int count = doc.Find(rest);
        return Msg(count == 0 ? $"No matches for \"{rest}\""
                              : $"Match {doc.FindCurrentOrdinal}/{count} for \"{rest}\"");
    }

    private string? FindStep(int dir)
    {
        var doc = Doc;
        if (doc is null) return "No document";
        if (doc.FindMatchCount == 0) return "No active search";
        if (dir > 0) doc.FindNext(); else doc.FindPrev();
        return $"Match {doc.FindCurrentOrdinal}/{doc.FindMatchCount}";
    }

    private Task<string?> Highlight(string[] args, string rest)
    {
        var doc = Doc;
        if (doc is null) return Msg("No document");
        if (doc.HighlightActiveSelection(AnnotColor.Yellow))
            return Msg("Highlighted selection");
        doc.SelectTool(EditorTool.Highlight);
        return Msg("Highlight tool — drag over text");
    }

    private string? SetTool(EditorTool tool)
    {
        var doc = Doc;
        if (doc is null) return "No document";
        doc.SelectTool(tool);
        return tool.ToString();
    }

    private async Task<string?> Print(string[] args, string rest)
    {
        var doc = Doc;
        if (doc is null) return "No document";
        string spec = string.IsNullOrWhiteSpace(rest) ? $"1-{doc.PageCount}" : rest;
        string temp = Path.Combine(Path.GetTempPath(), $"nopdf_print_{Guid.NewGuid():N}.pdf");
        await Task.Run(() => doc.ExtractRange(spec, temp));

        try
        {
            Process.Start(new ProcessStartInfo(temp) { Verb = "print", UseShellExecute = true });
            return "Printing…";
        }
        catch (Exception ex)
        {
            return $"Exported to {temp} (print failed: {ex.Message})";
        }
    }

    private string? TogglePages()
    {
        _main.IsThumbnailsPanelOpen = !_main.IsThumbnailsPanelOpen;
        return _main.IsThumbnailsPanelOpen ? "Pages panel shown" : "Pages panel hidden";
    }

    private Task<string?> Rotate(string[] args, string rest)
    {
        var doc = Doc;
        if (doc is null) return Msg("No document");
        // Last token may be a direction; the rest is the range.
        int delta = 90;
        string range = rest;
        if (args.Length > 0)
        {
            string last = args[^1].ToLowerInvariant();
            if (last is "cw" or "ccw" or "180" or "left" or "right")
            {
                delta = last switch { "ccw" or "left" => -90, "180" => 180, _ => 90 };
                range = string.Join(' ', args.Take(args.Length - 1));
            }
        }
        var idx = string.IsNullOrWhiteSpace(range)
            ? new List<int> { doc.CurrentPage - 1 }
            : PageOps.ParseRange(range, doc.PageCount).ToList();
        if (idx.Count == 0) return Msg($"No pages in range: {range}");
        doc.RotatePages(idx, delta);
        return Msg($"Rotated {idx.Count} page(s)");
    }

    private Task<string?> Delete(string[] args, string rest)
    {
        var doc = Doc;
        if (doc is null) return Msg("No document");
        var idx = string.IsNullOrWhiteSpace(rest)
            ? new List<int> { doc.CurrentPage - 1 }
            : PageOps.ParseRange(rest, doc.PageCount).ToList();
        if (idx.Count == 0) return Msg($"No pages in range: {rest}");
        if (idx.Count >= doc.PageCount) return Msg("Cannot delete every page");
        doc.DeletePages(idx);
        return Msg($"Deleted {idx.Count} page(s)");
    }

    private async Task<string?> Extract(string[] args, string rest)
    {
        var doc = Doc;
        if (doc is null) return "No document";
        if (args.Length == 0) return "Usage: extract <range> [path]";
        string range = args[0];
        string? path = args.Length >= 2 ? string.Join(' ', args.Skip(1)) : null;
        if (path is null)
        {
            string dir = Path.GetDirectoryName(doc.FilePath) ?? "";
            string name = $"{Path.GetFileNameWithoutExtension(doc.FilePath)}_p{range.Replace(',', '-')}.pdf";
            path = await _main.PickSaveAs(dir, name);
            if (path is null) return "Cancelled";
        }
        await Task.Run(() => doc.ExtractRange(range, path));
        return $"Extracted {range} → {path}";
    }

    private async Task<string?> Insert(string[] args, string rest)
    {
        var doc = Doc;
        if (doc is null) return "No document";
        string? path = args.Length > 0 && File.Exists(args[0]) ? args[0]
            : _main.OpenSingleFilePicker is not null ? await _main.OpenSingleFilePicker() : null;
        if (path is null || !File.Exists(path)) return "No file to insert";
        int at = args.Length > 1 && int.TryParse(args[1], out int a) ? a - 1 : doc.CurrentPage;
        doc.InsertFile(path, at);
        return $"Inserted {Path.GetFileName(path)}";
    }

    private async Task<string?> Merge(string[] args, string rest)
    {
        var doc = Doc;
        if (doc is null) return "No document";
        string? path = args.Length > 0 && File.Exists(args[0]) ? args[0]
            : _main.OpenSingleFilePicker is not null ? await _main.OpenSingleFilePicker() : null;
        if (path is null || !File.Exists(path)) return "No file to merge";
        doc.MergeFile(path);
        return $"Merged {Path.GetFileName(path)}";
    }

    private string? DoUndo() { Doc?.Undo(); return Doc is null ? "No document" : "Undo"; }
    private string? DoRedo() { Doc?.Redo(); return Doc is null ? "No document" : "Redo"; }

    private string? Fit(bool widthOnly)
    {
        if (Doc is null) return "No document";
        if (widthOnly) Doc.RequestFitWidth(); else Doc.RequestFitPage();
        return widthOnly ? "Fit width" : "Fit page";
    }

    private Task<string?> Scroll(int dx, int dy)
    {
        double step = Math.Max(1, _main.Config.ScrollRows) * 22.0;
        Doc?.ScrollBy(dx * step, dy * step);
        return Msg(null);
    }

    private async Task<string?> Save(string[] args, string rest)
    {
        var doc = Doc;
        if (doc is null) return "No document";
        string? dest = args.Length > 0 ? rest : null;
        await doc.SaveAsync(dest);
        return dest is null ? "Saved" : $"Saved to {dest}";
    }

    private async Task<string?> SaveAs(string[] args, string rest)
    {
        var doc = Doc;
        if (doc is null) return "No document";
        string? dest = rest.Length > 0 ? rest : null;
        if (dest is null)
        {
            string dir = Path.GetDirectoryName(doc.FilePath) ?? "";
            string name = Path.GetFileName(doc.FilePath);
            dest = await _main.PickSaveAs(dir, name);
            if (dest is null) return "Cancelled";
        }
        await doc.SaveAsync(dest);
        return $"Saved to {dest}";
    }

    private Task<string?> Close(string[] args, string rest)
    {
        _main.CloseTabCommand.Execute(null);
        return Msg(null);
    }

    private Task<string?> Quit(string[] args, string rest)
    {
        _main.RequestQuit();
        return Msg(null);
    }

    private Task<string?> Mark(string[] args, string rest)
    {
        var doc = Doc;
        if (doc is null) return Msg("No document to mark");
        if (args.Length == 0) return Msg("Usage: m <name>");
        _quickmarks.Set(args[0], doc.FilePath);
        return Msg($"Quickmark '{args[0]}' → {doc.Title}");
    }

    private Task<string?> DelMark(string[] args, string rest)
    {
        if (args.Length == 0) return Msg("Usage: delmark <name>");
        return Msg(_quickmarks.Remove(args[0]) ? $"Removed '{args[0]}'" : $"No such mark: {args[0]}");
    }

    private async Task<string?> Go(string[] args, string rest)
    {
        if (args.Length == 0) return "Usage: go <name>";
        string? path = _quickmarks.Resolve(args[0]);
        if (path is null) return $"No such quickmark: {args[0]}";
        if (!File.Exists(path)) return $"File missing: {path}";
        await _main.OpenPathAsync(path);
        return null;
    }

    /// <summary>Activates the signature tool, optionally selecting a preset by alias.</summary>
    private Task<string?> Sign(string[] args, string rest)
    {
        if (args.Length > 0 && !_main.SelectSignaturePresetByAlias(args[0]))
            return Msg($"No signature alias: {args[0]}");
        return Task.FromResult(SetTool(EditorTool.Signature));
    }

    private string? ListSignatures()
    {
        var doc = Doc;
        if (doc is null) return "No document";
        var sigs = doc.AllAnnotations().OfType<SignatureAnnotation>().ToList();
        int embedded = 0;
        try { embedded = doc.Document?.GetSignatureCount() ?? 0; } catch { }
        if (sigs.Count == 0 && embedded == 0) return "No signatures";
        var parts = new List<string>();
        foreach (var s in sigs) parts.Add($"{s.SignerName} ({s.Signed:yyyy-MM-dd HH:mm})");
        if (embedded > 0) parts.Add($"{embedded} embedded digital signature(s)");
        return "Signatures: " + string.Join("; ", parts);
    }

    private Task<string?> Config()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                NoPdf.App.Config.AppConfig.ConfigPath) { UseShellExecute = true });
            return Msg("Opened config");
        }
        catch (Exception ex) { return Msg($"Couldn't open config: {ex.Message}"); }
    }

    private Task<string?> Bind(string[] args, string rest)
    {
        if (args.Length < 2)
            return Msg("Usage: bind <key> <command>   (alias: bind :w save)");
        string key = args[0];
        string command = string.Join(' ', args.Skip(1));
        if (key.StartsWith(':'))
        {
            string alias = key.TrimStart(':');
            _main.AddAlias(alias, command);
            return Msg($"Alias :{alias} → {command}");
        }
        _main.AddBinding(key, command);
        return Msg($"Bound {key} → {command}");
    }

    private async Task<string?> Session(string[] args, string rest)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        string name = args.Length > 1 ? string.Join(' ', args.Skip(1)) : "";
        switch (sub)
        {
            case "save" or "s":
                if (name.Length == 0) return "Usage: session save <name>";
                _main.SaveNamedSession(name);
                return $"Saved session '{name}' ({_main.Tabs.Count} tabs)";
            case "load" or "l" or "open":
                if (name.Length == 0) return "Usage: session load <name>";
                if (_main.Session.GetNamed(name) is null) return $"No session: {name}";
                await _main.LoadNamedSessionAsync(name);
                return $"Loaded session '{name}'";
            case "del" or "delete" or "rm":
                if (name.Length == 0) return "Usage: session del <name>";
                return _main.Session.DeleteNamed(name) ? $"Deleted session '{name}'" : $"No session: {name}";
            case "list":
                var names = _main.Session.NamedSessions();
                return names.Count == 0 ? "No saved sessions" : "Sessions: " + string.Join(", ", names);
            default:
                return "Usage: session <save|load|del|list> [name]";
        }
    }

    private Task<string?> Bookmark(string[] args, string rest)
    {
        var doc = Doc;
        if (doc is null) return Msg("No document");
        if (string.IsNullOrWhiteSpace(rest)) return Msg("Usage: bookmark <name>");
        doc.AddUserBookmark(rest);
        _main.IsBookmarksPanelOpen = true;
        return Msg($"Bookmarked page {doc.CurrentPage} as '{rest}'");
    }

    private string? ToggleToc()
    {
        _main.IsBookmarksPanelOpen = !_main.IsBookmarksPanelOpen;
        return _main.IsBookmarksPanelOpen ? "Bookmarks panel shown" : "Bookmarks panel hidden";
    }

    private string? OpenMarks()
    {
        if (_quickmarks.All.Count == 0) return "No quickmarks";
        _main.CommandBar.OpenWithSuggestions(":", _quickmarks.All.Select(kv => $"o {kv.Value}"));
        return null;
    }

    /// <summary>Builds a help PDF (commands + hotkeys) and opens it in a tab.</summary>
    private async Task<string?> Help()
    {
        var lines = new List<NoPdf.Core.Import.TextDocument.Line>
        {
            new("Press ':' for the command line, '/' to search, 'f' to follow links.", false),
            new("", false),
            new("Commands", true),
        };
        foreach (var name in CommandNames())
            lines.Add(new(UsageText.TryGetValue(name, out var u) ? "  " + u : "  " + name));

        lines.Add(new("", false));
        lines.Add(new("Hotkeys", true));
        foreach (var kv in _main.Config.NormalBindings.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            lines.Add(new($"  {kv.Key,-12} {kv.Value}"));

        if (_main.Config.Aliases.Count > 0)
        {
            lines.Add(new("", false));
            lines.Add(new("Aliases", true));
            foreach (var kv in _main.Config.Aliases.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                lines.Add(new($"  {kv.Key,-12} {kv.Value}"));
        }

        try
        {
            var bytes = NoPdf.Core.Import.TextDocument.Build($"noPDF {NoPdf.App.AppVersion.Informational} — help", lines);
            string path = Path.Combine(Path.GetTempPath(), "noPDF-help.pdf");
            await File.WriteAllBytesAsync(path, bytes);
            await _main.OpenPathAsync(path);  // reuses the tab if help is already open
            return null;
        }
        catch (Exception ex) { return "Help failed: " + ex.Message; }
    }

    // ----- Usage hints (shown in the status bar as a command word is typed) -----

    private static readonly Dictionary<string, string> UsageText = new(StringComparer.OrdinalIgnoreCase)
    {
        ["open"] = "open <path|mark>… — open file(s), reuse an open tab",
        ["O"] = "O <path> — open in a new tab",
        ["tabnew"] = "tabnew [path] — open a new tab",
        ["page"] = "page <n|first|last|next|prev> — go to page",
        ["zoom"] = "zoom <pct|in|out|reset|width|page>",
        ["find"] = "find <text> — search all pages",
        ["highlight"] = "highlight — highlight the selection",
        ["rotate"] = "rotate <range> [cw|ccw|180]",
        ["delete"] = "delete <range> — delete pages",
        ["insert"] = "insert <path> [at] — insert a PDF",
        ["merge"] = "merge <path> — append a PDF",
        ["extract"] = "extract <range> [path] — export pages (dialog if no path)",
        ["save"] = "save [path] — write annotations",
        ["saveas"] = "saveas [path] — save a copy (dialog if no path)",
        ["print"] = "print [range]",
        ["m"] = "m <name> — quickmark the current file",
        ["go"] = "go <name> — open a quickmark",
        ["bookmark"] = "bookmark <name> — bookmark the current page",
        ["bmdel"] = "bmdel <name> — remove a page bookmark",
        ["session"] = "session <save|load|del|list> [name]",
        ["copypath"] = "copypath — copy the file's full path",
        ["marks"] = "marks — pick a quickmark to open",
        ["toolbar"] = "toolbar — toggle the icon toolbar",
        ["props"] = "props — toggle the annotation panel",
        ["toc"] = "toc — toggle the bookmarks panel",
        ["pages"] = "pages — toggle the thumbnails panel",
    };

    public string? Usage(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (UsageText.TryGetValue(name, out var u)) return u;
        return _commands.ContainsKey(name) ? name : null;
    }

    private static Task<string?> Msg(string? m) => Task.FromResult(m);
}
