using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using NoPdf.App.ViewModels;

namespace NoPdf.App.Views;

public partial class CommandBar : UserControl
{
    private CommandBarViewModel? _vm;
    private TextBox? _input;

    public CommandBar()
    {
        InitializeComponent();
        _input = this.FindControl<TextBox>("Input");
        DataContextChanged += OnDataContextChanged;
        if (_input is not null)
            _input.KeyDown += OnInputKeyDown;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.Opened -= OnOpened;
        _vm = DataContext as CommandBarViewModel;
        if (_vm is not null) _vm.Opened += OnOpened;
    }

    private void OnOpened()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _input?.Focus();
            if (_input is not null) _input.CaretIndex = _input.Text?.Length ?? 0;
        }, DispatcherPriority.Input);
    }

    private async void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await _vm.ExecuteAsync();
                break;
            case Key.Escape:
                e.Handled = true;
                _vm.Cancel();
                break;
            case Key.Up:
                e.Handled = true;
                _vm.HistoryPrev();
                MoveCaretToEnd();
                break;
            case Key.Down:
                e.Handled = true;
                _vm.HistoryNext();
                MoveCaretToEnd();
                break;
        }
    }

    private void MoveCaretToEnd()
    {
        if (_input is not null) _input.CaretIndex = _input.Text?.Length ?? 0;
    }

    private void OnHistoryPick(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is string entry)
        {
            _vm.FillFromHistory(entry.StartsWith('/') ? entry[1..] : entry);
            _input?.Focus();
            MoveCaretToEnd();
            if (sender is ListBox lb) lb.SelectedItem = null;
        }
    }
}

