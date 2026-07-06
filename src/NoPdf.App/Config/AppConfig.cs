using System;
using System.Collections.Generic;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NoPdf.App.Config;

/// <summary>
/// User configuration loaded from <c>%APPDATA%/NoPdf/config.yaml</c>. Maps NoPdf's
/// built-in commands to vim/qutebrowser-style key bindings and holds a few app
/// parameters. A commented default file is written on first run.
/// </summary>
public sealed class AppConfig
{
    public string Theme { get; set; } = "dark";
    public int CommandHistorySize { get; set; } = 200;

    /// <summary>Normal-mode multi-key hotkeys: key sequence → command line (as typed after ':').</summary>
    public Dictionary<string, string> NormalBindings { get; set; } = new();

    /// <summary>Keys active during/after a search: key → command line.</summary>
    public Dictionary<string, string> SearchBindings { get; set; } = new();

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

    /// <summary>Loads config, writing the commented default if none exists.</summary>
    public static AppConfig Load(out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(ConfigPath))
                File.WriteAllText(ConfigPath, DefaultYaml);

            var yaml = File.ReadAllText(ConfigPath);
            var config = Parse(yaml);
            // Merge in any default bindings the user hasn't overridden.
            var defaults = Parse(DefaultYaml);
            foreach (var kv in defaults.NormalBindings)
                config.NormalBindings.TryAdd(kv.Key, kv.Value);
            foreach (var kv in defaults.SearchBindings)
                config.SearchBindings.TryAdd(kv.Key, kv.Value);
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

    public const string DefaultYaml = """
# ── NoPdf configuration ───────────────────────────────────────────────
# Edit this file and restart NoPdf. Binding values are command lines,
# exactly what you'd type after ":" (see :help for the full list).
#
# There are three ways to run a command:
#   :cmd   — type ":" to open the command line, then the command
#   xy     — a "normal-mode" multi-key hotkey (no ":" needed)
#   /text  — "/" opens search; then n / N jump to next / previous match
#
# While typing a multi-key hotkey, matching bindings are shown above the
# command bar until the sequence completes.

theme: dark               # dark | light
command_history_size: 200

# Multi-key hotkeys (pressed without ":") → command line.
normal_bindings:
  hl: highlight           # highlight the current selection
  gg: page first
  G: page last
  J: page next
  K: page prev
  T: tabnew               # open a file in a new, focused tab
  X: tabclose
  gt: tabnext
  gT: tabprev
  u: undo
  U: redo
  zi: zoom in
  zo: zoom out
  zz: zoom reset
  zw: zoom width
  zp: zoom page
  b: toc                  # toggle bookmarks panel
  P: pages                # toggle pages/thumbnails panel
  yy: copy                # copy the current selection

# Keys used with search ("/"): jump between matches.
search_bindings:
  n: findnext
  N: findprev
""";
}
