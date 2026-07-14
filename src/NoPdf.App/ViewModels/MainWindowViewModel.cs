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
using NoPdf.Core.Annotations;

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
    public bool IsTitlebarHidden => !Config.ShowTitlebar;

    // ----- Tab display (top rows / left panel / off) -----
    [ObservableProperty] private bool _isTabsTop;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ShowLeftTabs))] private bool _isTabsLeft;
    [ObservableProperty] private double _tabsMaxHeight = 96;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ShowLeftTabs))] private bool _leftTabsVisible;
    private int _peekToken;

    public bool ShowLeftTabs => IsTabsLeft && LeftTabsVisible;

    // ----- Signature presets -----
    [ObservableProperty] private bool _isSignaturePanelOpen;
    [ObservableProperty] private SignaturePreset? _selectedSignaturePreset;
    public ObservableCollection<SignaturePreset> SignaturePresets { get; } = new();
    private readonly SignaturePresetStore _sigStore = new();

    [RelayCommand] private void ToggleSignaturePanel() => IsSignaturePanelOpen = !IsSignaturePanelOpen;

    [RelayCommand]
    private void AddSignaturePreset()
    {
        var p = new SignaturePreset { Name = Config.SignerName, Alias = NextAlias() };
        HookPreset(p);
        SignaturePresets.Add(p);
        SelectedSignaturePreset = p;
        SaveSignaturePresets();
    }

    /// <summary>A unique default alias like "sig", "sig2", …</summary>
    private string NextAlias()
    {
        for (int i = 1; ; i++)
        {
            string a = i == 1 ? "sig" : "sig" + i;
            if (!SignaturePresets.Any(p => string.Equals(p.Alias, a, StringComparison.OrdinalIgnoreCase)))
                return a;
        }
    }

    /// <summary>Selects the preset whose alias matches (used by <c>:sign &lt;alias&gt;</c>).</summary>
    public bool SelectSignaturePresetByAlias(string alias)
    {
        var p = SignaturePresets.FirstOrDefault(x =>
            string.Equals(x.Alias, alias, StringComparison.OrdinalIgnoreCase));
        if (p is null) return false;
        SelectedSignaturePreset = p;
        return true;
    }

    /// <summary>Generates a self-signed certificate for the selected preset and enables it.</summary>
    [RelayCommand]
    private void GenerateCertificate()
    {
        var p = SelectedSignaturePreset;
        if (p is null) return;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NoPdf", "certs");
            string safe = string.Concat((string.IsNullOrWhiteSpace(p.Alias) ? p.Name : p.Alias)
                .Split(Path.GetInvalidFileNameChars()));
            if (string.IsNullOrWhiteSpace(safe)) safe = "signature";
            string dest = Path.Combine(dir, safe + ".pfx");
            string pwd = string.IsNullOrEmpty(p.CertPassword) ? "noPDF" : p.CertPassword;

            NoPdf.Core.Signing.SignatureService.GenerateSelfSigned(
                string.IsNullOrWhiteSpace(p.Name) ? "noPDF Signature" : p.Name, pwd, dest);

            p.CertPassword = pwd;
            p.CertPath = dest;
            p.UseCertificate = true;
            StatusText = $"Generated certificate → {dest}";
        }
        catch (Exception ex) { StatusText = "Generate failed: " + ex.Message; }
    }

    /// <summary>
    /// Called when a certified signature stamp's reason edit is committed: exports the
    /// document with the stamp, prompts for a destination, and applies the certificate.
    /// </summary>
    public async void CertifySignature(DocumentViewModel doc, SignatureAnnotation sig)
    {
        if (sig.Certified) return;                 // already signed — don't re-prompt
        string? pfx = sig.CertPath;
        if (string.IsNullOrWhiteSpace(pfx) || !File.Exists(pfx))
        { StatusText = "Certificate not found: " + pfx; return; }

        string dir = Path.GetDirectoryName(doc.FilePath) ?? "";
        string name = $"{Path.GetFileNameWithoutExtension(doc.FilePath)}_signed.pdf";
        string? dest = await PickSaveAs(dir, name);
        if (dest is null) { StatusText = "Signing cancelled"; return; } // leave un-certified so it can be retried
        try
        {
            var bytes = doc.ExportWithAnnotations();
            var cert = NoPdf.Core.Signing.SignatureService.LoadCertificate(pfx, sig.CertPassword ?? "");
            string reason = sig.Contents ?? "";
            await Task.Run(() => NoPdf.Core.Signing.SignatureService.Sign(bytes, dest, cert, reason, ""));
            sig.Certified = true;                  // succeeded — don't fire again
            StatusText = $"Signed & certified → {Path.GetFileName(dest)}";
        }
        catch (Exception ex) { StatusText = "Sign failed: " + ex.Message; }
    }

    [RelayCommand]
    private void DeleteSignaturePreset()
    {
        if (SelectedSignaturePreset is null) return;
        SignaturePresets.Remove(SelectedSignaturePreset);
        SelectedSignaturePreset = SignaturePresets.Count > 0 ? SignaturePresets[0] : null;
        SaveSignaturePresets();
    }

    partial void OnSelectedSignaturePresetChanged(SignaturePreset? value) => ApplyPresetToAllDocs();

    private void LoadSignaturePresets()
    {
        foreach (var dto in _sigStore.Load())
        {
            var p = new SignaturePreset
            {
                Name = dto.Name, Alias = dto.Alias, FrameColor = dto.FrameColor,
                FrameThickness = dto.FrameThickness, FrameOpacity = dto.FrameOpacity,
                UseCertificate = dto.UseCertificate, CertPath = dto.CertPath, CertPassword = dto.CertPassword,
            };
            HookPreset(p);
            SignaturePresets.Add(p);
        }
        SelectedSignaturePreset = SignaturePresets.Count > 0 ? SignaturePresets[0] : null;
    }

    private void HookPreset(SignaturePreset p) => p.PropertyChanged += (_, _) =>
    {
        SaveSignaturePresets();
        if (ReferenceEquals(p, SelectedSignaturePreset)) ApplyPresetToAllDocs();
    };

    private void SaveSignaturePresets() => _sigStore.Save(SignaturePresets.Select(p =>
        new SignaturePresetStore.Dto
        {
            Name = p.Name, Alias = p.Alias, FrameColor = p.FrameColor,
            FrameThickness = p.FrameThickness, FrameOpacity = p.FrameOpacity,
            UseCertificate = p.UseCertificate, CertPath = p.CertPath, CertPassword = p.CertPassword,
        }));

    private void ApplyPresetToAllDocs() { foreach (var t in Tabs) ApplyPresetToDoc(t); }

    private void ApplyPresetToDoc(DocumentViewModel doc)
    {
        var p = SelectedSignaturePreset;
        if (p is not null)
        {
            doc.SignerName = string.IsNullOrWhiteSpace(p.Name) ? Config.SignerName : p.Name;
            doc.SigColor = ParseHex(p.FrameColor, AnnotColor.Blue);
            doc.SigThickness = p.FrameThickness;
            doc.SigOpacity = p.FrameOpacity;
            doc.SigUseCertificate = p.UseCertificate;
            doc.SigCertPath = p.CertPath;
            doc.SigCertPassword = p.CertPassword;
        }
        else
        {
            doc.SignerName = Config.SignerName; doc.SigColor = AnnotColor.Blue;
            doc.SigThickness = 1.5; doc.SigOpacity = 1.0;
            doc.SigUseCertificate = false; doc.SigCertPath = ""; doc.SigCertPassword = "";
        }
    }

    private static AnnotColor ParseHex(string hex, AnnotColor fallback)
    {
        hex = (hex ?? "").Trim().TrimStart('#');
        if (hex.Length == 6
            && byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return new AnnotColor(r, g, b);
        return fallback;
    }

    /// <summary>Raised to minimise the window (title bar is hidden).</summary>
    public event Action? MinimizeRequested;

    /// <summary>Opens an external link followed via hint mode (web/mail schemes only).</summary>
    private void OpenExternalUri(string uri)
    {
        try
        {
            if (System.Uri.TryCreate(uri, UriKind.Absolute, out var u)
                && u.Scheme is "http" or "https" or "mailto")
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
                StatusText = "Opened " + uri;
            }
            else StatusText = "Blocked link: " + uri;
        }
        catch (Exception ex) { StatusText = "Open link failed: " + ex.Message; }
    }

    [RelayCommand] private void Minimize() => MinimizeRequested?.Invoke();
    [RelayCommand] private void Quit() => RequestQuit();
    [RelayCommand] private void SelectTab(DocumentViewModel? doc) { if (doc is not null) SelectedTab = doc; }

    private void ApplyTabsConfig()
    {
        var (pos, rows, peek) = Config.TabsParsed;
        IsTabsTop = pos == "top";
        IsTabsLeft = pos == "left";
        TabsMaxHeight = Math.Max(1, rows) * 30;
        LeftTabsVisible = pos == "left" && peek < 0;
    }

    public void ShowTabs(string arg)
    {
        Config.Tabs = string.IsNullOrWhiteSpace(arg) ? "top 3" : arg.Trim();
        AppConfig.SetScalar("tabs", Config.Tabs);
        ApplyTabsConfig();
    }

    private void PeekLeftTabs()
    {
        var (pos, _, peek) = Config.TabsParsed;
        if (pos != "left") return;
        if (peek < 0) { LeftTabsVisible = true; return; }
        if (peek == 0) return;
        LeftTabsVisible = true;
        int tok = ++_peekToken;
        Task.Delay(peek).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (tok == _peekToken) LeftTabsVisible = false;
        }));
    }

    /// <summary>Set by the view; opens the native file picker and returns chosen paths.</summary>
    public Func<Task<IReadOnlyList<string>>>? OpenFilePicker { get; set; }

    /// <summary>Set by the view; opens a single PDF (for insert/merge).</summary>
    public Func<Task<string?>>? OpenSingleFilePicker { get; set; }

    /// <summary>Set by the view; opens a certificate (.pfx/.p12) picker.</summary>
    public Func<Task<string?>>? OpenCertFilePicker { get; set; }

    [RelayCommand]
    private async Task BrowseCertificate()
    {
        var p = SelectedSignaturePreset;
        if (p is null || OpenCertFilePicker is null) return;
        var path = await OpenCertFilePicker();
        if (!string.IsNullOrEmpty(path)) { p.CertPath = path; p.UseCertificate = true; }
    }

    /// <summary>Set by the view; prompts for a destination path (suggested dir + name).</summary>
    public Func<string?, string?, Task<string?>>? SaveAsPicker { get; set; }

    public Task<string?> PickSaveAs(string? dir, string? name)
        => SaveAsPicker is not null ? SaveAsPicker(dir, name) : Task.FromResult<string?>(null);

    public Quickmarks Quickmarks { get; }
    public RecentFiles Recent { get; }
    public SessionStore Session { get; }
    public ViewStateStore ViewStates { get; } = new();
    private bool _restoring;
    public AppConfig Config { get; private set; }
    public KeyBindingService KeyBindings { get; private set; }

    /// <summary>Recently closed tabs (path + original index) for reopen/undo.</summary>
    private readonly Stack<(string Path, int Index)> _closedTabs = new();

    /// <summary>Raised after the config file is hot-reloaded so the view can re-apply theme/titlebar.</summary>
    public event Action<AppConfig>? ConfigApplied;
    public CommandRegistry Commands { get; }
    public CommandBarViewModel CommandBar { get; }

    public ObservableCollection<string> RecentFiles { get; } = new();

    // ----- Which-key hint (multi-key hotkeys in progress) -----
    [ObservableProperty] private bool _isWhichKeyVisible;
    public ObservableCollection<string> WhichKeyItems { get; } = new();

    /// <summary>Set by the view; copies the current selection to the clipboard.</summary>
    public Func<Task>? CopyHandler { get; set; }
    public void RequestCopy() { if (CopyHandler is not null) _ = CopyHandler(); }

    /// <summary>Set by the view; copies arbitrary text to the clipboard.</summary>
    public Func<string, Task>? CopyTextHandler { get; set; }
    public void RequestCopyText(string text) { if (CopyTextHandler is not null) _ = CopyTextHandler(text); }

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
        ApplyTabsConfig();
        LoadSignaturePresets();
        RefreshRecent();
        if (cfgError is not null) StatusText = cfgError;

        _configWatcher = new ConfigWatcher(AppConfig.ConfigPath, ReloadConfig);
    }

    private readonly ConfigWatcher _configWatcher;

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

    partial void OnSelectedTabChanged(DocumentViewModel? oldValue, DocumentViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        if (newValue is not null) newValue.IsActive = true;
        SaveSession();
        PeekLeftTabs();
    }

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
    public Task RestoreSessionAsync() => OpenSessionAsync(Session.Load(), replace: false);

    public void SaveNamedSession(string name)
        => Session.SaveNamed(name, new SessionStore.SessionData
        {
            Files = Tabs.Select(t => t.FilePath).ToList(),
            Active = SelectedTab is not null ? Tabs.IndexOf(SelectedTab) : -1,
        });

    public Task LoadNamedSessionAsync(string name)
        => OpenSessionAsync(Session.GetNamed(name), replace: true);

    private async Task OpenSessionAsync(SessionStore.SessionData? data, bool replace)
    {
        if (data is null || data.Files.Count == 0) return;
        _restoring = true;
        try
        {
            if (replace)
            {
                foreach (var t in Tabs.ToList()) t.Dispose();
                Tabs.Clear();
            }
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
        // Normalise so the same file opened via dialog / drag-drop / command / the OS
        // resolves to one identity (relative vs absolute, mixed separators, casing).
        try { path = Path.GetFullPath(path); } catch { /* keep as given */ }

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
            ApplyPresetToDoc(doc);
            var saved = ViewStates.Get(path);
            if (saved is not null) doc.InitialView = (saved.Zoom, saved.OffsetX, saved.OffsetY);
            doc.ViewStateSink = (z, x, y) => ViewStates.Set(path, z, x, y);
            doc.CertifyRequested = sig => CertifySignature(doc, sig);
            doc.OpenUriRequested += OpenExternalUri;
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
        _closedTabs.Push((doc.FilePath, idx));   // remember for reopen/undo
        Tabs.Remove(doc);
        doc.Dispose();
        SelectedTab = Tabs.Count > 0
            ? Tabs[Math.Clamp(idx, 0, Tabs.Count - 1)]
            : null;   // no tabs left → show the start page
    }

    /// <summary>Reopens the most recently closed tab at its original position.</summary>
    public async void ReopenClosedTab()
    {
        while (_closedTabs.Count > 0)
        {
            var (path, idx) = _closedTabs.Pop();
            if (!File.Exists(path)) continue;
            await OpenPathAsync(path, forceNewTab: true);
            var reopened = Tabs[^1];
            Tabs.Move(Tabs.Count - 1, Math.Clamp(idx, 0, Tabs.Count - 1));
            SelectedTab = reopened;
            return;
        }
        StatusText = "No closed tab to reopen";
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

    [ObservableProperty] private bool _isAnnotationListPanelOpen;

    [RelayCommand]
    private void ToggleAnnotationListPanel()
    {
        IsAnnotationListPanelOpen = !IsAnnotationListPanelOpen;
        if (IsAnnotationListPanelOpen) SelectedTab?.RefreshAnnotationList();
    }

    /// <summary>Undo: annotation/page/bookmark edit if available, else reopen a closed tab.</summary>
    [RelayCommand]
    public void Undo()
    {
        if (SelectedTab is { CanUndo: true } d) d.Undo();
        else if (_closedTabs.Count > 0) ReopenClosedTab();
        else SelectedTab?.Undo();
    }

    [RelayCommand]
    private void Redo() => SelectedTab?.Redo();

    // ----- Config hot-reload -----

    public void AddBinding(string key, string command)
    {
        Config.NormalBindings[key] = command;
        KeyBindings = new KeyBindingService(Config);
        AppConfig.AddBindingToFile("normal_bindings", key, command);
    }

    public void AddAlias(string alias, string command)
    {
        Config.Aliases[alias] = command;
        AppConfig.AddBindingToFile("aliases", alias, command);
    }

    public void ReloadConfig()
    {
        var cfg = AppConfig.Load(out var err);
        Config = cfg;
        KeyBindings = new KeyBindingService(cfg);
        IsToolbarVisible = cfg.ShowToolbar;
        ApplyTabsConfig();
        OnPropertyChanged(nameof(IsTitlebarHidden));
        ConfigApplied?.Invoke(cfg);
        StatusText = err ?? "Config reloaded";
    }

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
