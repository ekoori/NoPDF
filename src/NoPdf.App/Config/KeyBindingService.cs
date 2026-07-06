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
        foreach (var kv in config.NormalBindings) _bindings[kv.Key] = kv.Value;
        foreach (var kv in config.SearchBindings) _bindings.TryAdd(kv.Key, kv.Value);
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
