using System;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NoPdf.App.Rendering;
using NoPdf.Core.Rendering;

namespace NoPdf.App.ViewModels;

/// <summary>A small page preview for the thumbnails panel.</summary>
public sealed partial class PageThumbnail : ViewModelBase
{
    private const double ThumbWidth = 140;
    private readonly DocumentViewModel _owner;
    private readonly PageInfo _size;
    private bool _rendered;

    public int PageIndex { get; }
    public int PageNumber => PageIndex + 1;

    public double DisplayWidth => ThumbWidth;
    public double DisplayHeight => _size.Width > 0 ? ThumbWidth * _size.Height / _size.Width : ThumbWidth;

    [ObservableProperty] private Bitmap? _image;

    public PageThumbnail(DocumentViewModel owner, int pageIndex, PageInfo size)
    {
        _owner = owner;
        PageIndex = pageIndex;
        _size = size;
    }

    public void EnsureRendered()
    {
        if (_rendered) return;
        _rendered = true;
        var doc = _owner.Document;
        if (doc is null) return;
        double scale = ThumbWidth / _size.Width;
        _ = Task.Run(() =>
        {
            try
            {
                var page = doc.RenderPage(PageIndex, scale);
                Dispatcher.UIThread.Post(() => Image = BitmapConverter.ToWriteableBitmap(page));
            }
            catch { }
        });
    }
}
