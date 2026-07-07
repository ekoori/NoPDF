using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
            SaveAsPicker = PickSaveAsAsync,
            CopyHandler = CopySelectionAsync,
        };
        vm.QuitRequested += () =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        };
        DataContext = vm;

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
            if (path is not null && path.EndsWith(".pdf", System.StringComparison.OrdinalIgnoreCase))
                await Vm.OpenPathAsync(path, forceNewTab: true);
        }
    }

    private void OnCommandButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm.CommandBar.Open(":");

    private void OnOutlineSelected(object? sender, SelectionChangedEventArgs e)
        => NavigateToBookmark(e);

    private void OnUserBookmarkSelected(object? sender, SelectionChangedEventArgs e)
        => NavigateToBookmark(e);

    private void NavigateToBookmark(SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is BookmarkNode node && node.PageIndex >= 0)
            Vm.SelectedTab?.GoToPage(node.PageIndex + 1);
    }

    // ----- Thumbnails / page operations -----

    private int SelectedThumbIndex()
        => ThumbList.SelectedItem is PageThumbnail t ? t.PageIndex : (Vm.SelectedTab?.CurrentPage ?? 1) - 1;

    private void OnThumbSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is PageThumbnail t)
            Vm.SelectedTab?.GoToPage(t.PageNumber);
    }

    private void OnThumbRotateLeft(object? sender, RoutedEventArgs e)
        => Vm.SelectedTab?.RotatePages(new[] { SelectedThumbIndex() }, -90);

    private void OnThumbRotateRight(object? sender, RoutedEventArgs e)
        => Vm.SelectedTab?.RotatePages(new[] { SelectedThumbIndex() }, 90);

    private void OnThumbMoveUp(object? sender, RoutedEventArgs e)
    {
        int i = SelectedThumbIndex();
        Vm.SelectedTab?.MovePage(i, i - 1);
    }

    private void OnThumbMoveDown(object? sender, RoutedEventArgs e)
    {
        int i = SelectedThumbIndex();
        Vm.SelectedTab?.MovePage(i, i + 1);
    }

    private void OnThumbDelete(object? sender, RoutedEventArgs e)
        => Vm.SelectedTab?.DeletePages(new[] { SelectedThumbIndex() });

    private async void OnThumbInsert(object? sender, RoutedEventArgs e)
    {
        var doc = Vm.SelectedTab;
        if (doc is null) return;
        var path = await PickSinglePdfAsync();
        if (path is not null) doc.InsertFile(path, SelectedThumbIndex() + 1);
    }

    private async void OnRecentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string path })
            await Vm.OpenPathAsync(path);
    }

    private async void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        // While the command bar is focused, let it handle everything.
        if (Vm.CommandBar.IsVisible) return;
        // While editing annotation text, don't hijack keys.
        if (FocusManager?.GetFocusedElement() is TextBox) return;

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

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
                    await RunCommand(result.Command!);
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
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(text));
            await clipboard.SetDataAsync(data);
            Vm.StatusText = $"Copied {text.Length} chars";
        }
    }

    private async Task<IReadOnlyList<string>> PickPdfFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open PDF",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PDF documents") { Patterns = new[] { "*.pdf" } },
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

    private async Task<string?> PickSaveAsAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save PDF As",
            DefaultExtension = "pdf",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PDF documents") { Patterns = new[] { "*.pdf" } },
            },
        });
        return file?.TryGetLocalPath();
    }
}
