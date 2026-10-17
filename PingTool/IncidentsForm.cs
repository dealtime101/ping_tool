using System.Globalization;

namespace PingTool
{
    // The incident timeline, newest first. A snapshot: it does not refresh while open.
    internal sealed class IncidentsForm : Form
    {
        // The colours: the dark palette of the timeline window, or the system's in a high-contrast theme, where the user has chosen the
        // colours they can read (looked at again when the theme changes, see OnSystemColorsChanged).
        private TimelinePalette palette = TimelinePalette.For(SystemInformation.HighContrast);
        private Action? applyColors;

        protected override void OnSystemColorsChanged(EventArgs e)
        {
            base.OnSystemColorsChanged(e);
            palette = TimelinePalette.For(SystemInformation.HighContrast);
            applyColors?.Invoke();
        }

        public IncidentsForm(IReadOnlyList<Incident> incidents, string summary, DateTimeOffset now)
        {
            // Positions and sizes are written for 96 DPI: the form scales them to the screen, so that the text, which does grow
            // with the scaling, keeps its room at 125 %, 150 % or 200 % (as TimelineForm and MainForm do).
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = Loc.T("ui.in.title");
            ClientSize = new Size(900, 480);
            MinimumSize = SizeFromClientSize(new Size(640, 360));
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = palette.FormBack;

            var culture = CultureInfo.CurrentCulture;
            string When(DateTimeOffset t) => t.ToLocalTime().ToString("G", culture);

            var list = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,   // the route below is that of ONE incident: with Ctrl or Shift several rows stayed marked and it showed the first of them
                HideSelection = false,   // the chosen incident stays marked while the route below has the focus (it is what the route belongs to)
                GridLines = true,
                Dock = DockStyle.Fill,
                AccessibleName = Loc.T("ui.in.an"),
                AccessibleDescription = Loc.T("ui.in.desc"),
            };
            // Column widths are not scaled by the form: written for 96 DPI too, and scaled here.
            int W(int at100) => DpiScale.Scale(new Size(at100, 0), DeviceDpi).Width;
            list.Columns.Add(Loc.T("ui.in.col.host"), W(120));
            list.Columns.Add(Loc.T("ui.in.col.type"), W(70));
            list.Columns.Add(Loc.T("ui.in.col.start"), W(130));
            list.Columns.Add(Loc.T("ui.in.col.end"), W(130));
            list.Columns.Add(Loc.T("ui.in.col.duration"), W(80));
            list.Columns.Add(Loc.T("ui.in.col.failed"), W(58));
            var causeColumn = list.Columns.Add(Loc.T("ui.in.col.cause"), W(100));
            list.Columns.Add("#", W(36));

            // "Cause / detail" is the most informative column: it takes everything the others leave, now and whenever the list
            // is resized, instead of a width written for one window (a fixed 100 px cut every cause after a few letters).
            bool fitting = false;
            void FitCause()
            {
                if (fitting) return;   // setting the width below raises ColumnWidthChanged again
                fitting = true;
                try
                {
                    int others = list.Columns.Cast<ColumnHeader>().Where(c => c != causeColumn).Sum(c => c.Width);
                    causeColumn.Width = ColumnFit.Fill(list.ClientSize.Width, SystemInformation.VerticalScrollBarWidth, others, W(120));
                }
                finally { fitting = false; }
            }

            list.HandleCreated += (_, _) => FitCause();
            list.SizeChanged += (_, _) => FitCause();
            // Another column dragged wider or narrower: what it takes or frees is the cause column's to take back (its own change is the one above).
            list.ColumnWidthChanged += (_, e) => { if (e.ColumnIndex != causeColumn.Index) FitCause(); };
            FitCause();

            // A click on a header sorts by that column (again: the other way round), on the values and not on the text shown.
            // Until then the order is the newest first. The arrow in the title says which column and which way.
            string[] titles = list.Columns.Cast<ColumnHeader>().Select(c => c.Text).ToArray();
            int sortColumn = -1;
            bool ascending = true;
            void SortBy(int column)
            {
                (sortColumn, ascending) = IncidentOrder.Click(sortColumn, ascending, column);
                list.ListViewItemSorter = new ItemSorter(sortColumn, ascending, now);
                list.Sort();
                // The selected incident (its route is shown below) keeps its place in the list's view: a sort can send its row out of sight, and
                // a person at the keyboard or with a screen reader would no longer see which incident the detail is about.
                if (list.SelectedItems.Count > 0)
                {
                    var selected = list.SelectedItems[0];
                    selected.EnsureVisible();
                    list.FocusedItem = selected;
                }
                for (int c = 0; c < titles.Length; c++) list.Columns[c].Text = titles[c] + (c == sortColumn ? (ascending ? " ▲" : " ▼") : "");

                // The arrow is part of the title: a narrow column ("Failed", "#") that held the title with no room to spare would cut it with
                // "..." as soon as it is sorted, and the direction would be unreadable. The column grows to its title with the arrow (never
                // shrinks, the user may have widened it); the cause column takes back what the others use (ColumnWidthChanged).
                if (sortColumn >= 0)
                {
                    var sorted = list.Columns[sortColumn];
                    int needed = TextRenderer.MeasureText(titles[sortColumn] + " ▼", list.Font).Width + W(16);   // the title, the arrow, the header's own padding
                    if (sorted.Width < needed) sorted.Width = needed;
                }
            }

            list.ColumnClick += (_, e) => SortBy(e.Column);

            // The same without a mouse: Ctrl+1 to Ctrl+8 sort by the first to the eighth column (a header cannot be focused).
            list.KeyDown += (_, e) =>
            {
                if (!e.Control || e.Alt || e.Shift) return;
                int digit = e.KeyCode is >= Keys.D1 and <= Keys.D9 ? e.KeyCode - Keys.D1 + 1
                    : e.KeyCode is >= Keys.NumPad1 and <= Keys.NumPad9 ? e.KeyCode - Keys.NumPad1 + 1 : 0;
                if (IncidentOrder.ColumnOfDigit(digit, titles.Length) is not int column) return;
                SortBy(column);
                e.Handled = e.SuppressKeyPress = true;
            };

            // All the rows are made first and handed over at once (inside BeginUpdate/EndUpdate): one repaint, not one per incident.
            var rows = new List<ListViewItem>(incidents.Count);
            foreach (var i in incidents.Reverse())
            {
                bool outage = i.Kind == IncidentKind.Outage;
                string detail = IncidentLog.CauseText(i, culture);

                rows.Add(new ListViewItem(new[]
                {
                    i.Host,
                    Loc.T(outage ? "ui.in.outage" : "ui.in.slowdown"),
                    When(i.Start),
                    i.End is null ? Loc.T("ui.in.ongoing") : When(i.End.Value),
                    IncidentLog.FormatDuration(i.Duration(now)),
                    outage ? i.FailedPings.ToString(culture) : "-",
                    detail,
                    i.Occurrence.ToString(culture),
                }) { Tag = i });
            }

            list.BeginUpdate();
            try { list.Items.AddRange(rows.ToArray()); }
            finally { list.EndUpdate(); }

            // The route to the host at the moment of the selected outage.
            // A control does not dispose a font it was given: this one goes with the text box, not when the finalizer gets to it.
            var mono = new Font(FontFamily.GenericMonospace, 9F);
            var details = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = mono,
                Dock = DockStyle.Fill,
                AccessibleName = Loc.T("ui.in.route.an"),
                AccessibleDescription = Loc.T("ui.in.route.desc"),
            };
            details.Disposed += (_, _) => mono.Dispose();
            void ShowDetails() => details.Text = list.Items.Count == 0 ? IncidentLog.NoIncidentDetail
                : list.SelectedItems.Count == 0 ? "" : DetailsOf((Incident)list.SelectedItems[0].Tag!);
            list.SelectedIndexChanged += (_, _) => ShowDetails();
            if (list.Items.Count > 0) list.Items[0].Selected = true;
            // Set here as well: a selection made before the list has a window handle may not raise the event, and the route of the
            // first incident (the only one, often) would stay blank until another row was chosen; with no incident at all the panel says so.
            ShowDetails();

            var lblSummary = new Label
            {
                Text = summary,
                UseMnemonic = false,   // the sentence may name a host: a "&" in its address is a character
                // As wide as the window and two lines high: the sentence wraps instead of running past the edge (a typical one is
                // about 100 characters, as wide as the window at its minimum size; the longest the format makes is 119).
                AutoSize = false,
                Location = new Point(10, 6),
                Size = new Size(880, 32),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };

            var lblNote = new Label
            {
                Text = Loc.T("ui.in.footer", HostMonitor.WindowSize),   // the dating rule of the report (IncidentLog.DatingNote), with the legend of the # column
                AutoSize = false,
                Location = new Point(10, 414),
                Size = new Size(780, 34),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };

            var close = new Button { Text = Loc.T("ui.close"), DialogResult = DialogResult.Cancel, Location = new Point(800, 418), Size = new Size(90, 28), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            CancelButton = close;

            // The list and the route text share the height: more incidents or a longer route, drag the bar (or enlarge the window).
            var split = new SplitContainer
            {
                Orientation = Orientation.Horizontal,
                Location = new Point(10, 40),
                Size = new Size(880, 366),
                SplitterDistance = 190,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Panel1MinSize = 80,
                Panel2MinSize = 60,
            };
            split.Panel1.Controls.Add(list);
            split.Panel2.Controls.Add(details);

            Controls.Add(lblSummary);
            Controls.Add(split);
            Controls.Add(lblNote);
            Controls.Add(close);

            applyColors = () =>
            {
                BackColor = split.BackColor = palette.FormBack;
                details.BackColor = palette.Back;
                details.ForeColor = palette.BoxText;
                lblSummary.ForeColor = palette.SummaryText;
                lblNote.ForeColor = palette.NoteText;
            };
            applyColors();
        }

        private sealed class ItemSorter(int column, bool ascending, DateTimeOffset now) : System.Collections.IComparer
        {
            public int Compare(object? x, object? y)
            {
                int c = IncidentOrder.Compare((Incident)((ListViewItem)x!).Tag!, (Incident)((ListViewItem)y!).Tag!, column, now);
                return ascending ? c : -c;
            }
        }

        private static string DetailsOf(Incident i)
        {
            // The cause whole, first: the column of the list can be too narrow for it.
            string head = IncidentLog.DetailLine(i, CultureInfo.CurrentCulture) + "\r\n\r\n";

            if (i.Kind == IncidentKind.Slowdown)
                return head + Loc.T("ui.in.noroute.slowdown");

            return head + (i.Path is null
                ? Loc.T("ui.in.noroute.outage")
                // A multi-line TextBox only breaks lines on CR LF: the route text uses a bare LF.
                : i.PathText().Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal));
        }
    }
}
