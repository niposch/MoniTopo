using System.Drawing;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MoniTopo.App.Tray;

public static class ApplicationIcon
{
    private static readonly Uri ResourceUri = new("pack://application:,,,/MoniTopo.App;component/Assets/MoniTopo.ico");

    // IconHelper also inspects the ICO decoder's other frames. Decoders remain
    // dispatcher-owned even when a frame is frozen, so load per window/thread.
    public static ImageSource WindowIcon => LoadWindowIcon();

    public static Icon CreateTrayIcon()
    {
        using var resource = System.Windows.Application.GetResourceStream(ResourceUri)!.Stream;
        return new Icon(resource, new Size(32, 32));
    }

    private static BitmapFrame LoadWindowIcon()
    {
        var image = BitmapFrame.Create(ResourceUri, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
        image.Freeze();
        return image;
    }
}
