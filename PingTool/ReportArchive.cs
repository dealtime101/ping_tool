using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace PingTool
{
    // What a report can be saved as: the HTML file alone, or an archive (zip) for someone who will look at the numbers themselves -
    // the report, the measures (every ping, as the CSV export writes them) and the probe settings, and nothing else: no webhook address,
    // no log folder, nothing of settings.json that is not a probe setting. The data given here is already masked, if the sender chose so.
    internal static class ReportArchive
    {
        public const string ReportName = "report.html";
        public const string MeasuresName = "measures.csv";
        public const string SettingsName = "probe-settings.txt";

        // Writes the report where the person chose. `zip` = the archive; the extension follows the choice (a name typed as .html with the
        // archive filter picked would otherwise hold a zip under an .html name). Returns the path written.
        public static string Save(string path, bool zip, ReportData masked, IEnumerable<LogEntry> entries, long dropped = 0)
        {
            string wanted = zip ? ".zip" : ".html";
            if (!string.Equals(Path.GetExtension(path), wanted, StringComparison.OrdinalIgnoreCase))
                path = Path.GetExtension(path).Length == 0 ? path + wanted : Path.ChangeExtension(path, wanted);

            // Whole or not at all: an earlier report under that name is not emptied before the new one is complete.
            if (zip) AtomicFile.Write(path, file => Write(file, masked, entries, dropped));
            else AtomicFile.WriteAllText(path, ReportBuilder.Build(masked));

            return path;
        }

        public static void Write(Stream output, ReportData masked, IEnumerable<LogEntry> entries, long dropped = 0)
        {
            // The report is built BEFORE the archive is opened: a report that cannot be built must not leave a half archive.
            string html = ReportBuilder.Build(masked);
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

            Add(zip, ReportName, new UTF8Encoding(false), masked.GeneratedAt, w => w.Write(html));
            Add(zip, MeasuresName, new UTF8Encoding(true), masked.GeneratedAt, w =>
            {
                w.Write(PingLog.CsvHeader + "\r\n");
                if (dropped > 0) w.Write(PingLog.IncompleteRow(dropped) + "\r\n");
                foreach (var e in entries) w.Write(PingLog.CsvLine(e) + "\r\n");
            });
            Add(zip, SettingsName, new UTF8Encoding(false), masked.GeneratedAt, w => w.Write(SettingsText(masked)));
        }

        private static void Add(ZipArchive zip, string name, Encoding encoding, DateTimeOffset time, Action<TextWriter> write)
        {
            // A zip dates its files from 1980 on: a date before is refused by the format.
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = time < new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) : time;
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, encoding);
            write(writer);
        }

        // The probe settings the measures were taken with and the limits a host is judged by, in words: what a reader needs to read the
        // numbers. A report rebuilt from log files does not know the probe settings, and says so instead of inventing them.
        public static string SettingsText(ReportData d)
        {
            var c = CultureInfo.InvariantCulture;
            var t = new StringBuilder();
            t.Append("PingTool ").Append(d.Version).Append("\r\n");
            t.Append("Period: ").Append(d.RunStart.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", c)).Append(" to ")
                .Append((d.PeriodEnd ?? d.GeneratedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", c)).Append("\r\n\r\n");

            if (d.FromLogFile)
                t.Append("Probe settings: not recorded (this report was rebuilt from log files).\r\n");
            else
                t.Append(c, $"Probe settings: one echo request every {d.IntervalMs} ms, timeout {d.TimeoutMs} ms, {d.PacketSize} bytes.\r\n");

            t.Append(c, $"Degraded when (last {HostMonitor.WindowSize} pings): loss at least {d.DegradedLossPercent} % or average latency at least {d.DegradedLatencyMs} ms")
                .Append(d.DegradedMos > 0 ? $" or voice quality (MOS) below {d.DegradedMos.ToString("0.0", c)}" : "").Append(".\r\n");
            t.Append(c, $"Down when: {d.DownAfter} failed pings in a row.\r\n");
            if (d.FromLogFile) t.Append("(The limits above are the defaults: a log does not record them.)\r\n");
            if (d.MaskedNote is not null) t.Append("\r\n").Append(d.MaskedNote).Append("\r\n");

            t.Append("\r\nTargets:\r\n");
            foreach (var x in d.Hosts)
                t.Append("  ").Append(x.Address).Append(": ").Append(x.Limits ?? (d.FromLogFile ? "limits not recorded" : "global limits")).Append("\r\n");

            t.Append("\r\nFiles: ").Append(ReportName).Append(" (the report), ").Append(MeasuresName).Append(" (every ping: time, target, status, round trip in ms, detail), ")
                .Append(SettingsName).Append(" (this file). Nothing else of the sender's settings is included.\r\n");
            return t.ToString();
        }
    }
}
