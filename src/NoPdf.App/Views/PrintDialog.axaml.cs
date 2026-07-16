using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NoPdf.App.Printing;

namespace NoPdf.App.Views;

/// <summary>Print options dialog. Returns the chosen options, or null if cancelled.</summary>
public partial class PrintDialog : Window
{
    /// <summary>The dialog's result: the options, the page range, and — when the user named
    /// one — a preset to save them under (usable as <c>:print &lt;name&gt;</c>).</summary>
    public sealed record Result(PrintOptions Options, string Range, string PresetName, bool MakeDefault);

    // Note: no hand-written InitializeComponent. A parameterless one would win overload
    // resolution over Avalonia's generated InitializeComponent(bool), and only the
    // generated one assigns the x:Name fields below — they would all stay null.
    public PrintDialog() => InitializeComponent();

    /// <summary>Fills the dialog from the current defaults.</summary>
    public void Init(PrintOptions defaults, string range)
    {
        if (PrintService.IsSupported && OperatingSystem.IsWindows())
        {
            var printers = PrintService.Printers();
            PrinterBox.ItemsSource = printers;
            string want = string.IsNullOrWhiteSpace(defaults.Printer)
                ? PrintService.DefaultPrinter() : defaults.Printer;
            PrinterBox.SelectedItem = printers.Contains(want) ? want : printers.Count > 0 ? printers[0] : null;
        }
        CopiesBox.Value = Math.Clamp(defaults.Copies, 1, 99);
        RangeBox.Text = range;
        FitBox.IsChecked = defaults.FitToPage;
        GrayBox.IsChecked = defaults.Grayscale;
        LandscapeBox.IsChecked = defaults.Landscape;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnPrint(object? sender, RoutedEventArgs e)
        => Close(new Result(
            new PrintOptions
            {
                Printer = PrinterBox.SelectedItem as string ?? "",
                Copies = (int)(CopiesBox.Value ?? 1),
                FitToPage = FitBox.IsChecked == true,
                Grayscale = GrayBox.IsChecked == true,
                Landscape = LandscapeBox.IsChecked == true,
            },
            RangeBox.Text ?? "",
            PresetNameBox.Text ?? "",
            DefaultBox.IsChecked == true));
}
