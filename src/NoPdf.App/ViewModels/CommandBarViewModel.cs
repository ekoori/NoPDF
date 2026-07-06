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
    private readonly string _historyPath;
    private readonly List<string> _history = new();
    private int _historyPos;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _prompt = ":";
    [ObservableProperty] private string _text = "";

    /// <summary>Previously executed command lines, most recent first (for display).</summary>
    public ObservableCollection<string> History { get; } = new();

    /// <summary>Raised when the bar opens so the view can focus/caret the input.</summary>
    public event Action? Opened;

    public CommandBarViewModel(MainWindowViewModel main, int maxHistory)
    {
        _main = main;
        _maxHistory = Math.Max(10, maxHistory);
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
        IsVisible = true;
        Opened?.Invoke();
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
    }

    public void HistoryNext()
    {
        if (_history.Count == 0) return;
        _historyPos = Math.Min(_history.Count, _historyPos + 1);
        if (_historyPos == _history.Count) { Text = ""; return; }
        SetFromHistory();
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

        History.Clear();
        foreach (var h in Enumerable.Reverse(_history)) History.Add(h);

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
            History.Clear();
            foreach (var h in Enumerable.Reverse(_history)) History.Add(h);
        }
        catch { }
    }
}
