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
        public Color Text => highContrast ? SystemColors.WindowText : Color.Silver;
        // GrayText is a disabled text and Highlight a selection background: a theme need not make them stand out from its window, so each
        // colour taken from the theme gives way to the text colour when its contrast with the window is under 3:1 (see GraphPalette.Readable).
        private static Color Fit(Color themeColour) => GraphPalette.Readable(themeColour, SystemColors.WindowText, SystemColors.Window);

        public Color Axis => highContrast ? Fit(SystemColors.GrayText) : Color.FromArgb(120, 192, 192, 192);
        public Color Bars => highContrast ? Fit(SystemColors.GrayText) : Color.FromArgb(90, 192, 192, 192);
        public Color Average => highContrast ? SystemColors.WindowText : Color.LimeGreen;
        public Color Network => highContrast ? Fit(SystemColors.Highlight) : Color.Cyan;
        public Color CutMark => highContrast ? Fit(SystemColors.HotTrack) : Color.OrangeRed;

        // The share of lost pings in a column: the stronger the red, the larger the share; in high contrast one opaque colour, the width of
        // the bar and its position at the bottom say the rest.
        public Color Loss(double fraction) => highContrast ? Fit(SystemColors.HotTrack) : Color.FromArgb((int)(70 + 185 * Math.Clamp(fraction, 0, 1)), 255, 99, 71);

        // The two colours of the hatching of an outage.
        public (Color Fore, Color Back) OutageHatch => highContrast ? (Fit(SystemColors.HotTrack), SystemColors.Window) : (Color.FromArgb(170, 255, 99, 71), Color.FromArgb(50, 255, 99, 71));

        // A slowdown: a plain tint normally; in high contrast a tint is not legible, a dotted pattern (not the outage's stripes) is.
        public bool SlowdownIsHatched => highContrast;
        public Color SlowdownColor => highContrast ? Fit(SystemColors.Highlight) : Color.FromArgb(60, 255, 165, 0);
    }
}
