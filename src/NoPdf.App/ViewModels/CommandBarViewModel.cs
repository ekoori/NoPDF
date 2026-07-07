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

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _prompt = ":";
    [ObservableProperty] private string _text = "";

    /// <summary>A fixed window of history lines shown above the input (no scrollbar).</summary>
    public ObservableCollection<CmdHistoryItem> Visible { get; } = new();

    /// <summary>Raised when the bar opens so the view can focus/caret the input.</summary>
    public event Action? Opened;

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
        Prompt = prompt;
        Text = initial;
        _historyPos = _history.Count;
        BuildVisible();
        IsVisible = true;
        Opened?.Invoke();
    }

    /// <summary>Rebuilds the fixed-size window of history lines around the cursor.</summary>
    private void BuildVisible()
    {
        Visible.Clear();
        int total = _history.Count;
        if (total == 0) return;
        int n = Math.Min(_visibleCount, total);
        int cur = _historyPos; // total == "empty input"
        int start = cur >= total
            ? Math.Max(0, total - n)
            : Math.Clamp(cur - n / 2, 0, Math.Max(0, total - n));
        for (int i = start; i < start + n && i < total; i++)
            Visible.Add(new CmdHistoryItem(_history[i], i == cur));
    }

    public void Cancel()
    {
        IsVisible = false;
        Text = "";
    }

    public async Task ExecuteAsync()
    {
        string input = Text.Trim();
        IsVisible = false;
        Text = "";
        if (input.Length == 0) return;

        string entry = Prompt == "/" ? "/" + input : input;
        RecordHistory(entry);

        string commandLine = Prompt == "/" ? "find " + input : input;
        string? result = await _main.Commands.ExecuteAsync(commandLine);
        if (result is not null) _main.StatusText = result;
    }

    public void FillFromHistory(string entry) => Text = entry;

    public void HistoryPrev()
    {
        if (_history.Count == 0) return;
        _historyPos = Math.Max(0, _historyPos - 1);
        SetFromHistory();
        BuildVisible();
    }

    public void HistoryNext()
    {
        if (_history.Count == 0) return;
        _historyPos = Math.Min(_history.Count, _historyPos + 1);
        if (_historyPos == _history.Count) { Text = ""; BuildVisible(); return; }
        SetFromHistory();
        BuildVisible();
    }

    private void SetFromHistory()
    {
        string entry = _history[_historyPos];
        if (Prompt == "/" && entry.StartsWith('/')) entry = entry[1..];
        Text = entry;
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

/// <summary>One history line shown above the command input.</summary>
public sealed record CmdHistoryItem(string Text, bool Current);
