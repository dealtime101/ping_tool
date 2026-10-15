namespace PingTool
{
    // Asked before a report is saved: what to hide from whoever will read it. Nothing ticked = the report as it always was.
    internal sealed class ReportMaskingForm : Form
    {
        private readonly CheckBox targets, ips, cards, computer;
        private readonly ReportData data;
        private readonly List<string> previews = new();

        public ReportMasking Result { get; private set; } = ReportMasking.None;

        public ReportMaskingForm(ReportData data)
        {
            this.data = data;
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
            cards = Box("Names of the network &cards, Wi-Fi networks, access points", 138);
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
            ok.Click += (_, _) => Result = Current();

            // What the reader will get, before anything is saved: the report with the boxes as they are now, in the browser.
            var preview = new Button { Text = "&Preview", Location = new Point(12, 214), Size = new Size(96, 28), AccessibleName = "Preview the report with these choices" };
            preview.Click += (_, _) => ShowPreview();
            Controls.Add(preview);

            // The previews are temporary files: they go when the box closes (the browser has the page by then).
            FormClosed += (_, _) =>
            {
                foreach (string path in previews)
                {
                    try { File.Delete(path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a browser still holding it: the temporary folder keeps it */ }
                }
            };
        }

        private ReportMasking Current() => new(targets.Checked, ips.Checked, cards.Checked, computer.Checked);

        private void ShowPreview()
        {
            string path = Path.Combine(Path.GetTempPath(), "pingtool-preview-" + Guid.NewGuid().ToString("N") + ".html");
            try
            {
                File.WriteAllText(path, ReportBuilder.Build(ReportAnonymizer.Mask(data, Current()).Data), new System.Text.UTF8Encoding(false));
                previews.Add(path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                MessageBox.Show(this, "Could not show the preview: " + ex.Message, "PingTool");
            }
        }

        private CheckBox Box(string text, int y)
        {
            string plain = text.Replace("&", "", StringComparison.Ordinal);   // what a screen reader says: without the access key mark
            var box = new CheckBox { Text = text, ForeColor = Color.White, AutoSize = true, Location = new Point(12, y), AccessibleName = plain };
            Controls.Add(box);
            return box;
        }

        // What to hide, or null when the person gives up saving.
        public static ReportMasking? Ask(IWin32Window owner, ReportData data)
        {
            using var dialog = new ReportMaskingForm(data);
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
