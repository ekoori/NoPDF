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

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        bool inText = FocusManager?.GetFocusedElement() is TextBox;

        // Ex-command triggers and normal-mode hotkeys (only when not typing text).
        if (!ctrl && !inText)
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

            // Multi-key hotkey dispatch.
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
        }

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.O: await Vm.OpenCommand.ExecuteAsync(null); e.Handled = true; break;
                case Key.S: await Vm.SaveCommand.ExecuteAsync(null); e.Handled = true; break;
                case Key.W: Vm.CloseTabCommand.Execute(null); e.Handled = true; break;
                case Key.F: Vm.CommandBar.Open("/"); e.Handled = true; break;
                case Key.C: await CopySelectionAsync(); e.Handled = true; break;
                case Key.Z when e.KeyModifiers.HasFlag(KeyModifiers.Shift): Vm.SelectedTab?.Redo(); e.Handled = true; break;
                case Key.Z: Vm.SelectedTab?.Undo(); e.Handled = true; break;
                case Key.Y: Vm.SelectedTab?.Redo(); e.Handled = true; break;
                case Key.OemPlus or Key.Add: Vm.SelectedTab?.ZoomInCommand.Execute(null); e.Handled = true; break;
                case Key.OemMinus or Key.Subtract: Vm.SelectedTab?.ZoomOutCommand.Execute(null); e.Handled = true; break;
                case Key.D0 or Key.NumPad0: Vm.SelectedTab?.ZoomResetCommand.Execute(null); e.Handled = true; break;
            }
        }
    }

    /// <summary>Maps a plain (no Ctrl/Alt) letter or digit key to a hotkey token.</summary>
    private static string? KeyToken(KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            return null;
        var k = e.Key;
        if (k >= Key.A && k <= Key.Z)
        {
            char c = (char)('a' + (k - Key.A));
            return e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                ? char.ToUpperInvariant(c).ToString() : c.ToString();
        }
        if (k >= Key.D0 && k <= Key.D9) return ((char)('0' + (k - Key.D0))).ToString();
        if (k >= Key.NumPad0 && k <= Key.NumPad9) return ((char)('0' + (k - Key.NumPad0))).ToString();
        return null;
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
