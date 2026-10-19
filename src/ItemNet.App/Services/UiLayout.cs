using System.Windows;

namespace ItemNet.App.Services;

/// <summary>A window's restored (non-maximised) bounds, in device-independent units.</summary>
public sealed class WindowBounds
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }

    public static WindowBounds Capture(Window w)
    {
        // RestoreBounds holds the normal-state rectangle while maximised or minimised.
        var r = w.WindowState == WindowState.Normal ? new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight) : w.RestoreBounds;
        return new WindowBounds { Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height, Maximized = w.WindowState == WindowState.Maximized };
    }

    /// <summary>
    /// Puts a window back the way it was. Call before Show. With <paramref name="position"/> false,
    /// only the size and maximised state come back and the window keeps its startup location.
    /// A saved position is skipped if its title bar no longer overlaps the desktop enough to grab
    /// (a monitor since unplugged, say).
    /// </summary>
    public static void Apply(Window w, WindowBounds? b, bool position = true)
    {
        if (b is null || b.Width < 100 || b.Height < 100) return;
        // Never larger than the primary screen's work area, so a centred window stays reachable.
        w.Width = Math.Max(Math.Min(b.Width, SystemParameters.WorkArea.Width), w.MinWidth);
        w.Height = Math.Max(Math.Min(b.Height, SystemParameters.WorkArea.Height), w.MinHeight);
        if (b.Maximized) w.WindowState = WindowState.Maximized;
        if (!position) return;

        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var title = new Rect(b.Left, b.Top, b.Width, 40);
        title.Intersect(desktop);
        if (title.IsEmpty || title.Width < 100) return;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = b.Left;
        w.Top = b.Top;
    }
}

/// <summary>Window positions and panel sizes, remembered between runs.</summary>
public sealed class UiLayout
{
    /// <summary>The main window's size and maximised state (its position isn't restored).</summary>
    public WindowBounds? Main { get; set; }
    public WindowBounds? Batch { get; set; }
    public WindowBounds? Settings { get; set; }
    /// <summary>Width of the item list panel; 0 keeps the default.</summary>
    public double ListWidth { get; set; }
    /// <summary>Item list column widths, in display order.</summary>
    public List<double> ListColumns { get; set; } = [];
    public bool SkyrimNetExpanded { get; set; } = true;
    public bool RawReplyExpanded { get; set; }
}
