using System.Runtime.InteropServices;

namespace MoniTopo.Windows.Shell;

public static partial class NativeIconHandle
{
    public static void Destroy(nint iconHandle) => _ = DestroyIcon(iconHandle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint iconHandle);
}
