namespace PingTool
{
    // The program's own icon (PingTool.ico, embedded): for the window and for the notification area, instead of the generic one
    // of Windows, which is the same for every program that has none and makes PingTool impossible to find in a crowd of icons.
    internal static class AppIcon
    {
        public const string ResourceName = "PingTool.ico";

        // size = what the place wants (the small icon of the notification area, say): the .ico holds 16 to 256 pixels and Windows
        // picks the closest frame. Falls back to the generic icon if the resource cannot be read: no icon is not a reason not to start.
        public static Icon Load(Size? size = null)
        {
            try
            {
                var assembly = typeof(AppIcon).Assembly;
                using var stream = assembly.GetManifestResourceStream(ResourceName);
                if (stream is not null) return size is Size s ? new Icon(stream, s) : new Icon(stream);

                // A resource that is not found: the generic icon, as before, but not in silence (see Report).
                Report(new FileNotFoundException(ResourceNote.Missing(ResourceName, assembly.GetManifestResourceNames())));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
            {
                // ExternalException: what the system answers (Win32Exception, COMException) when the picture is damaged inside - measured: an icon
                // whose pixels are altered raises a Win32Exception, where an empty, cut or mistaken one raises an ArgumentException.
                Report(ex);
            }

            return (Icon)SystemIcons.Application.Clone();
        }

        // A Debug line does not exist in a Release build, so the reason an icon is missing was never anywhere. Said to the trace (a listener
        // or a debugger shows it, in Release too) and to the crash log, the file that is attached to a bug report, where a repeat of the
        // same text (the window and the notification area both ask for the icon) is counted and not written twice.
        private static void Report(Exception reason)
        {
            System.Diagnostics.Trace.TraceWarning("Icon not loaded: " + reason.Message);
            CrashLog.TryAppend(CrashLog.DefaultPath, CrashLog.Entry(reason, DateTimeOffset.Now, AppVersion.Display, fatal: false));
        }
    }
}
