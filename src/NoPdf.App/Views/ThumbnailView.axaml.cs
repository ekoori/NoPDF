using Avalonia.Controls;
using Avalonia.Interactivity;
using NoPdf.App.ViewModels;

namespace NoPdf.App.Views;

public partial class ThumbnailView : UserControl
{
    public ThumbnailView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as PageThumbnail)?.EnsureRendered();
    }
}
