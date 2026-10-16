using System.Globalization;

namespace PingTool
{
    // Cutting a text the user typed (a name, a note, a line of a file) without cutting a CHARACTER: a string is UTF-16 units, an emoji
    // outside the basic plane is two of them (a surrogate pair) and a letter with an accent can be two or more (a base and its marks), so
    // text[..n] can leave a lone half that shows as a lozenge or a "?", and that a JSON or UTF-8 writer may refuse or replace.
    internal static class TextCut
    {
        // At most maxUnits UTF-16 units, cut BETWEEN characters as they are shown (the borders of the text elements).
        internal static string Units(string text, int maxUnits)
        {
            if (text.Length <= maxUnits) return text;
            var starts = StringInfo.ParseCombiningCharacters(text);
            int cut = 0;
            foreach (int s in starts) if (s <= maxUnits) cut = s;   // the last character boundary that does not go over
            if (cut == 0)
            {
                // A single character longer than the limit (a long sequence of marks): no boundary to use, but never inside a pair.
                cut = maxUnits;
                if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
            }

            return text[..cut];
        }

        // The text as it is when `fits` says so, else its longest beginning that fits with an ellipsis, a character at a time (never half
        // of one). "…" alone when not even a character and the ellipsis fit. A beginning shorter than two units is not offered.
        internal static string Ellipsize(string text, Func<string, bool> fits)
        {
            if (fits(text)) return text;
            var starts = StringInfo.ParseCombiningCharacters(text);
            for (int i = starts.Length - 1; i >= 1 && starts[i] > 1; i--)
            {
                string cut = text[..starts[i]] + "…";
                if (fits(cut)) return cut;
            }

            return "…";
        }
    }
}
