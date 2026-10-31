using System.Runtime.InteropServices;
using OPNsenseWidgets.Hosting;

namespace OPNsenseWidgets.Platform;

public sealed class WindowsPaths : IPlatformPaths
{
    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    public string DownloadsFolder
    {
        get
        {
            var folderId = Guid.Parse("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
            SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out var pathPtr);
            var path = Marshal.PtrToStringUni(pathPtr)!;
            Marshal.FreeCoTaskMem(pathPtr);
            return path;
        }
    }
}
