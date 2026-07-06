using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NoPdf.Core.Annotations;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NoPdf.App.Config;

/// <summary>
/// User configuration loaded from <c>%APPDATA%/NoPdf/config.yaml</c>. Maps NoPdf's
/// built-in commands to vim/qutebrowser-style key bindings and holds app
/// parameters. A commented default file (listing every command) is written on
/// first run.
/// </summary>
public sealed class AppConfig
{
    public string Theme { get; set; } = "dark";
    public int CommandHistorySize { get; set; } = 200;

    /// <summary>Lines scrolled by scrollup/scrolldown (j/k, arrows).</summary>
    public int ScrollRows { get; set; } = 3;

    // Text-box annotation defaults.
    public double TextboxFontSize { get; set; } = 14;
    public string TextboxFrameColor { get; set; } = "#1E6EDC";
    public double TextboxFrameOpacity { get; set; } = 1.0;

    /// <summary>Command aliases: alias → command line (e.g. w → save).</summary>
    public Dictionary<string, string> Aliases { get; set; } = new();

    /// <summary>Normal-mode multi-key hotkeys: key sequence → command line.</summary>
    public Dictionary<string, string> NormalBindings { get; set; } = new();

    /// <summary>Extra bindings (merged with normal); kept for grouping search keys.</summary>
    public Dictionary<string, string> SearchBindings { get; set; } = new();

    public AnnotColor TextboxFrameColorValue => ParseColor(TextboxFrameColor, AnnotColor.Blue);

    public static string ConfigPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NoPdf");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "config.yaml");
        }
    }

    public static AppConfig Load(out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(ConfigPath))
                File.WriteAllText(ConfigPath, DefaultYaml);

            var config = Parse(File.ReadAllText(ConfigPath));
            var defaults = Parse(DefaultYaml);
            foreach (var kv in defaults.NormalBindings) config.NormalBindings.TryAdd(kv.Key, kv.Value);
            foreach (var kv in defaults.SearchBindings) config.SearchBindings.TryAdd(kv.Key, kv.Value);
            foreach (var kv in defaults.Aliases) config.Aliases.TryAdd(kv.Key, kv.Value);
            return config;
        }
        catch (Exception ex)
        {
            error = $"Config error ({ex.Message}); using defaults";
            return Parse(DefaultYaml);
        }
    }

    private static AppConfig Parse(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<AppConfig>(yaml) ?? new AppConfig();
    }

    private static AnnotColor ParseColor(string hex, AnnotColor fallback)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 6 &&
            byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
            byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
            byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            return new AnnotColor(r, g, b);
        return fallback;
    }

    public const string DefaultYaml = """
# ── NoPdf configuration ───────────────────────────────────────────────
# Edit and restart NoPdf. Binding values are command lines — exactly what
# you'd type after ":".
#
# Three ways to run a command:
#   :cmd     type ":" then the command
#   xy       a normal-mode multi-key hotkey (no ":")
#   /text    "/" opens search; n / N jump to next / previous match
#
# Key notation for bindings:
#   a  A            letter (A = shift+a)
#   <c-r>          Ctrl+R          <a-x> Alt+X      <c-s-p> Ctrl+Shift+P
#   <up> <down> <left> <right> <pageup> <pagedown> <home> <end>
#   <space> <tab> <cr> <esc> <bs> <del>
# Multi-key sequences just concatenate, e.g. "gg", "z w"→ zw, "<space>f".
#
# ── ALL COMMANDS (bind any of these to any key) ───────────────────────
#   Files/tabs: open <path>          open (reuses a tab for the same file)
#               O <path> / tabnew    open in a NEW tab
#               tabnext tabprev tabclose
#               close  quit
#   Navigate:   page <n|first|last|next|prev>
#               scrollup scrolldown scrollleft scrollright
#               scrollpageup scrollpagedown
#   Zoom:       zoom <pct|in|out|reset|width|page>
#   Find:       find <text>   findnext   findprev
#   Tools:      hand select highlight note textbox callout
#               line rect arrow polyline
#   Annotate:   delannot          delete the selected annotation
#   Edit:       undo  redo  copy
#   Pages:      rotate <range> [cw|ccw|180]   delete <range>
#               insert <path> [at]   merge <path>   extract <range> <path>
#   Panels:     toc (bookmarks)   pages (thumbnails)
#   Marks:      m <name> / go <name>   (file quickmarks)
#               bookmark <name> / bmdel <name>   (page bookmarks)
#   Misc:       print [range]   save [path]   saveas <path>   help
# ──────────────────────────────────────────────────────────────────────

theme: dark
command_history_size: 200
scroll_rows: 3

# Text-box annotation defaults.
textbox_font_size: 14
textbox_frame_color: "#1E6EDC"
textbox_frame_opacity: 1.0        # 0 = transparent frame, 1 = solid

# Command aliases (alias → command). Lets ":w" run "save", etc.
aliases:
  w: save
  wq: save
  q: close
  qa: quit
  e: open

# Normal-mode hotkeys (no ":").
normal_bindings:
  hl: highlight
  gg: page first
  G: page last
  j: scrolldown
  k: scrollup
  "<down>": scrolldown
  "<up>": scrollup
  "<left>": scrollleft
  "<right>": scrollright
  "<pagedown>": scrollpagedown
  "<pageup>": scrollpageup
  "<home>": page first
  "<end>": page last
  n: findnext
  N: findprev
  H: tabprev
  L: tabnext
  gt: tabnext
  gT: tabprev
  T: tabnew
  X: tabclose
  u: undo
  U: redo
  "<c-r>": redo
  "<c-z>": undo
  "<c-y>": redo
  "<c-o>": open
  "<c-s>": save
  "<c-w>": tabclose
  "<c-c>": copy
  d: delannot
  zi: zoom in
  zo: zoom out
  zz: zoom reset
  zw: zoom width
  zp: zoom page
  b: toc
  P: pages
  yy: copy

# Extra bindings (merged with normal_bindings).
search_bindings: {}
""";
}
