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
        if (_vm is not null) { _vm.Opened -= OnOpened; _vm.FocusRequested -= OnFocusRequested; }
        _vm = DataContext as CommandBarViewModel;
        if (_vm is not null) { _vm.Opened += OnOpened; _vm.FocusRequested += OnFocusRequested; }
    }

    private void OnOpened() => FocusInput(caretToEnd: true);
    private void OnFocusRequested() => FocusInput(caretToEnd: true);

    private void FocusInput(bool caretToEnd)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _input?.Focus();
            if (caretToEnd && _input is not null) _input.CaretIndex = _input.Text?.Length ?? 0;
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
            case Key.Tab:
                e.Handled = true;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _vm.CompletePrev();
                else _vm.CompleteNext();
                MoveCaretToEnd();
                break;
        }
    }

    private void MoveCaretToEnd()
    {
        if (_input is not null) _input.CaretIndex = _input.Text?.Length ?? 0;
    }
}

