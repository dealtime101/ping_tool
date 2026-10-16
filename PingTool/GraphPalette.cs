using System.Drawing;

namespace PingTool
{
    // The colours of the latency graph. Normally the dark palette of the window; when Windows is in a HIGH CONTRAST theme the
    // user has chosen the colours they can read, so the graph takes the system's (window, text, highlight, hot-track, grey text)
    // and tells the targets apart by line style too, since colour is all a high-contrast theme leaves.
    internal sealed class GraphPalette
    {
        private readonly bool highContrast;

        private GraphPalette(bool highContrast) => this.highContrast = highContrast;

        public static GraphPalette For(bool highContrast) => new(highContrast);

        public Color Back => highContrast ? SystemColors.Window : Color.FromArgb(40, 40, 40);
        public Color Text => highContrast ? SystemColors.WindowText : Color.Silver;

        // The colour of the n-th line: its own in the dark palette; one of four system colours in high contrast.
        // Highlight is the background of a selection and GrayText the colour of a disabled text: a theme is not bound to make either of them
        // readable on its window (a custom one may not), so a colour that does not contrast enough with the window gives way to the text colour,
        // and the pattern of the line (Dash) keeps the lines apart.
        public Color Series(int index, Color own) => !highContrast ? own : (index % HighContrastColours) switch
        {
            0 => SystemColors.WindowText,
            1 => Readable(SystemColors.HotTrack, SystemColors.WindowText, SystemColors.Window),
            2 => Readable(SystemColors.Highlight, SystemColors.WindowText, SystemColors.Window),
            _ => Readable(SystemColors.GrayText, SystemColors.WindowText, SystemColors.Window),
        };

        // The least contrast a line needs against its background (WCAG asks 3:1 for graphical objects).
        public const double MinContrast = 3.0;

        internal static Color Readable(Color candidate, Color text, Color back) => Contrast(candidate, back) >= MinContrast ? candidate : text;

        // The contrast ratio of two colours, 1 (alike) to 21 (black on white), from their relative luminance as WCAG defines it: the one
        // formula of the program (UiColors.Contrast), which reads a colour with an alpha channel as it looks over the background.
        internal static double Contrast(Color a, Color b) => UiColors.Contrast(a, b);

        // A lost ping: red when one host is shown, its host's colour when several are compared (so you can tell whose it is).
        public Color Loss(bool compare, Color seriesColor) => compare ? seriesColor : highContrast ? Readable(SystemColors.HotTrack, SystemColors.WindowText, SystemColors.Window) : Color.Red;

        // The line pattern of the n-th line, as an index into Patterns (0 solid, 1 dash, 2 dot, 3 dash-dot). The colours come round every
        // `colours` lines; the pattern changes each time they do, so that no two lines look alike before colours x patterns lines
        // (24 in the dark palette, 16 in high contrast). The first round is solid in the dark palette.
        // In high contrast the four colours are the ones of the user's theme, and some themes give two of them alike or nearly: the first
        // four lines must not rest on the colour alone, so the pattern moves on with every line AND with every round (i + i / 4), which
        // still gives 16 different pairs of colour and pattern.
        public int Dash(int index)
        {
            int i = Math.Max(0, index);
            return highContrast ? (i + i / HighContrastColours) % Patterns : (i / Colours) % Patterns;
        }
        public const int Patterns = 4;
        public const int HighContrastColours = 4;
        private int Colours => highContrast ? HighContrastColours : HostPalette.Count;
    }
}
