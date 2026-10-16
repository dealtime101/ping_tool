using System.ComponentModel;

namespace PingTool
{
    // What Process.Start can throw for a file the shell is asked to open (FileOpener): no program for it or none allowed (Win32Exception), a
    // file that went meanwhile (FileNotFoundException), a refused permission or a folder that is not there (UnauthorizedAccess, IO), a path the
    // system does not take (Argument, NotSupported). All of them end as the box that says where the file is saved. A bug (a null reference) or a
    // lack of memory is not one of them: it is not hidden behind that box.
    internal static class OpenFailure
    {
        public static bool Is(Exception ex) =>
            ex is Win32Exception or InvalidOperationException or FileNotFoundException or DirectoryNotFoundException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException or ObjectDisposedException;
    }
}
