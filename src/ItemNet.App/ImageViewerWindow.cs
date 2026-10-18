using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ItemNet.App;

/// <summary>
/// Shows one view at full size. Left/Right step through the set, Esc closes. Opens at the image's
/// native pixel size (fitted to the screen if larger) and scales when resized.
/// </summary>
public sealed class ImageViewerWindow : Window
{
    private readonly IReadOnlyList<(string label, BitmapSource? image)> _views;
    private readonly string _caption;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private int _index;

    public ImageViewerWindow(IReadOnlyList<(string label, BitmapSource? image)> views, int index, string caption)
    {
        _views = views;
        _index = index;
        _caption = caption;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Content = _image;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        KeyDown += OnKeyDown;
        MouseLeftButtonUp += (_, _) => Close();
        Show(_index);
        SizeToImage();
    }

    private void Show(int index)
    {
        _index = index;
        var (label, image) = _views[index];
        _image.Source = image;
        Title = image is null ? $"{_caption}: {label}" : $"{_caption}: {label} ({image.PixelWidth}×{image.PixelHeight}), ←/→ to switch, Esc to close";
    }

    private void SizeToImage()
    {
        if (_views[_index].image is not { } img) return;
        // Pixel size in device-independent units, so 512 px shows as 512 screen pixels at any DPI.
        var dpi = VisualTreeHelper.GetDpi(Application.Current.MainWindow ?? this);
        var w = img.PixelWidth / dpi.DpiScaleX;
        var h = img.PixelHeight / dpi.DpiScaleY;
        var area = SystemParameters.WorkArea;
        var fit = Math.Min(1, Math.Min(area.Width * 0.95 / w, area.Height * 0.9 / h));
        _image.Width = w * fit;
        _image.Height = h * fit;
        SizeToContent = SizeToContent.WidthAndHeight;
        Loaded += (_, _) =>
        {
            // Let it scale freely once the window has taken the image's size.
            SizeToContent = SizeToContent.Manual;
            _image.Width = _image.Height = double.NaN;
            // CenterOwner is applied before SizeToContent settles, so centre by hand.
            if (Owner is { } o && o.WindowState != WindowState.Minimized)
            {
                var (ox, oy, ow, oh) = o.WindowState == WindowState.Maximized
                    ? (SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height)
                    : (o.Left, o.Top, o.ActualWidth, o.ActualHeight);
                Left = ox + (ow - ActualWidth) / 2;
                Top = oy + (oh - ActualHeight) / 2;
            }
        };
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Left: Show((_index + _views.Count - 1) % _views.Count); break;
            case Key.Right: Show((_index + 1) % _views.Count); break;
            default: return;
        }
        e.Handled = true;
    }
}
