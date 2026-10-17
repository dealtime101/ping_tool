namespace PingTool
{
    // "Compare with another log": the questions that go before the page, in one window (they used to be a box with Yes, No and Cancel for the
    // order, and nothing for the rest).
    internal sealed class ComparePeriodsForm : Form
    {
        private readonly RadioButton openedFirst, otherFirst;
        private readonly CheckBox openedPart, otherPart, mask;
        private readonly DateTimePicker openedFrom, openedTo, otherFrom, otherTo;
        private readonly List<(string OtherHost, ComboBox Choice)> pairs = new();
        private const string Format = "yyyy-MM-dd HH:mm";

        public ComparisonChoice? Result { get; private set; }

        public ComparePeriodsForm(string openedName, DateTimeOffset openedStart, DateTimeOffset openedEnd, IReadOnlyList<string> openedHosts,
            string otherName, DateTimeOffset otherStart, DateTimeOffset otherEnd, IReadOnlyList<string> otherHosts)
        {
            // Positions and sizes are written for 96 DPI: the form scales them to the screen (as the other windows do).
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = Loc.T("ui.lv.comparetitle");
            ClientSize = new Size(560, 300);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(64, 64, 64);

            Label Caption(string text, int x, int y, int width, int height = 18, Color? color = null) =>
                new() { Text = text, ForeColor = color ?? Color.White, AutoSize = false, AutoEllipsis = true, Location = new Point(x, y), Size = new Size(width, height), UseMnemonic = false };

            int y = 12;
            Controls.Add(Caption(Loc.T("ui.cmp.which"), 12, y, 536));
            openedFirst = new RadioButton { Text = Loc.T("ui.cmp.before.opened", openedName), ForeColor = Color.White, Checked = true, AutoSize = false, AutoEllipsis = true, Location = new Point(24, y + 22), Size = new Size(524, 22), AccessibleName = Loc.T("ui.cmp.before.opened.an") };
            otherFirst = new RadioButton { Text = Loc.T("ui.cmp.before.other", otherName), ForeColor = Color.White, AutoSize = false, AutoEllipsis = true, Location = new Point(24, y + 44), Size = new Size(524, 22), AccessibleName = Loc.T("ui.cmp.before.other.an") };
            Controls.Add(openedFirst);
            Controls.Add(otherFirst);
            y += 80;

            (CheckBox, DateTimePicker, DateTimePicker) Part(string key, DateTimeOffset start, DateTimeOffset end, string anKey)
            {
                var box = new CheckBox { Text = Loc.T(key, Stamp(start), Stamp(end)), ForeColor = Color.White, AutoSize = false, AutoEllipsis = true, Location = new Point(12, y), Size = new Size(536, 22), AccessibleName = Loc.T(anKey) };
                var from = Picker(start, end, start, 90, y + 26, Loc.T("ui.cmp.from.an"));
                var to = Picker(start, end, end, 330, y + 26, Loc.T("ui.cmp.to.an"));
                Controls.Add(box);
                Controls.Add(Caption(Loc.T("ui.cmp.from"), 24, y + 29, 60));
                Controls.Add(from);
                Controls.Add(Caption(Loc.T("ui.cmp.to"), 276, y + 29, 50));
                Controls.Add(to);
                box.CheckedChanged += (_, _) => { from.Enabled = to.Enabled = box.Checked; };
                from.Enabled = to.Enabled = false;
                y += 62;
                return (box, from, to);
            }

            (openedPart, openedFrom, openedTo) = Part("ui.cmp.part.opened", openedStart, openedEnd, "ui.cmp.part.opened.an");
            (otherPart, otherFrom, otherTo) = Part("ui.cmp.part.other", otherStart, otherEnd, "ui.cmp.part.other.an");

            // What only one of the logs has: a device that was replaced has another address after the change, and is then in neither pair.
            var (onlyOpened, onlyOther) = PeriodComparison.Unmatched(openedHosts, otherHosts);
            if (onlyOpened.Count > 0 && onlyOther.Count > 0)
            {
                Controls.Add(Caption(Loc.T("ui.cmp.pair.help"), 12, y, 536, 34, Color.Silver));
                y += 38;
                var panel = new Panel { Location = new Point(12, y), Size = new Size(536, 84), AutoScroll = true, BackColor = Color.FromArgb(64, 64, 64) };
                int py = 0;
                foreach (string other in onlyOther)
                {
                    panel.Controls.Add(Caption(Loc.T("ui.cmp.pair.row", other), 0, py + 4, 300));
                    var choice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(306, py), Size = new Size(210, 23), AccessibleName = Loc.T("ui.cmp.pair.an", other) };
                    choice.Items.Add(Loc.T("ui.cmp.pair.none"));
                    foreach (string o in onlyOpened) choice.Items.Add(o);
                    choice.SelectedIndex = 0;
                    panel.Controls.Add(choice);
                    pairs.Add((other, choice));
                    py += 28;
                }

                Controls.Add(panel);
                y += 92;
            }

            mask = new CheckBox { Text = Loc.T("ui.cmp.mask"), ForeColor = Color.White, AutoSize = false, AutoEllipsis = true, Location = new Point(12, y), Size = new Size(536, 22) };
            Controls.Add(mask);
            y += 34;

            var ok = new Button { Text = Loc.T("ui.cmp.ok"), Location = new Point(366, y), Size = new Size(90, 28) };
            var cancel = new Button { Text = Loc.T("ui.cancel"), DialogResult = DialogResult.Cancel, Location = new Point(462, y), Size = new Size(86, 28) };
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(ok);
            Controls.Add(cancel);
            ClientSize = new Size(560, y + 40);
            ok.Click += (_, _) => Accept();
        }

        private static string Stamp(DateTimeOffset t) => t.ToLocalTime().ToString(Format, System.Globalization.CultureInfo.CurrentCulture);

        // A date and time inside the span of the log (a picker refuses a value outside its own limits, and has limits of its own).
        private DateTimePicker Picker(DateTimeOffset start, DateTimeOffset end, DateTimeOffset value, int x, int y, string accessibleName)
        {
            DateTime Clamp(DateTimeOffset t) => t.ToLocalTime().DateTime is var d && d < DateTimePicker.MinimumDateTime ? DateTimePicker.MinimumDateTime
                : d > DateTimePicker.MaximumDateTime ? DateTimePicker.MaximumDateTime : d;
            return new DateTimePicker
            {
                Format = DateTimePickerFormat.Custom, CustomFormat = Format, Location = new Point(x, y), Size = new Size(170, 23), AccessibleName = accessibleName,
                MinDate = Clamp(start), MaxDate = Clamp(end), Value = Clamp(value),
            };
        }

        private void Accept()
        {
            DateTimeOffset? Take(CheckBox box, DateTimePicker picker) => box.Checked ? new DateTimeOffset(picker.Value) : null;
            // The end shown as 12:00 is the end of that minute: a ping at 12:00:30 is in.
            DateTimeOffset? TakeEnd(CheckBox box, DateTimePicker picker) => box.Checked ? new DateTimeOffset(picker.Value.AddMinutes(1).AddTicks(-1)) : null;
            if (openedPart.Checked && openedFrom.Value > openedTo.Value || otherPart.Checked && otherFrom.Value > otherTo.Value)
            {
                MessageBox.Show(this, Loc.T("ui.cmp.badrange"), "PingTool");
                return;
            }

            var map = new Dictionary<string, string>(TargetKey.Comparer);
            foreach (var (other, choice) in pairs)
                if (choice.SelectedIndex > 0) map[other] = (string)choice.SelectedItem!;
            // Two targets of the other log cannot be the same target of this one: the second would be merged into the first.
            if (map.Values.Distinct(TargetKey.Comparer).Count() != map.Count)
            {
                MessageBox.Show(this, Loc.T("ui.cmp.pair.twice"), "PingTool");
                return;
            }

            Result = new ComparisonChoice(openedFirst.Checked, Take(openedPart, openedFrom), TakeEnd(openedPart, openedTo), Take(otherPart, otherFrom), TakeEnd(otherPart, otherTo), map, mask.Checked);
            DialogResult = DialogResult.OK;
        }
    }
}
