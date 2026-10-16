using System.Drawing;

namespace PingTool
{
    // The colours of the session timeline. Normally the dark palette of the window; when Windows is in a HIGH CONTRAST theme the user has
    // chosen the colours they can read, so the chart takes the system's (like GraphPalette for the latency graph). The codings that do not
    // depend on colour stay in both: an outage is hatched, a slowdown is another pattern or a tint, the cut mark is a triangle, the
    // network change a dotted line.
    internal sealed class TimelinePalette
    {
        private readonly bool highContrast;

        private TimelinePalette(bool highContrast) => this.highContrast = highContrast;

        public static TimelinePalette For(bool highContrast) => new(highContrast);

        public bool HighContrast => highContrast;

        // The window around the chart and the texts on it (the summary, the legend): the system's own in a high-contrast theme, where the
        // user chose the colours that are readable for them, the dark grey and the white and silver of the other windows otherwise.
        public Color FormBack => highContrast ? SystemColors.Control : Color.FromArgb(64, 64, 64);
        public Color SummaryText => highContrast ? SystemColors.ControlText : Color.White;
        public Color NoteText => highContrast ? SystemColors.ControlText : Color.Silver;

        public Color Back => highContrast ? SystemColors.Window : Color.FromArgb(40, 40, 40);
        // The text of a box laid on Back (the route text of the incidents window, which shares the dark palette of this one): the colour of
        // the system's text on its window in a high-contrast theme.
        public Color BoxText => highContrast ? SystemColors.WindowText : Color.White;
        public Color Text => highContrast ? SystemColors.WindowText : Color.Silver;
        // GrayText is a disabled text and Highlight a selection background: a theme need not make them stand out from its window, so each
        // colour taken from the theme gives way to the text colour when its contrast with the window is under 3:1 (see GraphPalette.Readable).
        private static Color Fit(Color themeColour) => GraphPalette.Readable(themeColour, SystemColors.WindowText, SystemColors.Window);

        // The silver of the axis and of the bars of latency, translucent over the dark plot: the alpha is the least that keeps 3:1 against it
        // (a graphic that carries information). The bars were at 90, which is 2.3:1; the axis at 120 was 2.98:1.
        public const int AxisAlpha = 130, BarsAlpha = 140;
        public Color Axis => highContrast ? Fit(SystemColors.GrayText) : Color.FromArgb(AxisAlpha, 192, 192, 192);
        public Color Bars => highContrast ? Fit(SystemColors.GrayText) : Color.FromArgb(BarsAlpha, 192, 192, 192);
        public Color Average => highContrast ? SystemColors.WindowText : Color.LimeGreen;
        public Color Network => highContrast ? Fit(SystemColors.Highlight) : Color.Cyan;
        public Color CutMark => highContrast ? Fit(SystemColors.HotTrack) : Color.OrangeRed;

        // The share of lost pings in a column: the stronger the red, the larger the share; in high contrast one opaque colour, the width of
        // the bar and its position at the bottom say the rest.
        // The weakest red is still a graphic that carries information (a column with a single lost ping, often the one to find), which asks
        // 3:1 against the plot: at an alpha of 70 over the dark plot it was 1.5:1, and a lone loss was lost in it. 180 gives 3.2:1.
        public const int MinLossAlpha = 180;
        public Color Loss(double fraction) => highContrast ? Fit(SystemColors.HotTrack)
            : Color.FromArgb((int)(MinLossAlpha + (255 - MinLossAlpha) * Math.Clamp(fraction, 0, 1)), 255, 99, 71);

        // The two colours of the hatching of an outage.
        public (Color Fore, Color Back) OutageHatch => highContrast ? (Fit(SystemColors.HotTrack), SystemColors.Window) : (Color.FromArgb(170, 255, 99, 71), Color.FromArgb(50, 255, 99, 71));

        // A slowdown: a plain tint normally; in high contrast a tint is not legible, a pattern is - neither the outage's stripes nor anything
        // like the dotted line of a change of network, which has the same colour there (a fine dot pattern was taken for it): the window
        // draws large squares (TimelineForm), a block against a line, so that the two events cannot be mistaken for each other without
        // the colour.
        public bool SlowdownIsHatched => highContrast;
        public Color SlowdownColor => highContrast ? Fit(SystemColors.Highlight) : Color.FromArgb(60, 255, 165, 0);
    }
}
