using System.Globalization;

namespace PingTool
{
    // A recorded night, read back from PingTool log files (see LogReplay): the same timeline, incidents and HTML report
    // as live, for a session that is over - the application closed, or crashed, in the meantime.
    internal sealed class LogViewerForm : Form
    {
        private readonly ReplayResult replay;

        // `entries` is only read here, in the constructor and the handlers it creates (they capture the parameter): not a field.
        public LogViewerForm(ReplayResult replay, List<LogEntry> entries, string sources)
        {
            this.replay = replay;

            // Positions and sizes below are written for 96 DPI: the form scales them to the screen (like the main window and the timeline
            // do), so that the text, which does grow with the scaling, keeps its room at 125 %, 150 % or 200 %.
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = "PingTool - Log";
            ClientSize = new Size(560, 250);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(64, 64, 64);

            var c = CultureInfo.CurrentCulture;
            string When(DateTimeOffset t) => t.ToLocalTime().ToString("G", c);
            var hosts = replay.Sessions.Keys.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();
            string text = string.Join("\n", new[]
            {
                sources,
                $"{When(replay.From)} to {When(replay.To)} ({IncidentLog.FormatDuration(replay.To - replay.From)})",
                $"{entries.Count.ToString("N0", c)} pings on {hosts.Count.ToString(c)} target(s): {string.Join(", ", hosts.Take(4))}{(hosts.Count > 4 ? ", ..." : "")}",
                replay.Incidents.Summary(replay.To),
                "Incidents are detected again from the pings with the default limits (slow above "
                    + HostMonitor.DefaultLatencyMs.ToString(c) + " ms, loss at least " + HostMonitor.DefaultLossPercent.ToString(c) + " %, down after "
                    + HostMonitor.DefaultDownAfter.ToString(c) + " failures): the log does not record the ones used at the time.",
            });
            // A read-only box that scrolls, not a label of a fixed height: the sources can be several long paths, and the sentence on the
            // limits comes last, so a label cut the end off with no way to read it. (A TextBox breaks lines on CR LF only.)
            var info = new TextBox
            {
                Text = text.Replace("\n", "\r\n", StringComparison.Ordinal),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(64, 64, 64),
                ForeColor = Color.White,
                Location = new Point(12, 12),
                Size = new Size(536, 150),
                AccessibleName = "The log that was opened",
            };

            var timeline = new Button { Text = "Session timeline...", Location = new Point(12, 176), Size = new Size(170, 28) };
            var incidents = new Button { Text = "Incidents...", Location = new Point(190, 176), Size = new Size(170, 28) };
            var report = new Button { Text = "Save report (HTML)...", Location = new Point(368, 176), Size = new Size(180, 28) };
            var compare = new Button { Text = "Compare with another log...", Location = new Point(12, 212), Size = new Size(220, 28) };
            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Location = new Point(458, 212), Size = new Size(90, 28) };
            CancelButton = close;

            compare.Click += async (_, _) =>
            {
                compare.Enabled = false;
                try { await CompareWith(entries, sources); }
                finally { compare.Enabled = true; }
            };

            timeline.Click += (_, _) =>
            {
                using var dialog = new TimelineForm(entries, replay.Incidents.Incidents.ToList(), hosts, hosts[0]);
                dialog.ShowDialog(this);
            };
            incidents.Click += (_, _) =>
            {
                using var dialog = new IncidentsForm(replay.Incidents.Incidents, replay.Incidents.Summary(replay.To), replay.To);
                dialog.ShowDialog(this);
            };
            report.Click += (_, _) => SaveReport(entries);

            Controls.AddRange(new Control[] { info, timeline, incidents, report, compare, close });
        }

        // Two periods side by side: this log and another one the person picks (a night before and a night after a new router, a cable, a
        // provider). The page opens in the browser like the report; see PeriodComparison for what it says and what it warns about.
        private async Task CompareWith(List<LogEntry> entries, string sources)
        {
            using var open = new OpenFileDialog { Filter = "PingTool log (*.csv)|*.csv|All files (*.*)|*.*", Multiselect = true, Title = "The other log, to compare with " + sources };
            if (open.ShowDialog(this) != DialogResult.OK) return;

            string[] paths = open.FileNames;
            Cursor = Cursors.WaitCursor;
            try
            {
                var (other, otherEntries, error) = await Task.Run(() => MainForm.LoadLogs(paths));
                if (other is null || otherEntries is null) { MessageBox.Show(error, "PingTool"); return; }

                string otherName = paths.Length == 1 ? Path.GetFileName(paths[0]) : paths.Length.ToString(CultureInfo.CurrentCulture) + " log files";
                var answer = MessageBox.Show(this,
                    $"Which one is the period BEFORE the change?\r\n\r\nYes: the log you opened ({sources}) is before, {otherName} is after.\r\nNo: {otherName} is before, the log you opened is after.",
                    "PingTool - compare two periods", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return;

                var mine = PeriodComparison.Measure(sources, replay, entries);
                var theirs = PeriodComparison.Measure(otherName, other, otherEntries);
                string html = answer == DialogResult.Yes ? PeriodComparison.BuildHtml(mine, theirs, DateTimeOffset.Now) : PeriodComparison.BuildHtml(theirs, mine, DateTimeOffset.Now);

                using var save = new SaveFileDialog { Filter = "HTML page (*.html)|*.html", FileName = $"pingtool-comparison-{DateTime.Now:yyyyMMdd-HHmmss}.html" };
                if (save.ShowDialog(this) != DialogResult.OK) return;

                File.WriteAllText(save.FileName, html, new System.Text.UTF8Encoding(false));
                FileOpener.OfferToOpen(this, "The comparison", save.FileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show("Could not write the file: " + ex.Message, "PingTool");
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void SaveReport(List<LogEntry> entries)
        {
            var sessions = replay.Sessions.Values.OrderBy(s => s.Address, StringComparer.OrdinalIgnoreCase).ToList();
            var data = new ReportData(DateTimeOffset.Now, replay.From, "", AppVersion.Display, 0, 0, 0,
                HostMonitor.DefaultLatencyMs, HostMonitor.DefaultLossPercent,
                sessions.Select(s => new HostReport(s.Address, s.IpText, s.Monitor.State, s.Stats.Sent, s.Stats.Lost,
                    s.Stats.LossPercent, s.Stats.Min, s.Stats.Avg, s.Stats.Max, s.Stats.Jitter, s.History.ToArray(), s.Stats.Hours, Mos: s.Mos)).ToList(),
                Diagnosis.For(sessions.Select(s => s.ToTarget()).ToList()),
                replay.Incidents.Summary(replay.To), replay.Incidents.Incidents.ToList(),
                FromLogFile: true, PeriodEnd: replay.To);

            if (ReportMaskingForm.Ask(this, data) is not { } masking) return;
            var masked = ReportAnonymizer.Mask(data, masking);

            using var dialog = new SaveFileDialog
            {
                Filter = "HTML report (*.html)|*.html|Archive: report, measures and probe settings (*.zip)|*.zip",
                FileName = $"pingtool-report-{replay.From.ToLocalTime():yyyyMMdd-HHmmss}.html",
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                string saved = ReportArchive.Save(dialog.FileName, dialog.FilterIndex == 2, masked.Data, ReportAnonymizer.MaskEntries(masked, entries));
                ReportMaskingForm.ShowKey(this, masked.Key);
                FileOpener.OfferToOpen(this, "The report", saved);   // where it is, and the offer to open it
            }
            catch (IOException ex)
            {
                MessageBox.Show("Could not write the file: " + ex.Message, "PingTool");
            }
            catch (UnauthorizedAccessException ex)
            {
                MessageBox.Show("Could not write the file: " + ex.Message, "PingTool");
            }
        }
    }
}
