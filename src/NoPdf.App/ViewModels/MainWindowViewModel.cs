using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoPdf.App.Commands;
using NoPdf.App.Config;
using NoPdf.App.Editing;

namespace NoPdf.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    public ObservableCollection<DocumentViewModel> Tabs { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocument))]
    private DocumentViewModel? _selectedTab;

    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private bool _isBookmarksPanelOpen;
    [ObservableProperty] private bool _isThumbnailsPanelOpen;
    [ObservableProperty] private bool _isToolbarVisible;
    [ObservableProperty] private bool _isAnnotationPanelOpen;

    public bool HasDocument => SelectedTab is not null;

    /// <summary>Set by the view; opens the native file picker and returns chosen paths.</summary>
    public Func<Task<IReadOnlyList<string>>>? OpenFilePicker { get; set; }

    /// <summary>Set by the view; opens a single PDF (for insert/merge).</summary>
    public Func<Task<string?>>? OpenSingleFilePicker { get; set; }

    /// <summary>Set by the view; prompts for a "Save As" destination path.</summary>
    public Func<Task<string?>>? SaveAsPicker { get; set; }

    public Quickmarks Quickmarks { get; }
    public RecentFiles Recent { get; }
    public SessionStore Session { get; }
    public ViewStateStore ViewStates { get; } = new();
    private bool _restoring;
    public AppConfig Config { get; }
    public KeyBindingService KeyBindings { get; }
    public CommandRegistry Commands { get; }
    public CommandBarViewModel CommandBar { get; }

    public ObservableCollection<string> RecentFiles { get; } = new();

    // ----- Which-key hint (multi-key hotkeys in progress) -----
    [ObservableProperty] private bool _isWhichKeyVisible;
    public ObservableCollection<string> WhichKeyItems { get; } = new();

    /// <summary>Set by the view; copies the current selection to the clipboard.</summary>
    public Func<Task>? CopyHandler { get; set; }
    public void RequestCopy() { if (CopyHandler is not null) _ = CopyHandler(); }

    /// <summary>Raised when the user runs :quit.</summary>
    public event Action? QuitRequested;

    public MainWindowViewModel()
    {
        Tabs.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasDocument)); SaveSession(); };
        Quickmarks = new Quickmarks();
        Recent = new RecentFiles();
        Session = new SessionStore();
        Config = AppConfig.Load(out string? cfgError);
        KeyBindings = new KeyBindingService(Config);
        Commands = new CommandRegistry(this, Quickmarks);
        CommandBar = new CommandBarViewModel(this, Config.CommandHistorySize, Config.HistoryVisible);
        IsToolbarVisible = Config.ShowToolbar;
        RefreshRecent();
        if (cfgError is not null) StatusText = cfgError;
    }

    public void ShowWhichKey(IReadOnlyList<(string Seq, string Command)> candidates)
    {
        WhichKeyItems.Clear();
        foreach (var (seq, cmd) in candidates)
            WhichKeyItems.Add($"{seq}  →  {cmd}");
        IsWhichKeyVisible = WhichKeyItems.Count > 0;
    }

    public void HideWhichKey()
    {
        IsWhichKeyVisible = false;
        WhichKeyItems.Clear();
    }

    public bool HasRecent => RecentFiles.Count > 0;

    private void RefreshRecent()
    {
        RecentFiles.Clear();
        foreach (var f in Recent.Files) RecentFiles.Add(f);
        OnPropertyChanged(nameof(HasRecent));
    }

    public void RequestQuit() => QuitRequested?.Invoke();

    partial void OnSelectedTabChanged(DocumentViewModel? value) => SaveSession();

    private void SaveSession()
    {
        if (_restoring) return;
        Session.Save(new SessionStore.SessionData
        {
            Files = Tabs.Select(t => t.FilePath).ToList(),
            Active = SelectedTab is not null ? Tabs.IndexOf(SelectedTab) : -1,
        });
    }

    /// <summary>Reopens the files from the previous session.</summary>
    public async Task RestoreSessionAsync()
    {
        var data = Session.Load();
        if (data is null || data.Files.Count == 0) return;
        _restoring = true;
        try
        {
            foreach (var f in data.Files)
                if (File.Exists(f)) await OpenPathAsync(f, forceNewTab: true);
        }
        finally { _restoring = false; }

        if (data.Active >= 0 && data.Active < Tabs.Count) SelectedTab = Tabs[data.Active];
        SaveSession();
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (OpenFilePicker is null) return;
        IReadOnlyList<string> paths;
        try { paths = await OpenFilePicker(); }
        catch { return; }

        foreach (var path in paths)
            await OpenPathAsync(path);
    }

    public async Task OpenPathAsync(string path, bool forceNewTab = false)
    {
        // Reuse an already-open tab for the same file unless a new tab is forced.
        if (!forceNewTab)
        {
            var existing = Tabs.FirstOrDefault(t =>
                string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { SelectedTab = existing; return; }
        }

        StatusText = $"Opening {path}…";
        try
        {
            var doc = await DocumentViewModel.LoadAsync(path);
            doc.TextboxFontSize = Config.TextboxFontSize;
            doc.TextboxFrameColor = Config.TextboxFrameColorValue;
            doc.TextboxFrameOpacity = Config.TextboxFrameOpacity;
            var saved = ViewStates.Get(path);
            if (saved is not null) doc.InitialView = (saved.Zoom, saved.OffsetX, saved.OffsetY);
            doc.ViewStateSink = (z, x, y) => ViewStates.Set(path, z, x, y);
            Tabs.Add(doc);
            SelectedTab = doc;
            Recent.Add(path);
            RefreshRecent();
            StatusText = $"Opened {doc.Title} ({doc.Pages.Count} pages)";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to open: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CloseTab(DocumentViewModel? doc)
    {
        doc ??= SelectedTab;
        if (doc is null) return;
        int idx = Tabs.IndexOf(doc);
        Tabs.Remove(doc);
        doc.Dispose();
        if (Tabs.Count > 0)
            SelectedTab = Tabs[Math.Clamp(idx, 0, Tabs.Count - 1)];
    }

    [RelayCommand]
    private void SelectTool(EditorTool tool)
    {
        if (SelectedTab is not null)
            SelectedTab.CurrentTool = tool;
    }

    [RelayCommand]
    private void ToggleBookmarks() => IsBookmarksPanelOpen = !IsBookmarksPanelOpen;

    [RelayCommand]
    private void ToggleThumbnails() => IsThumbnailsPanelOpen = !IsThumbnailsPanelOpen;

    [RelayCommand]
    private void ToggleToolbar() => IsToolbarVisible = !IsToolbarVisible;

    [RelayCommand]
    private void ToggleAnnotationPanel() => IsAnnotationPanelOpen = !IsAnnotationPanelOpen;

    [RelayCommand]
    private void Undo() => SelectedTab?.Undo();

    [RelayCommand]
    private void Redo() => SelectedTab?.Redo();

    /// <summary>Opens file(s) in new focused tabs (T / :O / :tabnew).</summary>
    [RelayCommand]
    private async Task OpenNewTabAsync()
    {
        if (OpenFilePicker is null) return;
        IReadOnlyList<string> paths;
        try { paths = await OpenFilePicker(); }
        catch { return; }
        foreach (var p in paths) await OpenPathAsync(p, forceNewTab: true);
    }

    public void TabNext()
    {
        if (Tabs.Count < 2 || SelectedTab is null) return;
        int i = Tabs.IndexOf(SelectedTab);
        SelectedTab = Tabs[(i + 1) % Tabs.Count];
    }

    public void TabPrev()
    {
        if (Tabs.Count < 2 || SelectedTab is null) return;
        int i = Tabs.IndexOf(SelectedTab);
        SelectedTab = Tabs[(i - 1 + Tabs.Count) % Tabs.Count];
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (SelectedTab is null) return;
        await SelectedTab.SaveAsync();
        StatusText = $"Saved {SelectedTab.Title}";
    }

    [RelayCommand]
    private async Task PrintAsync()
    {
        if (SelectedTab is null) return;
        StatusText = await Commands.ExecuteAsync("print") ?? StatusText;
    }

    /// <summary>Copies the active text selection of the current document to the clipboard.</summary>
    public string? GetSelectionText() => SelectedTab?.GetActiveSelectionText();
}
