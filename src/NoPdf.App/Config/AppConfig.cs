using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    public string Theme { get; set; } = "dark";        // dark | light | inherit
    public bool ShowToolbar { get; set; } = false;
    public bool ShowTitlebar { get; set; } = false;

    /// <summary>Name used on signatures; falls back to the OS user name if blank.</summary>
    public string UserName { get; set; } = "";

    /// <summary>Legacy global certificate; certificates are now set per signature
    /// in the signatures panel. Kept so old config files still parse.</summary>
    public string CertPath { get; set; } = "";
    public string CertPassword { get; set; } = "";
    public string SignerName => string.IsNullOrWhiteSpace(UserName) ? Environment.UserName : UserName;
    public int CommandHistorySize { get; set; } = 200;

    /// <summary>Number of history lines shown above the command line.</summary>
    public int HistoryVisible { get; set; } = 5;

    /// <summary>Tabs panel: "top|bottom &lt;rows&gt;" | "left|right &lt;peek-ms&gt;" | "on" | "off".</summary>
    public string Tabs { get; set; } = "top 3";

    /// <summary>Min/close buttons ride with the tabs panel instead of the top chrome.</summary>
    public bool TitleButtonsInTabs { get; set; }

    /// <summary>Parsed: position (top/bottom/left/right/off), rows (top/bottom),
    /// peek ms (left/right; -1 = always shown).</summary>
    public (string Pos, int Rows, int PeekMs) TabsParsed
    {
        get
        {
            var t = (Tabs ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0) return ("top", 3, 0);
            int Rows() => t.Length > 1 && int.TryParse(t[1], out var r) ? Math.Clamp(r, 1, 12) : 3;
            int Peek() => t.Length > 1 && int.TryParse(t[1], out var ms) ? ms : -1;
            return t[0] switch
            {
                "top" => ("top", Rows(), 0),
                "bottom" => ("bottom", Rows(), 0),
                "left" => ("left", 0, Peek()),
                "right" => ("right", 0, Peek()),
                "on" => ("left", 0, -1),
                "off" => ("off", 0, 0),
                _ => ("top", 3, 0),
            };
        }
    }

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

    /// <summary>
    /// Inserts or updates <c>key: value</c> under a top-level mapping (e.g.
    /// "normal_bindings" or "aliases") in the config file, preserving comments.
    /// </summary>
    public static void AddBindingToFile(string section, string key, string value)
    {
        try
        {
            var path = ConfigPath;
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            string yKey = key.All(c => char.IsLetterOrDigit(c)) ? key : $"\"{key}\"";
            string entry = $"  {yKey}: {value}";

            int header = lines.FindIndex(l =>
                l.TrimStart().StartsWith(section + ":") && !l.TrimStart().StartsWith("#"));
            if (header < 0)
            {
                lines.Add(section + ":");
                lines.Add(entry);
            }
            else
            {
                int existing = -1, insertAt = header + 1;
                for (int j = header + 1; j < lines.Count; j++)
                {
                    var l = lines[j];
                    if (l.Length > 0 && !char.IsWhiteSpace(l[0]) && !l.TrimStart().StartsWith("#")) break;
                    var t = l.Trim();
                    if (t.Length == 0 || t.StartsWith("#")) continue;
                    insertAt = j + 1;
                    var k = t.Split(':')[0].Trim().Trim('"');
                    if (k == key) { existing = j; break; }
                }
                if (existing >= 0) lines[existing] = entry;
                else lines.Insert(insertAt, entry);
            }
            File.WriteAllText(path, string.Join("\n", lines) + "\n");
        }
        catch { }
    }

    /// <summary>Sets a top-level scalar <c>key: value</c> in the config file, preserving comments.</summary>
    public static void SetScalar(string key, string value)
    {
        try
        {
            var path = ConfigPath;
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            int idx = lines.FindIndex(l =>
                l.TrimStart() == l && l.StartsWith(key + ":") && !l.StartsWith("#"));
            // keep any trailing comment on the line
            string entry = $"{key}: {value}";
            if (idx < 0) lines.Add(entry);
            else
            {
                int c = lines[idx].IndexOf('#');
                if (c > 0) entry += "  " + lines[idx][c..];
                lines[idx] = entry;
            }
            File.WriteAllText(path, string.Join("\n", lines) + "\n");
        }
        catch { }
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
# ── ALL COMMANDS ──────────────────────────────────────────────────────
# Every command is listed below (commented out). Uncomment a line under
# `normal_bindings:` in the form `key: command` to bind it to a key.
#
#   # Files / tabs
#   open <path>            open a file (reuses a tab for the same file)
#   O <path>  / tabnew      open in a NEW tab
#   tabnext                 next tab
#   tabprev                 previous tab
#   tabclose                close current tab
#   close                   close current tab
#   quit                    exit the app
#   copypath                copy the current file's full path
#
#   # Sessions (all tabs)
#   session save <name>     save the open tabs under a name
#   session load <name>     replace tabs with a saved session
#   session del <name>      delete a saved session
#   session list            list saved sessions
#
#   # Navigate
#   page <n|first|last|next|prev>
#   scrollup / scrolldown / scrollleft / scrollright
#   scrollpageup / scrollpagedown
#
#   # Zoom
#   zoom <pct|in|out|reset|width|page>
#
#   # Find
#   find <text> / findnext / findprev
#
#   # Tools
#   hand  select  highlight  note  textbox  callout  line  rect  arrow  polyline
#
#   # Edit
#   undo  redo  copy  delannot   (delannot = delete selected annotation)
#
#   # Pages
#   rotate <range> [cw|ccw|180]   delete <range>
#   insert <path> [at]   merge <path>   extract <range> <path>
#
#   # Panels / UI
#   toc        toggle bookmarks panel
#   pages      toggle thumbnails panel
#   props      toggle annotation properties panel
#   toolbar    toggle the icon toolbar
#
#   # Marks
#   m <name> / go <name>          file quickmarks
#   bookmark <name> / bmdel <name>  page bookmarks
#
#   # Misc
#   print [range]   save [path]   saveas <path>   help
# ──────────────────────────────────────────────────────────────────────

theme: dark               # dark | light | inherit (follow the OS)
show_toolbar: false       # the icon toolbar is hidden by default
show_titlebar: false      # hide the OS window title bar (min/max/close)
user_name: ""             # name printed on signatures (blank = OS user)
# Certificates are configured per signature in the signatures panel (:signatures).
cert_path: ""             # legacy global .pfx (unused)
cert_password: ""
command_history_size: 200
history_visible: 5        # history lines shown above the ":" line
tabs: top 3               # tabs panel: top|bottom <rows> | left|right <peek-ms> | on | off  (:tabspanel)
title_buttons_in_tabs: false  # false = min/close stay top-right; true = they ride with the tabs panel
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
  f: hint                 # show link hints; type the label to follow
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
  yy: copy                # copy the selected text
  YY: copypath            # copy the current file's full path

# Extra bindings (merged with normal_bindings).
search_bindings: {}
""";
}
