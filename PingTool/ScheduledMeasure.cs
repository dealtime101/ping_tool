using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PingTool
{
    // A measurement campaign for the Windows Task Scheduler: "every night at 22:00, probe for 8 hours with this profile and write the
    // report". The task runs PingTool.exe --headless (see HeadlessRunner) for the current user, when that user is logged on.
    internal static class ScheduledMeasure
    {
        public const string Folder = "PingTool";
        public const int MaxNameLength = 40;
        public const int MaxCommandLength = 261;   // the limit of /TR in schtasks

        // "{date}" in a report file name becomes the start of the run, so that each run of a task writes its own report.
        public static string ExpandReportPath(string path, DateTimeOffset start) =>
            path.Replace("{date}", start.ToLocalTime().ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        public static bool TryName(string? input, out string name, out string error)
        {
            name = (input ?? "").Trim();
            error = "";
            if (name.Length == 0) error = "Give the task a name.";
            else if (name.Length > MaxNameLength) error = $"The name is limited to {MaxNameLength} characters.";
            else if (!name.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_')) error = "The name can hold letters, digits, spaces, - and _ only.";
            return error.Length == 0;
        }

        // The command the task runs: PingTool.exe --headless --duration 8h --report "<file>" [--profile "<name>"]
        public static string BuildCommand(string exePath, TimeSpan duration, string reportPath, string? profile)
        {
            string Q(string s) => "\"" + s.Replace("\"", "", StringComparison.Ordinal) + "\"";
            var sb = new StringBuilder(Q(exePath));
            sb.Append(" --headless --duration ").Append(FormatDuration(duration)).Append(" --report ").Append(Q(reportPath));
            if (!string.IsNullOrEmpty(profile)) sb.Append(" --profile ").Append(Q(profile));
            return sb.ToString();
        }

        // In whole hours when it is, else minutes: the units StartupOptions.TryParseDuration reads back.
        public static string FormatDuration(TimeSpan d) =>
            d.TotalMinutes % 60 == 0 ? ((int)d.TotalHours).ToString(CultureInfo.InvariantCulture) + "h" : ((int)d.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m";

        // The arguments of `schtasks /Create`, one per item (no shell quoting to get wrong): the days are the ones ticked, every day being a
        // daily task. Null with the reason when the task cannot be made as asked.
        public static List<string>? CreateArguments(string name, string command, TimeSpan startTime, IReadOnlyCollection<DayOfWeek> days, out string error)
        {
            error = "";
            if (!TryName(name, out string taskName, out error)) return null;
            if (days.Count == 0) { error = "Tick at least one day."; return null; }
            if (command.Length > MaxCommandLength)
            {
                error = $"The command is {command.Length.ToString(CultureInfo.CurrentCulture)} characters long and the Task Scheduler takes {MaxCommandLength} at most: choose a shorter report folder or profile name.";
                return null;
            }

            var args = new List<string> { "/Create", "/TN", Folder + "\\" + taskName, "/TR", command, "/F" };
            string[] codes = { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" };
            if (days.Distinct().Count() == 7) args.AddRange(new[] { "/SC", "DAILY" });
            else args.AddRange(new[] { "/SC", "WEEKLY", "/D", string.Join(",", days.Distinct().OrderBy(d => ((int)d + 6) % 7).Select(d => codes[(int)d])) });   // Monday first
            args.AddRange(new[] { "/ST", startTime.ToString(@"hh\:mm", CultureInfo.InvariantCulture) });
            return args;
        }

        // Runs schtasks; the text it answers (or why it could not run) and whether it worked.
        public static (bool Ok, string Text) Run(IReadOnlyList<string> arguments)
        {
            var info = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (string a in arguments) info.ArgumentList.Add(a);
            try
            {
                using var process = Process.Start(info)!;
                string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                if (!process.WaitForExit(15000)) { process.Kill(); return (false, "The Task Scheduler did not answer in time."); }
                return (process.ExitCode == 0, output.Trim());
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return (false, "Could not run schtasks.exe: " + ex.Message);
            }
        }
    }
}
