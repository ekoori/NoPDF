using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NoPdf.App.Commands;

/// <summary>One documented command: its group, alternate names, argument syntax and
/// what it does. This is the single source for :help and the config file's comments.</summary>
public sealed record CommandDoc(string Name, string Group, string Args, string Description, params string[] Aliases)
{
    /// <summary>"open &lt;path&gt;" — the name plus its argument syntax.</summary>
    public string Syntax => Args.Length > 0 ? $"{Name} {Args}" : Name;
    /// <summary>The one-line hint shown in the status bar while typing.</summary>
    public string UsageLine => $"{Syntax} — {Description}";
}

/// <summary>The command catalogue, grouped like an application's menus.</summary>
public static class CommandDocs
{
    // Groups are listed in menu order.
    public static readonly string[] Groups =
        { "File", "Tabs", "Navigation", "Search", "View", "Pages", "Annotations", "Signatures", "Panels", "Options", "Help" };

    public static readonly IReadOnlyList<CommandDoc> All = new[]
    {
        // ----- File -----
        new CommandDoc("open", "File", "<path|mark>…", "Open file(s) — the first replaces the current tab", "o"),
        new CommandDoc("O", "File", "<path|mark>…", "Open file(s) in a new tab", "opentab", "tabnew"),
        new CommandDoc("save", "File", "[path]", "Write annotations back to the file", "w"),
        new CommandDoc("saveas", "File", "[path]", "Save a copy (asks if no path given)"),
        new CommandDoc("reload", "File", "", "Re-read the file from disk, discarding edits"),
        new CommandDoc("print", "File", "[preset] [range]",
            "Print with a saved preset (or the default one) — no dialog"),
        new CommandDoc("printdialog", "File", "[range]",
            "Print via a dialog; can save the choices as a named preset"),
        new CommandDoc("printpreset", "File", "[list|default <name>|del <name>]",
            "List print presets, or pick/remove the default"),
        new CommandDoc("copypath", "File", "", "Copy the document's full path to the clipboard"),
        new CommandDoc("session", "File", "<save|load|del|list> [name]", "Save or restore the set of open tabs", "sess"),
        new CommandDoc("close", "File", "", "Close the current tab", "q"),
        new CommandDoc("quit", "File", "", "Close noPDF", "qa"),

        // ----- Tabs -----
        new CommandDoc("tabnext", "Tabs", "", "Go to the next tab"),
        new CommandDoc("tabprev", "Tabs", "", "Go to the previous tab"),
        new CommandDoc("tabclose", "Tabs", "", "Close the current tab"),
        new CommandDoc("reopen", "Tabs", "", "Reopen the most recently closed tab"),

        // ----- Navigation -----
        new CommandDoc("page", "Navigation", "<n|first|last|next|prev>", "Go to a page", "p", "goto"),
        new CommandDoc("scrolldown", "Navigation", "", "Scroll down"),
        new CommandDoc("scrollup", "Navigation", "", "Scroll up"),
        new CommandDoc("scrollleft", "Navigation", "", "Scroll left"),
        new CommandDoc("scrollright", "Navigation", "", "Scroll right"),
        new CommandDoc("scrollpagedown", "Navigation", "", "Scroll down one screen (next spread in full view)"),
        new CommandDoc("scrollpageup", "Navigation", "", "Scroll up one screen (previous spread in full view)"),
        new CommandDoc("hint", "Navigation", "",
            "Label the links and form fields on screen; type a label to follow or fill one", "follow"),
        new CommandDoc("bookmark", "Navigation", "<name>", "Bookmark the current page (saved in the PDF)", "bm"),
        new CommandDoc("bmdel", "Navigation", "<name>", "Remove a bookmark from the PDF"),
        new CommandDoc("m", "Navigation", "<name>", "Quickmark the current file", "mark"),
        new CommandDoc("go", "Navigation", "<name>", "Open a quickmark", "gm"),
        new CommandDoc("delmark", "Navigation", "<name>", "Delete a quickmark", "dm"),
        new CommandDoc("marks", "Navigation", "", "Pick a quickmark to open"),

        // ----- Search -----
        new CommandDoc("find", "Search", "<text>", "Search all pages and jump to the first match", "f", "search"),
        new CommandDoc("findnext", "Search", "", "Jump to the next match", "n"),
        new CommandDoc("findprev", "Search", "", "Jump to the previous match", "N"),

        // ----- View -----
        new CommandDoc("view", "View", "<scroll|full|scrollh> [pages]",
            "Layout: scroll = vertical (N across), full = only the page(s) in focus, scrollh = horizontal (N rows)"),
        new CommandDoc("zoom", "View", "<pct|in|out|reset|width|page>", "Set the zoom level", "z"),
        new CommandDoc("fit", "View", "", "Fit the whole page in the window"),
        new CommandDoc("fitwidth", "View", "", "Fit the page width to the window"),

        // ----- Pages -----
        new CommandDoc("rotate", "Pages", "<range> [cw|ccw|180]", "Rotate pages"),
        new CommandDoc("delete", "Pages", "<range>", "Delete pages", "del"),
        new CommandDoc("insert", "Pages", "<path> [at]", "Insert another PDF's pages"),
        new CommandDoc("merge", "Pages", "<path>", "Append another PDF"),
        new CommandDoc("extract", "Pages", "<range> [path]", "Export pages to a new PDF (asks if no path)"),

        // ----- Annotations -----
        new CommandDoc("select", "Annotations", "", "Select tool: pick text or annotations (default)", "sel"),
        new CommandDoc("hand", "Annotations", "",
            "View tool: drag to pan, click links, fill in form fields"),
        new CommandDoc("highlight", "Annotations", "", "Highlight the selected text", "hl"),
        new CommandDoc("note", "Annotations", "", "Sticky-note tool"),
        new CommandDoc("textbox", "Annotations", "", "Free-text box tool", "tb"),
        new CommandDoc("callout", "Annotations", "", "Callout (text with a leader line) tool"),
        new CommandDoc("line", "Annotations", "", "Line tool"),
        new CommandDoc("arrow", "Annotations", "", "Arrow tool"),
        new CommandDoc("rect", "Annotations", "", "Rectangle tool", "box", "rectangle"),
        new CommandDoc("polyline", "Annotations", "", "Polyline tool", "poly"),
        new CommandDoc("delannot", "Annotations", "", "Delete the selected annotation(s)"),
        new CommandDoc("group", "Annotations", "", "Group the selected annotations"),
        new CommandDoc("ungroup", "Annotations", "", "Ungroup one level of the selection"),
        new CommandDoc("yank", "Annotations", "", "Copy the selected annotation(s)"),
        new CommandDoc("paste", "Annotations", "", "Paste annotation(s) onto the current page"),
        new CommandDoc("copy", "Annotations", "", "Copy the selected text"),
        new CommandDoc("undo", "Annotations", "", "Undo the last change"),
        new CommandDoc("redo", "Annotations", "", "Redo the last undone change"),

        // ----- Signatures -----
        new CommandDoc("sign", "Signatures", "[alias]", "Signature tool, optionally selecting a preset", "signature"),
        new CommandDoc("siglist", "Signatures", "",
            "Verify this document's digital signatures and show them in the signatures panel"),

        // ----- Panels -----
        new CommandDoc("toc", "Panels", "", "Toggle the bookmarks panel", "b"),
        new CommandDoc("pages", "Panels", "", "Toggle the page thumbnails panel"),
        new CommandDoc("props", "Panels", "", "Toggle the annotation properties panel", "annot"),
        new CommandDoc("annots", "Panels", "", "Toggle the annotations list panel", "annotations"),
        new CommandDoc("signatures", "Panels", "", "Toggle the signature presets panel"),
        new CommandDoc("toolbar", "Panels", "", "Toggle the icon toolbar"),
        new CommandDoc("tabspanel", "Panels", "[top|bottom|left|right|on|off] [n]",
            "Toggle the tabs panel, or place it around the view", "showtabs"),

        // ----- Options -----
        new CommandDoc("config", "Options", "", "Open the config file in your editor"),
        new CommandDoc("bind", "Options", "<key> <command>", "Bind a key (\":cmd\" pre-fills the command line)"),

        // ----- Help -----
        new CommandDoc("help", "Help", "", "Open this help document in a tab"),
        new CommandDoc("version", "Help", "", "Show the noPDF version"),
    };

    private static readonly Dictionary<string, CommandDoc> ByName = Build();

    private static Dictionary<string, CommandDoc> Build()
    {
        var d = new Dictionary<string, CommandDoc>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in All)
        {
            d[c.Name] = c;
            foreach (var a in c.Aliases) d.TryAdd(a, c);
        }
        return d;
    }

    /// <summary>The documentation for a command or one of its aliases.</summary>
    public static CommandDoc? Find(string name)
        => name is not null && ByName.TryGetValue(name, out var c) ? c : null;

    public static IEnumerable<CommandDoc> InGroup(string group)
        => All.Where(c => c.Group == group);

    /// <summary>Renders the catalogue as commented lines for the config file.</summary>
    public static string ConfigComment()
    {
        var sb = new StringBuilder();
        foreach (var g in Groups)
        {
            var items = InGroup(g).ToList();
            if (items.Count == 0) continue;
            sb.Append("#\n#   ").Append(g.ToUpperInvariant()).Append('\n');
            foreach (var c in items)
            {
                string alias = c.Aliases.Length > 0 ? $"  (aka {string.Join(", ", c.Aliases)})" : "";
                // Pad to a column, but never let a long syntax run straight into its
                // description — keep at least a gap.
                const int col = 38;
                string syntax = c.Syntax.Length < col ? c.Syntax.PadRight(col) : c.Syntax + "  ";
                sb.Append("#     ").Append(syntax).Append(c.Description).Append(alias).Append('\n');
            }
        }
        return sb.ToString();
    }
}
