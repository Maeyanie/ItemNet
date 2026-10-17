using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ItemNet.Render.D3D;

namespace ItemNet.App.Services;

public static class Images
{
    /// <summary>RGBA8 pixels from the renderer as a frozen WPF bitmap.</summary>
    public static BitmapSource FromView(RenderedView view)
    {
        var bgra = new byte[view.Rgba.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = view.Rgba[i + 2];
            bgra[i + 1] = view.Rgba[i + 1];
            bgra[i + 2] = view.Rgba[i];
            bgra[i + 3] = view.Rgba[i + 3];
        }
        var bmp = BitmapSource.Create(view.Width, view.Height, 96, 96, PixelFormats.Bgra32, null, bgra, view.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    public static BitmapSource? FromFile(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad; // don't hold the file open
            img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            img.UriSource = new Uri(path);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }
}
