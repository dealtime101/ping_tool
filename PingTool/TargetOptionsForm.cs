namespace PingTool
{
    // "Name and limits of this host" (and the service it belongs to): a readable name, and limits of its own instead of the global ones. A limit left unticked
    // follows the global value (shown greyed). Limits apply from the next Start; the name at once.
    internal sealed class TargetOptionsForm : Form
    {
        private readonly TextBox label, service;
        private readonly (CheckBox Own, NumericUpDown Value)[] limits;

        public TargetOptions? Result { get; private set; }

        public TargetOptionsForm(string address, TargetOptions? current, int globalSlow, int globalLoss, int globalDown)
        {
            // Positions and sizes are written for 96 DPI: the form scales them to the screen, so that the text, which does grow with the
            // scaling, keeps its room at 125 %, 150 % or 200 % (as IncidentsForm, TimelineForm and TraceForm do).
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = Loc.T("ui.to.title");
            ClientSize = new Size(380, 330);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(64, 64, 64);

            // A long address (a URL, a full IPv6 address) is cut with "..." and the whole of it comes up as a tooltip (AutoEllipsis does
            // both), instead of being cut off flat: the person must be able to tell which target this is.
            // UseMnemonic = false: an address is shown as it is. With the default, the "&" of a URL (?a=1&b=2) is taken for the mark of a keyboard
            // shortcut and dropped, so the target does not read as the one the person chose.
            Controls.Add(new Label { Text = address, UseMnemonic = false, ForeColor = Color.Silver, AutoSize = false, AutoEllipsis = true, Location = new Point(12, 10), Size = new Size(356, 18), AccessibleName = Loc.T("ui.to.address.an", address) });
            // The & makes the next letter an access key. Two kinds, and they do not do the same: Alt+N (name) and Alt+V (service) belong to
            // a LABEL, which takes the focus to the box that follows it; Alt+S, Alt+L and Alt+D (the three limits) belong to the CHECK BOX
            // "own value" of the row (see Row), so the key ticks or unticks it, which enables the number beside it (the own value is kept in
            // memory while unticked, so pressing it twice loses nothing). Five different letters, so that no key means two things (in
            // each language: the French ones are N, V, L, P and R).
            Controls.Add(new Label { Text = Loc.T("ui.to.name"), ForeColor = Color.White, AutoSize = true, Location = new Point(12, 36) });
            label = new TextBox
            {
                Location = new Point(12, 56), Size = new Size(356, 23), MaxLength = TargetOptions.MaxLabelLength,
                Text = current?.Label ?? "", AccessibleName = Loc.T("ui.to.name.an"),
            };
            Controls.Add(label);

            // Targets with the same service are diagnosed together, whatever their host names (see ServiceDiagnosis): left empty, the
            // targets of one host name are the service.
            Controls.Add(new Label { Text = Loc.T("ui.to.service"), ForeColor = Color.White, AutoSize = true, Location = new Point(12, 86) });
            service = new TextBox
            {
                Location = new Point(12, 106), Size = new Size(356, 23), MaxLength = TargetOptions.MaxLabelLength,
                Text = current?.Service ?? "", AccessibleName = Loc.T("ui.to.service.an"),
            };
            Controls.Add(service);

            limits = new[]
            {
                Row(0, Loc.T("ui.to.slow"), Limits.DegradedLatencyMs, current?.SlowMs, globalSlow),
                Row(1, Loc.T("ui.to.loss"), Limits.DegradedLossPercent, current?.LossPercent, globalLoss),
                Row(2, Loc.T("ui.to.down"), Limits.DownAfter, current?.DownAfter, globalDown),
            };

            // Said where the limits are, not only in the code: a limit changed during a run shows no change, and looks as if it was not saved.
            Controls.Add(new Label
            {
                Text = Loc.T("ui.to.help"),
                ForeColor = Color.Silver, AutoSize = false, Location = new Point(12, 234), Size = new Size(356, 48),
                AccessibleName = Loc.T("ui.to.help.an"),
            });

            var ok = new Button { Text = Loc.T("ui.ok"), DialogResult = DialogResult.OK, Location = new Point(190, 290), Size = new Size(84, 28) };
            var cancel = new Button { Text = Loc.T("ui.cancel"), DialogResult = DialogResult.Cancel, Location = new Point(284, 290), Size = new Size(84, 28) };
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(ok);
            Controls.Add(cancel);
            ok.Click += (_, _) => Result = TargetOptions.Normalize(new TargetOptions(
                label.Text,
                limits[0].Own.Checked ? (int)limits[0].Value.Value : null,
                limits[1].Own.Checked ? (int)limits[1].Value.Value : null,
                limits[2].Own.Checked ? (int)limits[2].Value.Value : null,
                service.Text));
        }

        // One limit: "own value" box + the number (greyed and showing the global value while unticked).
        private (CheckBox, NumericUpDown) Row(int index, string text, (int Min, int Max) range, int? own, int global)
        {
            int y = 142 + index * 30;
            string plain = text.Replace("&", "", StringComparison.Ordinal);   // what a screen reader says: without the access key mark
            var box = new CheckBox { Text = text, ForeColor = Color.White, AutoSize = true, Location = new Point(12, y + 2), Checked = own is not null, AccessibleName = Loc.T("ui.to.own.an", plain) };
            var number = new NumericUpDown
            {
                Minimum = range.Min, Maximum = range.Max, Location = new Point(250, y), Size = new Size(118, 23),
                Value = Math.Clamp(own ?? global, range.Min, range.Max), Enabled = own is not null, AccessibleName = plain,
            };
            // Unticked, the number shows the global value; the own value goes into the memory and comes back when the box is ticked again.
            var memory = new OwnValueMemory(own);
            box.CheckedChanged += (_, _) =>
            {
                number.Enabled = box.Checked;
                if (box.Checked) number.Value = Math.Clamp(memory.Restore(global), range.Min, range.Max);
                else
                {
                    memory.Remember((int)number.Value);
                    number.Value = Math.Clamp(global, range.Min, range.Max);
                }
            };
            Controls.Add(box);
            Controls.Add(number);
            return (box, number);
        }
    }
}
