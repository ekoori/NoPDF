using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NoPdf.App.Commands;
using NoPdf.App.Printing;

namespace NoPdf.App.Views;

/// <summary>Print options dialog. Returns the chosen options, or null if cancelled.</summary>
public partial class PrintDialog : Window
{
    /// <summary>The dialog's result: the options, the page range, and — when the user named
    /// one — a preset to save them under (usable as <c>:print &lt;name&gt;</c>).</summary>
    public sealed record Result(PrintOptions Options, string Range, string PresetName, bool MakeDefault);

    private const string NewPreset = "(new preset)";
    private IReadOnlyList<PrintPreset> _presets = Array.Empty<PrintPreset>();
    // Filling the dropdown selects an item, which would otherwise fire the handler and
    // overwrite the values we are in the middle of applying.
    private bool _loading;

    // Note: no hand-written InitializeComponent. A parameterless one would win overload
    // resolution over Avalonia's generated InitializeComponent(bool), and only the
    // generated one assigns the x:Name fields below — they would all stay null.
    public PrintDialog() => InitializeComponent();

    /// <summary>Fills the dialog from the current defaults and the saved presets.</summary>
    public void Init(PrintOptions defaults, string range, IReadOnlyList<PrintPreset> presets)
    {
        _loading = true;
        _presets = presets;

        var names = new List<string> { NewPreset };
        names.AddRange(presets.Select(p => p.Name));
        PresetBox.ItemsSource = names;
        var preselect = presets.FirstOrDefault(p => p.IsDefault);
        PresetBox.SelectedIndex = preselect is null ? 0 : names.IndexOf(preselect.Name);

        if (PrintService.IsSupported && OperatingSystem.IsWindows())
        {
            var printers = PrintService.Printers();
            PrinterBox.ItemsSource = printers;
        }
        _loading = false;

        // `defaults` already reflects the default preset, so this agrees with the selection.
        Apply(defaults);
        RangeBox.Text = range;
        PresetNameBox.Text = preselect?.Name ?? "";
        DefaultBox.IsChecked = preselect is not null;
    }

    /// <summary>Picking a preset loads its settings, and offers its name for editing —
    /// rename it and printing saves it as a new preset.</summary>
    private void OnPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || PresetBox.SelectedItem is not string name) return;
        if (name == NewPreset)
        {
            PresetNameBox.Text = "";
            DefaultBox.IsChecked = false;
            return;
        }
        if (_presets.FirstOrDefault(p => p.Name == name) is not { } p) return;
        Apply(p.ToOptions());
        PresetNameBox.Text = p.Name;
        DefaultBox.IsChecked = p.IsDefault;
    }

    private void Apply(PrintOptions o)
    {
        if (PrinterBox.ItemsSource is IEnumerable<string> printers)
        {
            var list = printers.ToList();
            string want = string.IsNullOrWhiteSpace(o.Printer) && OperatingSystem.IsWindows()
                ? PrintService.DefaultPrinter() : o.Printer;
            PrinterBox.SelectedItem = list.Contains(want) ? want : list.FirstOrDefault();
        }
        CopiesBox.Value = Math.Clamp(o.Copies, 1, 99);
        FitBox.IsChecked = o.FitToPage;
        GrayBox.IsChecked = o.Grayscale;
        LandscapeBox.IsChecked = o.Landscape;
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
