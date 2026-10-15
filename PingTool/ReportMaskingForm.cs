namespace PingTool
{
    // Asked before a report is saved: what to hide from whoever will read it. Nothing ticked = the report as it always was.
    internal sealed class ReportMaskingForm : Form
    {
        private readonly CheckBox targets, ips, cards, computer;

        public ReportMasking Result { get; private set; } = ReportMasking.None;

        public ReportMaskingForm()
        {
            // Positions and sizes are written for 96 DPI: the form scales them to the screen (as TargetOptionsForm does).
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = "PingTool - Save the report";
            ClientSize = new Size(420, 252);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(64, 64, 64);

            Controls.Add(new Label
            {
                Text = "Hide from the reader of this report (a provider, a forum, a colleague). The same pseudonym stands for the same target or address "
                    + "everywhere, so incidents and routes stay linked. Nothing ticked = the full report.",
                ForeColor = Color.White, AutoSize = false, Location = new Point(12, 10), Size = new Size(396, 62), AccessibleName = "What hiding does",
            });

            targets = Box("Target &names and addresses (target-1, target-2...)", 82);
            ips = Box("&IP addresses, in the routes and the network changes too", 110);
            cards = Box("Names of the network &cards of this PC", 138);
            computer = Box("The &computer name", 166);

            Controls.Add(new Label
            {
                Text = "After saving, the pseudonyms and what they stand for are shown to you only: they are not in the file.",
                ForeColor = Color.Silver, AutoSize = false, Location = new Point(12, 194), Size = new Size(396, 18), AccessibleName = "Where the key is",
            });

            var ok = new Button { Text = "Save...", DialogResult = DialogResult.OK, Location = new Point(230, 214), Size = new Size(84, 28) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(324, 214), Size = new Size(84, 28) };
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(ok);
            Controls.Add(cancel);
            ok.Click += (_, _) => Result = new ReportMasking(targets.Checked, ips.Checked, cards.Checked, computer.Checked);
        }

        private CheckBox Box(string text, int y)
        {
            string plain = text.Replace("&", "", StringComparison.Ordinal);   // what a screen reader says: without the access key mark
            var box = new CheckBox { Text = text, ForeColor = Color.White, AutoSize = true, Location = new Point(12, y), AccessibleName = plain };
            Controls.Add(box);
            return box;
        }

        // What to hide, or null when the person gives up saving.
        public static ReportMasking? Ask(IWin32Window owner)
        {
            using var dialog = new ReportMaskingForm();
            return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Result : null;
        }

        // The pseudonyms and what they stand for, for the sender: one line each.
        public static void ShowKey(IWin32Window owner, IReadOnlyList<string> key)
        {
            if (key.Count == 0) return;
            MessageBox.Show(owner, "What the pseudonyms of the report stand for (keep this: it is not in the file):\n\n" + string.Join("\n", key), "PingTool");
        }
    }
}
