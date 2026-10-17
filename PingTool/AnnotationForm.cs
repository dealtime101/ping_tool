namespace PingTool
{
    // "Mark an event": a short note about something done by hand (a router restarted, a cable changed, a download started), with its time and
    // either every target or the one selected. It joins the events of the session (see NetworkEvent.Note): a line on the timeline and a row
    // of the report.
    internal sealed class AnnotationForm : Form
    {
        private const int MaxLength = NetworkEvent.MaxNoteLength;

        private readonly TextBox text;
        private readonly DateTimePicker when;
        private readonly ComboBox scope;
        private readonly string? targetName;
        private readonly string? targetAddress;

        public NetworkEvent? Result { get; private set; }

        // `target`: the name of the selected host (the scope can then be limited to it), or null. `address`: its address, which is what the
        // timeline of that host recognizes the note by.
        public AnnotationForm(string? target, string? address = null)
        {
            targetAddress = address;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            targetName = target;
            Text = Loc.T("ui.mk.title");
            ClientSize = new Size(380, 248);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(64, 64, 64);

            Controls.Add(new Label { Text = Loc.T("ui.mk.what"), ForeColor = Color.White, AutoSize = true, Location = new Point(12, 12) });
            text = new TextBox { Location = new Point(12, 32), Size = new Size(356, 23), MaxLength = MaxLength, AccessibleName = Loc.T("ui.mk.what.an") };
            Controls.Add(text);

            Controls.Add(new Label { Text = Loc.T("ui.mk.time"), ForeColor = Color.White, AutoSize = true, Location = new Point(12, 70) });
            when = new DateTimePicker
            {
                Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm", Location = new Point(12, 90), Size = new Size(170, 23),
                MaxDate = DateTime.Now.AddMinutes(1), Value = DateTime.Now, AccessibleName = Loc.T("ui.mk.time.an"),
            };
            Controls.Add(when);

            Controls.Add(new Label { Text = Loc.T("ui.mk.applies"), ForeColor = Color.White, AutoSize = true, Location = new Point(200, 70) });
            scope = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(200, 90), Size = new Size(168, 23), AccessibleName = Loc.T("ui.mk.applies.an") };
            scope.Items.Add(Loc.T("ui.mk.all"));
            if (target is not null) scope.Items.Add(target);
            scope.SelectedIndex = target is null ? 0 : 1;
            Controls.Add(scope);

            Controls.Add(new Label
            {
                Text = Loc.T("ui.mk.help"),
                ForeColor = Color.Silver, AutoSize = false, Location = new Point(12, 128), Size = new Size(356, 80), AccessibleName = Loc.T("ui.mk.help.an"),
            });

            var ok = new Button { Text = Loc.T("ui.ok"), Location = new Point(190, 212), Size = new Size(84, 28) };
            var cancel = new Button { Text = Loc.T("ui.cancel"), DialogResult = DialogResult.Cancel, Location = new Point(284, 212), Size = new Size(84, 28) };
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(ok);
            Controls.Add(cancel);
            ok.Click += (_, _) =>
            {
                Result = NetworkEvent.FromNote(text.Text, new DateTimeOffset(when.Value), scope.SelectedIndex == 1 ? targetName : null, scope.SelectedIndex == 1 ? targetAddress : null);
                if (Result is null) { text.Focus(); return; }
                DialogResult = DialogResult.OK;
            };
        }
    }
}
