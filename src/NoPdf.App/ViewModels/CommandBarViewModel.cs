using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NoPdf.App.ViewModels;

/// <summary>Backing state for the bottom command line (":" and "/" modes).</summary>
public sealed partial class CommandBarViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private readonly int _maxHistory;
    private readonly int _visibleCount;
    private readonly string _historyPath;
    private readonly List<string> _history = new();
    private int _historyPos;

    // Transient suggestion list (e.g. :marks → "o <file>" entries).
    private List<string>? _suggestions;
    private int _suggIndex;

    // Tab-completion of command names.
    private List<string>? _compMatches;
    private int _compIndex;
    private string _compPrefix = "";
    private bool _settingText;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _prompt = ":";
    [ObservableProperty] private string _text = "";

    public ObservableCollection<CmdHistoryItem> Visible { get; } = new();

    public event Action? Opened;
    public event Action? FocusRequested;

    /// <summary>Raised once the bar is done, so the view can hand keyboard focus back to
    /// the page — otherwise page keys like Delete go nowhere after a command.</summary>
    public event Action? Closed;

    public CommandBarViewModel(MainWindowViewModel main, int maxHistory, int visibleCount)
    {
        _main = main;
        _maxHistory = Math.Max(10, maxHistory);
        _visibleCount = Math.Clamp(visibleCount, 1, 12);
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NoPdf");
        Directory.CreateDirectory(dir);
        _historyPath = Path.Combine(dir, "cmd_history.txt");
        LoadHistory();
    }

    public void Open(string prompt, string initial = "")
    {
        _suggestions = null;
        _compMatches = null;
        Prompt = prompt;
        SetText(initial);
        _historyPos = _history.Count;
        BuildVisible();
        IsVisible = true;
        Opened?.Invoke();
    }

    /// <summary>Opens the bar showing a cycling list of candidate command lines.</summary>
    public void OpenWithSuggestions(string prompt, IEnumerable<string> suggestions)
    {
        _suggestions = suggestions.ToList();
        _compMatches = null;
        Prompt = prompt;
        _suggIndex = 0;
        SetText(_suggestions.Count > 0 ? _suggestions[0] : "");
        BuildVisible();
        IsVisible = true;
        Opened?.Invoke();
    }

    public void RequestFocus() => FocusRequested?.Invoke();

    public void Cancel()
    {
        IsVisible = false;
        _suggestions = null;
        _compMatches = null;
        SetText("");
        Closed?.Invoke();
    }

    public async Task ExecuteAsync()
    {
        string input = Text.Trim();
        IsVisible = false;
        _suggestions = null;
        _compMatches = null;
        SetText("");
        if (input.Length == 0) { Closed?.Invoke(); return; }

        string entry = Prompt == "/" ? "/" + input : input;
        RecordHistory(entry);

        string commandLine = Prompt == "/" ? "find " + input : input;
        string? result = await _main.Commands.ExecuteAsync(commandLine);
        if (result is not null) _main.StatusText = result;
        Closed?.Invoke(); // after the command, in case it re-opened the bar itself
    }

    // ----- Up/Down: cycle suggestions or history -----

    public void HistoryPrev()
    {
        if (_suggestions is { Count: > 0 }) { CycleSuggestion(-1); return; }
        if (_history.Count == 0) return;
        _historyPos = Math.Max(0, _historyPos - 1);
        SetFromHistory();
        BuildVisible();
    }

    public void HistoryNext()
    {
        if (_suggestions is { Count: > 0 }) { CycleSuggestion(+1); return; }
        if (_history.Count == 0) return;
        _historyPos = Math.Min(_history.Count, _historyPos + 1);
        if (_historyPos == _history.Count) { SetText(""); BuildVisible(); return; }
        SetFromHistory();
        BuildVisible();
    }

    private void CycleSuggestion(int dir)
    {
        _suggIndex = (_suggIndex + dir + _suggestions!.Count) % _suggestions.Count;
        SetText(_suggestions[_suggIndex]);
        BuildVisible();
    }

    // ----- Tab: complete command names -----

    public void CompleteNext() => Complete(+1);
    public void CompletePrev() => Complete(-1);

    private void Complete(int dir)
    {
        if (_compMatches is null)
        {
            _compPrefix = FirstWord(Text);
            _compMatches = _main.Commands.CommandNames()
                .Where(n => n.StartsWith(_compPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _compIndex = dir > 0 ? -1 : 0;
        }
        if (_compMatches.Count == 0) return;
        _compIndex = (_compIndex + dir + _compMatches.Count) % _compMatches.Count;
        string args = ArgsPart(Text);
        SetText(_compMatches[_compIndex] + (args.Length > 0 ? " " + args : ""));
    }

    // ----- Text change: reset completion + show usage -----

    partial void OnTextChanged(string value)
    {
        if (_settingText) return;
        _compMatches = null;           // manual typing cancels completion cycle
        var usage = _main.Commands.Usage(FirstWord(value));
        if (usage is not null) _main.StatusText = usage;
    }

    private void SetText(string value)
    {
        _settingText = true;
        Text = value;
        _settingText = false;
    }

    private static string FirstWord(string s)
    {
        s = s.TrimStart();
        int sp = s.IndexOf(' ');
        return sp < 0 ? s : s[..sp];
    }

    private static string ArgsPart(string s)
    {
        s = s.TrimStart();
        int sp = s.IndexOf(' ');
        return sp < 0 ? "" : s[(sp + 1)..];
    }

    // ----- History window display -----

    private void BuildVisible()
    {
        Visible.Clear();
        if (_suggestions is { Count: > 0 })
        {
            int n = Math.Min(_visibleCount, _suggestions.Count);
            int start = Math.Clamp(_suggIndex - n / 2, 0, Math.Max(0, _suggestions.Count - n));
            for (int i = start; i < start + n && i < _suggestions.Count; i++)
                Visible.Add(new CmdHistoryItem(_suggestions[i], i == _suggIndex));
            return;
        }
        int total = _history.Count;
        if (total == 0) return;
        int hn = Math.Min(_visibleCount, total);
        int cur = _historyPos;
        int hs = cur >= total ? Math.Max(0, total - hn) : Math.Clamp(cur - hn / 2, 0, Math.Max(0, total - hn));
        for (int i = hs; i < hs + hn && i < total; i++)
            Visible.Add(new CmdHistoryItem(_history[i], i == cur));
    }

    private void SetFromHistory()
    {
        string entry = _history[_historyPos];
        if (Prompt == "/" && entry.StartsWith('/')) entry = entry[1..];
        SetText(entry);
    }

    private void RecordHistory(string entry)
    {
        _history.RemoveAll(h => h == entry);
        _history.Add(entry);
        if (_history.Count > _maxHistory) _history.RemoveRange(0, _history.Count - _maxHistory);
        _historyPos = _history.Count;
        try { File.WriteAllLines(_historyPath, _history); } catch { }
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(_historyPath)) return;
            foreach (var line in File.ReadAllLines(_historyPath))
                if (!string.IsNullOrWhiteSpace(line)) _history.Add(line);
            if (_history.Count > _maxHistory) _history.RemoveRange(0, _history.Count - _maxHistory);
            _historyPos = _history.Count;
        }
        catch { }
    }
}

/// <summary>One history/suggestion line shown above the command input.</summary>
public sealed record CmdHistoryItem(string Text, bool Current);
