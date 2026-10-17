namespace PingTool
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            // A bad option is said in a box and nothing starts: a shortcut that silently ignored a typo
            // would monitor the wrong thing without anybody noticing. With --headless nobody is there to click the
            // box (a scheduled task would wait for ever): the message goes to the error output, and the exit code says it.
            // The handler of last resort comes first, so that nothing below can fail without leaving a trace in crash.log.
            AppDomain.CurrentDomain.UnhandledException += (_, e) => OnFatal(e.ExceptionObject);
            // An error that came again and again is written at its 2nd, 10th, 100th time; the count reached since is said when the process ends.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => CrashLog.FlushRepeats();

            bool headlessAsked = args.Any(a => string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase));
            if (!StartupOptions.TryParse(args, out var startup, out string message))
            {
                // --help comes back here too, with the usage text: that is not an error and must not wear the error icon, the error
                // output or the error exit code. A script that asks for the help redirects the standard output and reads a 0.
                bool help = message == StartupOptions.Usage;
                if (headlessAsked) (help ? Console.Out : Console.Error).WriteLine(message);
                else
                {
                    MessageBox.Show(message, help ? "PingTool - help" : "PingTool - invalid command line", MessageBoxButtons.OK,
                        help ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                }

                return HeadlessRunner.ExitCodeForStartupMessage(message);
            }

            // No window at all: not even the WinForms initialisation.
            if (startup.Headless) return RunHeadless(startup);

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
            // The windows are dark: the system controls (lists, combo boxes, buttons, scroll bars) follow (PIN461.21, .53, .67, .96).
#pragma warning disable WFO5001 // SetColorMode is still marked experimental
            Application.SetColorMode(SystemColorMode.Dark);
#pragma warning restore WFO5001

            // An error in the window's own thread (an event handler, an await that resumed there) is logged and PingTool carries on:
            // a monitoring stopped by one stray exception would be worse than the error. Before any window exists, as required.
            guiStarted = true;
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => OnWindowThreadError(e.Exception);

            if (startup.TestWebhooks)
            {
                var (text, code) = TestWebhooks();
                // The icon and the title tell the outcome before the text is read: a webhook that did not answer is an alert that will
                // not arrive, which the box must not show as it shows a success.
                MessageBox.Show(text, WebhookTest.Title(code), MessageBoxButtons.OK, code == HeadlessRunner.OkCode ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                return code;   // 0 only when every webhook answered (a script can rely on it)
            }

            // What PingTool reads of the Wi-Fi, to be put next to "netsh wlan show interfaces": the check of the offsets on a real card.
            if (startup.WifiCheck)
            {
                var reading = WifiWatch.Read();
                MessageBox.Show(WifiWatch.Describe(reading), Loc.T("ui.wifi.title"), MessageBoxButtons.OK,
                    reading.State is null && reading.Unavailable is not null ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                return reading.State is null && reading.Unavailable is not null ? 1 : 0;
            }

            Application.Run(new MainForm(startup));
            return 0;
        }

        private static bool guiStarted;
        // 1 once the box of OnWindowThreadError was shown; taken with Interlocked so that two errors at once show it once, whichever thread
        // runs the handler (today only the window thread does: ThreadException is raised on the thread of the failing message loop).
        private static int windowErrorShown;

        // The process is going down (an exception nobody caught on another thread): write what is known, say so where someone can read it.
        private static void OnFatal(object? exception)
        {
            // The log first (the one thing that must not be lost), then what is said to the user: nothing in this method may throw, an
            // exception here would replace the error being reported by another one, and the log of it would not exist.
            bool saved = false;
            try { saved = CrashLog.TryAppend(CrashLog.DefaultPath, CrashLog.Entry(exception, DateTimeOffset.Now, AppVersion.Display, fatal: true)); }
            catch (Exception) { }

            string what = CrashLog.Summary(exception);
            string where = saved ? "Details: " + CrashLog.DefaultPath : "The details could not be saved.";
            try
            {
                if (guiStarted) MessageBox.Show("PingTool hit an unexpected error and must close.\r\n\r\n" + what + "\r\n\r\n" + where, "PingTool - unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                else Console.Error.WriteLine("Unexpected error: " + what + ". " + where);
            }
            catch (Exception)
            {
                // The box could not be shown (no desktop to show it on, a thread that cannot pump messages): the line goes to the error output.
                try { Console.Error.WriteLine("Unexpected error: " + what + ". " + where); } catch (Exception) { }
            }
        }

        // Every error is written to the log; the box is shown once per run (a loop failing every second must not bury the window in boxes).
        private static void OnWindowThreadError(Exception exception)
        {
            // As in OnFatal, nothing here may throw: the handler of Application.ThreadException has nobody above it, so an exception that
            // leaves it (the log that cannot be written in a way TryAppend did not foresee, a box that cannot be shown) ends the process and
            // takes the error that was being reported with it, when the whole point of this handler is that PingTool carries on.
            bool saved = false;
            try { saved = CrashLog.TryAppend(CrashLog.DefaultPath, CrashLog.Entry(exception, DateTimeOffset.Now, AppVersion.Display, fatal: false)); }
            catch (Exception) { }

            if (Interlocked.Exchange(ref windowErrorShown, 1) != 0) return;
            string what = "an error that could not be described";
            try { what = CrashLog.Summary(exception); }
            catch (Exception) { }

            string message = "PingTool hit an unexpected error and carries on; the monitoring may be incomplete. Later errors of this kind are only logged.\r\n\r\n"
                + what + "\r\n\r\n" + (saved ? "Details: " + CrashLog.DefaultPath : "The details could not be saved.");
            try { MessageBox.Show(message, "PingTool - unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            catch (Exception)
            {
                // The box could not be shown (no desktop to show it on): the line goes to the error output, where a console would read it.
                try { Console.Error.WriteLine("Unexpected error: " + what); } catch (Exception) { }
            }
        }

        // "PingTool.exe --headless --duration 8h --report night.html [targets]": see HeadlessRunner. Everything it has to say goes to
        // the standard output (the summary) or the error output (problems); the exit code is 0 / 1 / 2 as documented.
        private static int RunHeadless(StartupOptions o) => HeadlessRunner.Guarded(() => RunHeadlessCore(o), ex =>
            CrashLog.TryAppend(CrashLog.DefaultPath, CrashLog.Entry(ex, DateTimeOffset.Now, AppVersion.Display, fatal: true)));

        private static int RunHeadlessCore(StartupOptions o)
        {
            try
            {
                var settings = Settings.Load(Settings.DefaultPath);
                if (settings.LoadProblem is { } loadProblem) Console.Error.WriteLine(loadProblem);

                // --profile: the settings of a saved profile for this run (the file is not changed); what the command line says wins.
                Profile? profile = null;
                if (o.ProfileName is { } wanted)
                {
                    profile = ProfileBook.Find(settings.Profiles, wanted);
                    if (profile is null)
                    {
                        Console.Error.WriteLine($"No saved profile is named \"{wanted}\" in {Settings.DefaultPath}.");
                        return HeadlessRunner.ErrorCode;
                    }

                    ProfileBook.ApplyTo(settings, profile);
                }

                if (o.IntervalMs is int ms) settings.IntervalMs = Limits.Clamp(ms, Limits.IntervalMs);

                var found = new List<string>();
                if (o.Diagnose)
                {
                    found = DiagnosticTargets.Discover(out bool gatewayFound, out string noGatewayText);
                    if (!gatewayFound) Console.Error.WriteLine(noGatewayText);
                }

                var hosts = found.Concat(o.Hosts).ToList();
                if (hosts.Count == 0) hosts = profile?.Hosts ?? settings.Hosts;

                using var webhooks = settings.Webhooks.Count == 0 ? null
                    : new WebhookSender(settings.Webhooks.Select(w => WebhookPayload.TryParseUrl(w, out var url) ? url : null).OfType<Uri>(), AppVersion.Number);
                if (webhooks is not null) webhooks.Failed += (label, reason) => Console.Error.WriteLine($"Alert not delivered to the webhook {label}: {reason}");

                // Before anything is probed: a night of measures must not end without a report because the path was never writable.
                string? reportPath = null;
                if (o.ReportPath is not null)
                {
                    if (!HeadlessRunner.TryPrepareReportPath(o.ReportPath, DateTimeOffset.Now, out string prepared, out string reportProblem))
                    {
                        Console.Error.WriteLine(reportProblem);
                        return HeadlessRunner.ErrorCode;
                    }

                    reportPath = prepared;
                }

                bool reportFailed = false;
                var pseudonyms = new List<string>();   // --hide: what the pseudonyms of the last report stand for (never in the report itself)
                void WriteReport(ReportData data)
                {
                    if (reportPath is null) return;
                    try
                    {
                        if (o.Hide is { } hide) data = ReportAnonymizer.Apply(data, hide, out pseudonyms);
                        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                        AtomicFile.WriteAllText(reportPath, ReportBuilder.Build(data));   // the report of the last checkpoint stays whole if this write fails
                        reportFailed = false;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                    {
                        reportFailed = true;
                        Console.Error.WriteLine("Could not write the report: " + ex.Message);
                    }
                }

                async Task<ProbeOutcome> Probe(ProbeTarget target, int timeoutMs, CancellationToken token)
                {
                    using var ping = new System.Net.NetworkInformation.Ping();
                    return await ProbeRunner.RunAsync(target, timeoutMs, ping, new byte[settings.PacketSize], token);
                }

                // Ctrl+C (or Ctrl+Break) ends the monitoring early instead of killing the process: the report is written with what was
                // seen so far, the summary is printed, and the exit code says what was observed, as at the end of the duration.
                using var interrupted = new CancellationTokenSource();
                ConsoleCancelEventHandler onInterrupt = (_, e) =>
                {
                    e.Cancel = true;   // the process goes on to finish properly
                    if (!interrupted.IsCancellationRequested) Console.Error.WriteLine("Interrupted: writing the report with what was seen so far.");
                    interrupted.Cancel();
                };
                Console.CancelKeyPress += onInterrupt;
                HeadlessResult result;
                try
                {
                    result = HeadlessRunner.RunAsync(hosts, settings, o.Duration!.Value, Probe, WriteReport, alert: webhooks is null ? null : webhooks.Send,
                        stop: interrupted.Token).GetAwaiter().GetResult();
                }
                finally
                {
                    Console.CancelKeyPress -= onInterrupt;
                }
                Console.WriteLine(result.Summary + HeadlessRunner.ReportNote(reportPath, reportFailed));
                if (pseudonyms.Count > 0) Console.Error.WriteLine("What the pseudonyms of the report stand for (keep this: it is not in the report):\r\n" + string.Join("\r\n", pseudonyms));
                return reportFailed ? HeadlessRunner.ErrorCode : result.ExitCode;
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return HeadlessRunner.ErrorCode;
            }
        }

        // How long the test of the webhooks goes on before a window says that it is going on (see TestWebhooks).
        internal const int WaitingShownAfterMs = 500;

        // A small window with a bar that moves, until `work` is over (it closes itself then). No button and no close box: the test is the
        // only thing this process does, and it ends by itself within the time limits of the sender.
        private static void ShowWaiting(Task work, int webhookCount)
        {
            using var waiting = new Form
            {
                Text = "PingTool",
                AutoScaleDimensions = new SizeF(96F, 96F),
                AutoScaleMode = AutoScaleMode.Dpi,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                ControlBox = false,
                MinimizeBox = false,
                MaximizeBox = false,
                StartPosition = FormStartPosition.CenterScreen,
                ClientSize = new Size(380, 96),
            };
            waiting.Controls.Add(new Label
            {
                Text = $"Sending a test alert to {webhookCount} webhook(s)...\r\nA receiver that does not answer can take up to about 20 seconds.",
                UseMnemonic = false,
                AutoSize = false,
                Location = new Point(12, 10),
                Size = new Size(356, 44),
            });
            waiting.Controls.Add(new ProgressBar { Style = ProgressBarStyle.Marquee, Location = new Point(12, 62), Size = new Size(356, 18) });
            waiting.Shown += (_, _) =>
            {
                if (work.IsCompleted) { waiting.Close(); return; }
                work.ContinueWith(_ =>
                {
                    try { waiting.BeginInvoke(waiting.Close); }
                    catch (InvalidOperationException) { }   // the window is already gone: nothing left to close
                }, TaskScheduler.Default);
            };
            waiting.ShowDialog();
        }

        // "PingTool.exe --test-webhooks": one test alert to every webhook of settings.json, the answer of each
        // in a box. The alert goes out exactly as a real one does (same body, same retries), so a typo in an
        // address or a refused request shows up here and not in the middle of the night. Up to ~20 s when a
        // receiver does not answer (time limit and retries), during which a window says so; the addresses themselves are never shown.
        private static (string Text, int Code) TestWebhooks()
        {
            var settings = Settings.Load(Settings.DefaultPath);
            if (settings.Webhooks.Count == 0)
                return (WebhookTest.Summary(settings.LoadProblem, settings.WebhooksIgnored, 0, Array.Empty<WebhookResult>(), Settings.DefaultPath), HeadlessRunner.ErrorCode);

            var urls = settings.Webhooks.Select(w => WebhookPayload.TryParseUrl(w, out var url) ? url : null).OfType<Uri>();
            using var sender = new WebhookSender(urls, AppVersion.Number);
            var test = new WebhookEvent("webhook-test", HostChange.None,
                "PingTool webhook test: if you read this, alerts will reach this channel.", null, DateTimeOffset.Now);
            // The sending runs apart, so that the thread of the window can show that something is going on: a receiver that does not answer
            // takes up to ~20 s, and a shortcut that shows nothing for that long looks like a program that hung (it is started again, or killed).
            // The window only comes if the answers are not back after a short while: when all answer at once there is no flash of a window.
            var sending = Task.Run(() => sender.SendNowAsync(test));
            if (Task.WaitAny(new Task[] { sending }, WaitingShownAfterMs) < 0) ShowWaiting(sending, settings.Webhooks.Count);
            var results = sending.GetAwaiter().GetResult();
            return (WebhookTest.Summary(settings.LoadProblem, settings.WebhooksIgnored, settings.Webhooks.Count, results, Settings.DefaultPath),
                WebhookTest.ExitCode(settings.WebhooksIgnored, settings.Webhooks.Count, results));
        }
    }
}