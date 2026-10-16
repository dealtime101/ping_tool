using System.Drawing;

namespace PingTool
{
    // The colours of text on the dark window (see Form1.Designer: BackColor 64, 64, 64), with the contrast they must keep.
    // WCAG: 4.5:1 for normal text, 3:1 for large text (the big result is 48 points bold).
    internal static class UiColors
    {
        public static readonly Color WindowBack = Color.FromArgb(64, 64, 64);

        // Tomato is fine for the big result (3.5:1, large text) but too dim for a sentence: this one reaches 4.5:1.
        public static readonly Color SmallTextRed = Color.FromArgb(255, 140, 120);

        // The colour as it LOOKS: a colour with an alpha channel is drawn over what is behind it, and the eye reads the result, not the colour
        // that was asked for (white at 20 % over black is a dark grey, not a white). Opaque colours come back as they are.
        public static Color Over(Color top, Color back)
        {
            if (top.A == 255) return top;
            double alpha = top.A / 255.0;
            static byte Mix(byte t, byte b, double a) => (byte)Math.Clamp((int)Math.Round(t * a + b * (1 - a)), 0, 255);
            return Color.FromArgb(255, Mix(top.R, back.R, alpha), Mix(top.G, back.G, alpha), Mix(top.B, back.B, alpha));
        }

        // The contrast ratio of a text (or a line) colour with its background, 1 (alike) to 21 (black on white), from the relative luminance
        // of WCAG - of the colours as they LOOK: the background over behindBack (the window, by default) when it has an alpha channel, then
        // the text over that background. Opaque colours give the figure they always gave.
        public static double Contrast(Color text, Color back, Color? behindBack = null)
        {
            var seenBack = Over(back, behindBack ?? WindowBack);
            var seenText = Over(text, seenBack);
            double la = Luminance(seenText), lb = Luminance(seenBack);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        private static double Luminance(Color c)
        {
            static double Channel(int v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
    }
}
