namespace PingTool
{
    // "Schedule a measurement": a task of the Windows Task Scheduler that runs PingTool without a window at a given time, on the days
    // ticked, for a given time, with a saved profile (or the saved list of targets), and writes an HTML report per run. See ScheduledMeasure.
    internal sealed class ScheduleForm : Form
    {
        private readonly TextBox name, folder;
        private readonly DateTimePicker time;
        private readonly NumericUpDown hours;
        private readonly ComboBox profile;
        private readonly CheckBox[] days = new CheckBox[7];
        private const string NoProfile = "(the saved list of targets)";

        public ScheduleForm(IReadOnlyList<string> profileNames, string defaultFolder)
        {
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = "PingTool - Schedule a measurement";
            ClientSize = new Size(420, 372);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(64, 64, 64);

            Label Caption(string text, int x, int y) => new() { Text = text, ForeColor = Color.White, AutoSize = true, Location = new Point(x, y) };

            Controls.Add(Caption("&Task name", 12, 12));
            name = new TextBox { Location = new Point(12, 32), Size = new Size(396, 23), MaxLength = ScheduledMeasure.MaxNameLength, Text = "Night watch", AccessibleName = "Name of the task" };
            Controls.Add(name);

            Controls.Add(Caption("&Start at", 12, 66));
            time = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Location = new Point(12, 86), Size = new Size(90, 23), Value = DateTime.Today.AddHours(22), AccessibleName = "Time of day to start" };
            Controls.Add(time);
            Controls.Add(Caption("&Hours to measure", 130, 66));
            hours = new NumericUpDown { Minimum = 1, Maximum = 24 * 30, Value = 8, Location = new Point(130, 86), Size = new Size(90, 23), AccessibleName = "Hours to measure" };
            Controls.Add(hours);

            Controls.Add(Caption("Days", 12, 122));
            string[] labels = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
            int x = 12;
            foreach (int d in new[] { 1, 2, 3, 4, 5, 6, 0 })   // Monday first
            {
                days[d] = new CheckBox { Text = labels[d], ForeColor = Color.White, AutoSize = true, Checked = true, Location = new Point(x, 142), AccessibleName = labels[d] };
                Controls.Add(days[d]);
                x += 56;
            }

            Controls.Add(Caption("&Profile (its targets and limits)", 12, 178));
            profile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(12, 198), Size = new Size(396, 23), AccessibleName = "Saved profile to measure with" };
            profile.Items.Add(NoProfile);
            foreach (string p in profileNames) profile.Items.Add(p);
            profile.SelectedIndex = 0;
            Controls.Add(profile);

            Controls.Add(Caption("&Folder for the reports (one file per run)", 12, 234));
            folder = new TextBox { Location = new Point(12, 254), Size = new Size(396, 23), Text = defaultFolder, AccessibleName = "Folder for the reports" };
            Controls.Add(folder);

            Controls.Add(new Label
            {
                Text = "The task runs when you are logged on, with no window; it writes pingtool-<date>.html in that folder and its exit code says whether an incident was seen. "
                    + "Remove it later from the Task Scheduler (folder PingTool). The targets are the ones saved when you click Create.",
                ForeColor = Color.Silver, AutoSize = false, Location = new Point(12, 288), Size = new Size(396, 48), AccessibleName = "What the task does",
            });

            var create = new Button { Text = "Create", Location = new Point(230, 336), Size = new Size(84, 28) };
            var cancel = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Location = new Point(324, 336), Size = new Size(84, 28) };
            AcceptButton = create;
            CancelButton = cancel;
            Controls.Add(create);
            Controls.Add(cancel);
            create.Click += (_, _) => Create();
        }

        private void Create()
        {
            string dir = folder.Text.Trim();
            if (dir.Length == 0 || dir.Any(char.IsControl) || !Path.IsPathFullyQualified(dir))
            {
                MessageBox.Show(this, "Give the full path of a folder for the reports, for example C:\\PingTool\\reports.", "PingTool");
                return;
            }

            string report = Path.Combine(dir, "pingtool-{date}.html");
            string command = ScheduledMeasure.BuildCommand(Application.ExecutablePath, TimeSpan.FromHours((double)hours.Value), report,
                profile.SelectedIndex > 0 ? (string)profile.SelectedItem! : null);
            var chosen = Enumerable.Range(0, 7).Where(d => days[d].Checked).Select(d => (DayOfWeek)d).ToList();
            var args = ScheduledMeasure.CreateArguments(name.Text, command, time.Value.TimeOfDay, chosen, out string error);
            if (args is null) { MessageBox.Show(this, error, "PingTool"); return; }

            var (ok, text) = ScheduledMeasure.Run(args);
            MessageBox.Show(this, ok ? "The task was created in the Task Scheduler (folder PingTool).\r\n\r\n" + text : "The task could not be created.\r\n\r\n" + text,
                "PingTool", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            if (ok) DialogResult = DialogResult.OK;
        }
    }
}
