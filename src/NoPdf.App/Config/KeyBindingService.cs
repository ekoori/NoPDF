using System;
using System.Collections.Generic;
using System.Linq;

namespace NoPdf.App.Config;

public enum KeyFeedKind { None, Pending, Execute }

public sealed record KeyFeedResult(
    KeyFeedKind Kind,
    string? Command,
    IReadOnlyList<(string Seq, string Command)> Candidates,
    string Pending);

/// <summary>
/// Matches normal-mode multi-key hotkey sequences against the configured bindings,
/// qutebrowser-style. Feed one token per key press; a partial match returns the
/// candidate list (for the which-key hint) and keeps accumulating.
/// </summary>
public sealed class KeyBindingService
{
    private readonly Dictionary<string, string> _bindings = new(StringComparer.Ordinal);
    private string _pending = "";

    public KeyBindingService(AppConfig config)
    {
        foreach (var kv in config.NormalBindings) _bindings[Normalize(kv.Key)] = kv.Value;
        foreach (var kv in config.SearchBindings) _bindings.TryAdd(Normalize(kv.Key), kv.Value);
    }

    /// <summary>
    /// Canonicalizes a binding string so different notations for the same key all
    /// match the tokens produced at runtime, e.g. <c>&lt;Ctrl-R&gt;</c>,
    /// <c>&lt;ctrl+r&gt;</c> and <c>&lt;c-r&gt;</c> all become <c>&lt;c-r&gt;</c>.
    /// </summary>
    public static string Normalize(string binding)
    {
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < binding.Length)
        {
            if (binding[i] == '<')
            {
                int j = binding.IndexOf('>', i);
                if (j < 0) { sb.Append(binding[i..]); break; }
                sb.Append(NormalizeGroup(binding[(i + 1)..j]));
                i = j + 1;
            }
            else { sb.Append(binding[i]); i++; }
        }
        return sb.ToString();
    }

    private static string NormalizeGroup(string inner)
    {
        var parts = inner.Split(new[] { '-', '+' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "<>";
        bool c = false, a = false, s = false;
        for (int k = 0; k < parts.Length - 1; k++)
        {
            switch (parts[k].ToLowerInvariant())
            {
                case "c" or "ctrl" or "control": c = true; break;
                case "a" or "alt" or "option": a = true; break;
                case "s" or "shift": s = true; break;
            }
        }
        string key = parts[^1].ToLowerInvariant() switch
        {
            "escape" => "esc", "return" or "enter" => "cr",
            "backspace" => "bs", "delete" => "del", "pgup" => "pageup", "pgdn" => "pagedown",
            var v => v,
        };
        var mods = new List<string>(3);
        if (c) mods.Add("c");
        if (a) mods.Add("a");
        if (s) mods.Add("s");
        return mods.Count == 0 ? $"<{key}>" : $"<{string.Join('-', mods)}-{key}>";
    }

    public bool HasPending => _pending.Length > 0;
    public string Pending => _pending;
    public void Reset() => _pending = "";

    public KeyFeedResult Feed(string token)
    {
        string seq = _pending + token;
        bool exact = _bindings.TryGetValue(seq, out var cmd);
        var prefixed = _bindings.Where(kv => kv.Key.StartsWith(seq, StringComparison.Ordinal)).ToList();
        bool hasLonger = prefixed.Any(kv => kv.Key.Length > seq.Length);

        if (exact && !hasLonger)
        {
            _pending = "";
            return new(KeyFeedKind.Execute, cmd, Array.Empty<(string, string)>(), "");
        }
        if (hasLonger)
        {
            _pending = seq;
            var cands = prefixed
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();
            return new(KeyFeedKind.Pending, null, cands, seq);
        }
        if (exact)
        {
            _pending = "";
            return new(KeyFeedKind.Execute, cmd, Array.Empty<(string, string)>(), "");
        }
        _pending = "";
        return new(KeyFeedKind.None, null, Array.Empty<(string, string)>(), "");
    }
}
