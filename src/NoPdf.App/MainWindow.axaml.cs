using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using NoPdf.App.Config;
using NoPdf.App.ViewModels;

namespace NoPdf.App;

public partial class MainWindow : Window
{
    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    public MainWindow()
    {
        InitializeComponent();

        var vm = new MainWindowViewModel
        {
            OpenFilePicker = PickPdfFilesAsync,
            OpenSingleFilePicker = PickSinglePdfAsync,
            OpenCertFilePicker = PickCertAsync,
            SaveAsPicker = PickSaveAsAsync,
            CopyHandler = CopySelectionAsync,
            CopyTextHandler = CopyTextAsync,
        };
        // Closing the window (X, Alt+F4, shutdown) must still cache unsaved edits.
        Closing += (_, _) => Vm.AutosaveNow();
        vm.QuitRequested += () =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        };
        DataContext = vm;

        // Hide the OS title bar (icon + min/max/close) unless the config re-enables
        // it. BorderOnly keeps a resize border; the drag strip moves the window.
        if (!vm.Config.ShowTitlebar)
            WindowDecorations = Avalonia.Controls.WindowDecorations.BorderOnly;

        vm.ConfigApplied += ApplyConfigLive;
        vm.MinimizeRequested += () => WindowState = WindowState.Minimized;

        AddHandler(KeyDownEvent, OnGlobalKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy : DragDropEffects.None;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;
        foreach (var f in files)
        {
            var path = f.TryGetLocalPath();
            if (path is not null && NoPdf.Core.Import.DocumentImport.IsSupportedDocument(path))
                await Vm.OpenPathAsync(path); // focus it if it's already open
        }
    }

    private void OnCommandButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm.CommandBar.Open(":");

    private void ApplyConfigLive(NoPdf.App.Config.AppConfig cfg)
    {
        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = cfg.Theme.ToLowerInvariant() switch
            {
                "light" => Avalonia.Styling.ThemeVariant.Light,
                "dark" => Avalonia.Styling.ThemeVariant.Dark,
                _ => Avalonia.Styling.ThemeVariant.Default,
            };
        WindowDecorations = cfg.ShowTitlebar
            ? Avalonia.Controls.WindowDecorations.Full
            : Avalonia.Controls.WindowDecorations.BorderOnly;
    }

    private static bool OverButton(object? source)
        => (source as Control)?.FindAncestorOfType<Button>(includeSelf: true) is not null;

    private void OnDragStripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && !OverButton(e.Source))
            BeginMoveDrag(e);
    }

    private void OnDragStripDoubleTapped(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (OverButton(e.Source)) return;
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnOutlineSelected(object? sender, SelectionChangedEventArgs e)
        => NavigateToBookmark(e);

    private void NavigateToBookmark(SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is BookmarkNode node && node.PageIndex >= 0)
            Vm.SelectedTab?.GoToPage(node.PageIndex + 1);
    }

    // ----- Thumbnails / page operations -----

    private int SelectedThumbIndex()
        => ThumbList.SelectedItem is PageThumbnail t ? t.PageIndex : (Vm.SelectedTab?.CurrentPage ?? 1) - 1;

    private System.Collections.Generic.List<int> SelectedThumbIndices()
    {
        var list = ThumbList.SelectedItems?.OfType<PageThumbnail>().Select(t => t.PageIndex).OrderBy(i => i).ToList()
                   ?? new System.Collections.Generic.List<int>();
        if (list.Count == 0) list.Add(SelectedThumbIndex());
        return list;
    }

    private void OnThumbSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[^1] is PageThumbnail t)
            Vm.SelectedTab?.GoToPage(t.PageNumber);
    }

    /// <summary>Selects and scrolls to the thumbnail of the page currently shown in the view.</summary>
    private void OnThumbFocusCurrent(object? sender, RoutedEventArgs e)
    {
        var doc = Vm.SelectedTab;
        if (doc is null || doc.Thumbnails.Count == 0) return;
        int idx = System.Math.Clamp(doc.CurrentPage - 1, 0, doc.Thumbnails.Count - 1);
        ThumbList.SelectedItems?.Clear();
        ThumbList.SelectedIndex = idx;
        ThumbList.ScrollIntoView(idx);
    }

    private void OnThumbRotateLeft(object? sender, RoutedEventArgs e)
        => Vm.SelectedTab?.RotatePages(SelectedThumbIndices(), -90);

    private void OnThumbRotateRight(object? sender, RoutedEventArgs e)
        => Vm.SelectedTab?.RotatePages(SelectedThumbIndices(), 90);

    private void OnThumbMoveUp(object? sender, RoutedEventArgs e)
        => Vm.SelectedTab?.MovePages(SelectedThumbIndices(), -1);

    private void OnThumbMoveDown(object? sender, RoutedEventArgs e)
        => Vm.SelectedTab?.MovePages(SelectedThumbIndices(), +1);

    private void OnThumbDelete(object? sender, RoutedEventArgs e)
        => Vm.SelectedTab?.DeletePages(SelectedThumbIndices());

    private async void OnThumbInsert(object? sender, RoutedEventArgs e)
    {
        var doc = Vm.SelectedTab;
        if (doc is null) return;
        var paths = await PickPdfFilesAsync();   // multiselect dialog
        int at = SelectedThumbIndex() + 1;
        foreach (var path in paths)
            at += doc.InsertFile(path, at);
    }

    private async void OnRecentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string path })
            await Vm.OpenPathAsync(path);
    }

    private async void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        bool inText = FocusManager?.GetFocusedElement() is TextBox;

        // Command bar open: if it's focused let it handle keys; if the user clicked
        // away (page focused), ':' brings focus back and Esc closes it.
        if (Vm.CommandBar.IsVisible)
        {
            if (inText) return;
            if (!ctrl && !alt)
            {
                if (e.Key == Key.OemSemicolon && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                { Vm.CommandBar.RequestFocus(); e.Handled = true; return; }
                if (e.Key == Key.Escape) { Vm.CommandBar.Cancel(); e.Handled = true; return; }
            }
            return;
        }

        // While editing annotation text, don't hijack keys.
        if (inText) return;

        // Follow-links hint mode captures letters until a match / cancel.
        if (Vm.SelectedTab?.IsHintMode == true)
        {
            if (e.Key == Key.Escape) { Vm.SelectedTab.ExitHintMode(); e.Handled = true; return; }
            if (!ctrl && !alt && e.Key >= Key.A && e.Key <= Key.Z)
                Vm.SelectedTab.FeedHintKey((char)('a' + (e.Key - Key.A)));
            else
                Vm.SelectedTab.ExitHintMode();
            e.Handled = true; return;
        }

        // Command-line / search triggers and Escape (no modifiers).
        if (!ctrl && !alt)
        {
            switch (e.Key)
            {
                case Key.OemSemicolon when e.KeyModifiers.HasFlag(KeyModifiers.Shift): // ':'
                    Vm.HideWhichKey(); Vm.KeyBindings.Reset();
                    Vm.CommandBar.Open(":"); e.Handled = true; return;
                case Key.OemQuestion or Key.Divide: // '/'
                    Vm.HideWhichKey(); Vm.KeyBindings.Reset();
                    Vm.CommandBar.Open("/"); e.Handled = true; return;
                case Key.Escape:
                    if (Vm.IsWhichKeyVisible || Vm.KeyBindings.HasPending)
                    { Vm.KeyBindings.Reset(); Vm.HideWhichKey(); }
                    else Vm.SelectedTab?.ClearActiveSelection();
                    e.Handled = true; return;
            }
        }

        // Ctrl+F opens the search bar (a UI action, not a command).
        if (ctrl && !alt && e.Key == Key.F)
        {
            Vm.HideWhichKey(); Vm.KeyBindings.Reset();
            Vm.CommandBar.Open("/"); e.Handled = true; return;
        }

        // Normal-mode hotkey dispatch (supports modifiers + special keys).
        string? token = KeyToken(e);
        if (token is not null)
        {
            var result = Vm.KeyBindings.Feed(token);
            switch (result.Kind)
            {
                case KeyFeedKind.Execute:
                    Vm.HideWhichKey(); e.Handled = true;
                    // A binding whose value starts with ':' pre-fills the command line
                    // instead of running (e.g. o: ":open" → type args, then Enter).
                    var bound = result.Command!;
                    if (bound.StartsWith(':'))
                    {
                        Vm.KeyBindings.Reset();
                        Vm.CommandBar.Open(":", bound[1..].TrimStart() + " ");
                        return;
                    }
                    await RunCommand(bound);
                    return;
                case KeyFeedKind.Pending:
                    Vm.ShowWhichKey(result.Candidates); e.Handled = true; return;
                default:
                    Vm.HideWhichKey();
                    break;
            }
        }

        // Keep the page focused: never let Tab move focus into the toolbar.
        if (e.Key is Key.Tab) { e.Handled = true; return; }
    }

    private static readonly Dictionary<Key, string> SpecialKeys = new()
    {
        [Key.Up] = "up", [Key.Down] = "down", [Key.Left] = "left", [Key.Right] = "right",
        [Key.PageUp] = "pageup", [Key.PageDown] = "pagedown",
        [Key.Home] = "home", [Key.End] = "end",
        [Key.Space] = "space", [Key.Tab] = "tab", [Key.Enter] = "cr",
        [Key.Back] = "bs", [Key.Delete] = "del",
    };

    /// <summary>
    /// Maps a key press to a binding token: a bare char for plain letters/digits,
    /// or vim-style <c>&lt;c-r&gt;</c> / <c>&lt;a-left&gt;</c> / <c>&lt;up&gt;</c>
    /// for modified or special keys. Returns null for pure modifier presses.
    /// </summary>
    private static string? KeyToken(KeyEventArgs e)
    {
        var k = e.Key;
        // Ignore standalone modifier presses.
        if (k is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
              or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System)
            return null;

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        string? baseName = null;
        if (k >= Key.A && k <= Key.Z) baseName = ((char)('a' + (k - Key.A))).ToString();
        else if (k >= Key.D0 && k <= Key.D9) baseName = ((char)('0' + (k - Key.D0))).ToString();
        else if (k >= Key.NumPad0 && k <= Key.NumPad9) baseName = ((char)('0' + (k - Key.NumPad0))).ToString();
        else if (SpecialKeys.TryGetValue(k, out var s)) baseName = s;
        if (baseName is null) return null;

        bool isLetter = baseName.Length == 1 && baseName[0] is >= 'a' and <= 'z';

        // Plain (no ctrl/alt): letters carry case via shift, others use <s-...>.
        if (!ctrl && !alt)
        {
            if (isLetter) return shift ? baseName.ToUpperInvariant() : baseName;
            if (!shift) return baseName.Length == 1 ? baseName : $"<{baseName}>";
            return $"<s-{baseName}>";
        }

        // Modified: <c-…>, <a-…>, <c-a-s-…> etc.
        var mods = new List<string>(3);
        if (ctrl) mods.Add("c");
        if (alt) mods.Add("a");
        if (shift) mods.Add("s");
        return $"<{string.Join('-', mods)}-{baseName}>";
    }

    private async Task RunCommand(string commandLine)
    {
        string? result = await Vm.Commands.ExecuteAsync(commandLine);
        if (result is not null) Vm.StatusText = result;
    }

    private async Task CopySelectionAsync()
    {
        var text = Vm.GetSelectionText();
        if (string.IsNullOrEmpty(text)) return;
        await PutOnClipboardAsync(text);
        Vm.StatusText = $"Copied {text.Length} chars";
    }

    private async Task CopyTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        await PutOnClipboardAsync(text);
        Vm.StatusText = $"Copied: {text}";
    }

    private async Task PutOnClipboardAsync(string text)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(text));
        await clipboard.SetDataAsync(data);
    }

    private async Task<IReadOnlyList<string>> PickPdfFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open document",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Documents") { Patterns = new[] { "*.pdf", "*.cbz", "*.cbr", "*.cb7", "*.cbt", "*.djvu", "*.djv" } },
                new FilePickerFileType("PDF documents") { Patterns = new[] { "*.pdf" } },
                new FilePickerFileType("Comic archives") { Patterns = new[] { "*.cbz", "*.cbr", "*.cb7", "*.cbt" } },
                new FilePickerFileType("DjVu") { Patterns = new[] { "*.djvu", "*.djv" } },
                FilePickerFileTypes.All,
            },
        });

        return files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();
    }

    private async Task<string?> PickSinglePdfAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select PDF",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("PDF documents") { Patterns = new[] { "*.pdf" } } },
        });
        return files.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => !string.IsNullOrEmpty(p));
    }

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(null).Properties.IsMiddleButtonPressed
            && (sender as Control)?.DataContext is DocumentViewModel doc)
        {
            Vm.CloseTabCommand.Execute(doc);
            e.Handled = true;
        }
    }

    private void OnAnnotationItemClick(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AnnotationListItem item)
            Vm.SelectedTab?.SelectAnnotationItemCommand.Execute(item);
    }

    private async Task<string?> PickCertAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select certificate (.pfx / .p12)",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PKCS#12 certificate") { Patterns = new[] { "*.pfx", "*.p12" } },
                FilePickerFileTypes.All,
            },
        });
        return files.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => !string.IsNullOrEmpty(p));
    }

    private async Task<string?> PickSaveAsAsync(string? dir, string? name)
    {
        var opts = new FilePickerSaveOptions
        {
            Title = "Save PDF As",
            DefaultExtension = "pdf",
            SuggestedFileName = name,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PDF documents") { Patterns = new[] { "*.pdf" } },
            },
        };
        if (!string.IsNullOrEmpty(dir))
            opts.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(dir);

        var file = await StorageProvider.SaveFilePickerAsync(opts);
        return file?.TryGetLocalPath();
    }
}
