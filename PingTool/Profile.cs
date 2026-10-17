using System.Globalization;
using System.Text;

namespace PingTool
{
    // A named, reusable way of monitoring: the targets and every setting that shapes the probing.
    // Switching from a quick check to a long watch is picking another name.
    internal sealed class Profile
    {
        public string Name { get; set; } = "";
        public List<string> Hosts { get; set; } = new();
        public int IntervalMs { get; set; } = 1000;
        public int TimeoutMs { get; set; } = 1000;
        public int PacketSize { get; set; } = 32;
        public bool Alert { get; set; } = true;
        public int DegradedLatencyMs { get; set; } = 150;
        public int DegradedLossPercent { get; set; } = 30;
        public double DegradedMos { get; set; }   // 0 = not used (see Settings)
        public int DownAfter { get; set; } = HostMonitor.DefaultDownAfter;

        // The name and own limits of some of the hosts above, by address (see TargetOptions).
        public Dictionary<string, TargetOptions> TargetOptions { get; set; } = new(TargetKey.Comparer);
    }

    // The rules for the list of profiles kept in settings.json. Names are compared without
    // regard to case ("Home" and "home" are one profile), the way Windows treats file names.
    internal static class ProfileBook
    {
        public const int MaxProfiles = 20;

        // The settings a profile shapes the probing with, for a run that does not use the window (the file is not touched).
        public static void ApplyTo(Settings settings, Profile profile)
        {
            settings.IntervalMs = Limits.Clamp(profile.IntervalMs, Limits.IntervalMs);
            settings.TimeoutMs = profile.TimeoutMs;
            settings.PacketSize = profile.PacketSize;
            settings.DegradedLatencyMs = profile.DegradedLatencyMs;
            settings.DegradedLossPercent = profile.DegradedLossPercent;
            settings.DegradedMos = Limits.ClampMos(profile.DegradedMos);
            settings.DownAfter = profile.DownAfter;
            settings.TargetOptions = new Dictionary<string, TargetOptions>(profile.TargetOptions, TargetKey.Comparer);
        }
        public const int MaxNameLength = 40;

        // The most UTF-16 units of a name, looked at BEFORE the name is cut into characters as they are shown: a flag followed by a
        // million tag characters is ONE shown character, and would pass the count below while costing the work of the whole text.
        // 512 is more than 40 of the longest emoji sequences in use.
        public const int MaxNameUnits = 512;

        // The name as it is compared and kept: trimmed, and in the composed form of Unicode (NFC). "Café" typed with the accented letter and
        // "Café" with an e and a combining accent (what a paste from a Mac file name or some keyboards gives) are drawn the same and are the
        // same name to the person: kept apart, they would be two profiles that nobody can tell apart on screen. null when the text is not
        // valid Unicode (a lone half of a surrogate pair), which no form can be made of.
        internal static string? Canonical(string? text)
        {
            string trimmed = (text ?? "").Trim();
            try { return trimmed.Normalize(NormalizationForm.FormC); }
            catch (ArgumentException) { return null; }
        }

        // The same for a comparison, where a text that cannot be normalized is compared as it is.
        private static string KeyOf(string? text) => Canonical(text) ?? (text ?? "").Trim();

        public static bool TryName(string? input, out string name, out string error)
        {
            name = Canonical(input) ?? (input ?? "").Trim();
            error = "";

            if (Canonical(input) is null) error = Loc.T("pr.name.invalid");
            else if (name.Length == 0) error = Loc.T("pr.name.empty");
            else if (string.Equals(name, DiagnosticTargets.ProfileName, StringComparison.OrdinalIgnoreCase))
                error = Loc.T("pr.name.reserved");
            else if (name.Length > MaxNameUnits) error = Loc.T("pr.name.long", MaxNameLength);
            // The length is what the user sees, in characters as they are shown (a family emoji, three people joined by U+200D, is one), not in
            // UTF-16 units (that one is eight). Written in words, not with the emoji itself: the joiners in it are invisible in an editor.
            else if (new StringInfo(name).LengthInTextElements > MaxNameLength) error = Loc.T("pr.name.long", MaxNameLength);
            else if (HasInvisible(name)) error = Loc.T("pr.name.invisible");

            return error.Length == 0;
        }

        // Control characters and the invisible "format" ones (zero width space, bidi marks): "Home" and "Home" + U+200B look
        // the same on screen but would be two profiles. One exception: the zero width joiner INSIDE a character that is drawn as one
        // (an emoji family, a flag with a pride stripe): without it the emoji falls apart. Between two letters it joins nothing and
        // stays refused, so "Ho" + ZWJ + "me" cannot pass for "Home".
        // Read by Unicode characters (runes), not by UTF-16 units: a format character outside the basic plane (U+E0001, the "tag" characters)
        // is two units, and neither half is a format character by itself.
        private static bool HasInvisible(string name)
        {
            var starts = StringInfo.ParseCombiningCharacters(name);   // once for the name: the borders of its text elements
            int i = 0;
            foreach (var rune in name.EnumerateRunes())
            {
                if (Rune.IsControl(rune) || IsInvisible(name, starts, i, rune)) return true;
                i += rune.Utf16SequenceLength;
            }

            return false;
        }

        // A character that draws nothing and that the user cannot tell from its absence: the "format" ones (unless IsAllowedFormat keeps
        // them) and the other characters Unicode says to ignore when drawing - the combining grapheme joiner (U+034F), the Hangul and
        // Khmer fillers, the variation selectors, the unassigned places among them. "Home" and "Home" + U+034F look the same and would be
        // two profiles. A variation selector is kept after the character it belongs to (see IsKeptSelector).
        private static bool IsInvisible(string text, int[] starts, int index, Rune rune)
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format) return !IsAllowedFormat(text, starts, index, rune);
            return IsIgnorable(rune) && !IsKeptSelector(text, index, rune);
        }

        private static bool IsIgnorable(Rune rune)
        {
            int v = rune.Value;
            if (v is 0x034F or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or 0x3164 or 0xFFA0) return true;
            if (v is >= 0x180B and <= 0x180F or >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF) return true;   // variation selectors
            return Rune.GetUnicodeCategory(rune) == UnicodeCategory.OtherNotAssigned && (v is >= 0xFFF0 and <= 0xFFF8 or >= 0xE0000 and <= 0xE0FFF);
        }

        // A variation selector that picks a drawing of the character before it is part of the text: the emoji or text presentation
        // (U+FE0E/U+FE0F) after a symbol or a keycap digit ("❤️", "1️⃣"), the ideographic ones after a CJK ideograph, the Mongolian ones
        // after a Mongolian letter. After a letter of an alphabet, after nothing, or after another selector it changes nothing on screen.
        private static bool IsKeptSelector(string text, int index, Rune rune)
        {
            if (index == 0) return false;
            var before = char.IsLowSurrogate(text[index - 1]) && index > 1 ? Rune.GetRuneAt(text, index - 2) : Rune.GetRuneAt(text, index - 1);
            int v = rune.Value, b = before.Value;
            if (v is 0xFE0E or 0xFE0F)
            {
                var category = Rune.GetUnicodeCategory(before);
                if (category is UnicodeCategory.OtherSymbol or UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol
                    or UnicodeCategory.OtherPunctuation or UnicodeCategory.DashPunctuation) return true;
                int next = index + rune.Utf16SequenceLength;
                return (b is >= '0' and <= '9' or '#' or '*') && next < text.Length && text[next] == (char)0x20E3;   // the keycap (combining enclosing keycap)
            }

            if (v is >= 0xE0100 and <= 0xE01EF) return b is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x2FA1F;
            if (v is >= 0x180B and <= 0x180F) return b is >= 0x1820 and <= 0x18AA;
            return false;
        }

        // The format characters that hold one drawn character together and are kept: the zero width joiner (see IsJoiner) and the tag
        // characters of an emoji flag sequence (the flag of England, U+1F3F4 followed by tags), when they follow something in the same text
        // element. A tag character on its own, or after a letter that is not a flag, is as invisible as a zero width space.
        private static bool IsAllowedFormat(string text, int[] starts, int index, Rune rune) =>
            rune.Value == 0x200D ? IsJoiner(text, starts, index)
            : rune.Value is >= 0xE0020 and <= 0xE007F && Holds(text, starts, index, rune.Utf16SequenceLength, mayEnd: true) && ElementStartsWithBlackFlag(text, starts, index);

        // The text element that holds the unit at `index`: the last border at or before it (the borders are in order, so a bisection:
        // a name made of many format characters must not cost a pass over the whole text for each of them).
        private static int ElementOf(int[] starts, int index)
        {
            int at = Array.BinarySearch(starts, index);
            return at >= 0 ? at : ~at - 1;
        }

        // Tags are invisible after a letter too ("Home" + a tag is a second, look-alike name): they are kept only in a flag sequence,
        // whose element begins with U+1F3F4.
        private static bool ElementStartsWithBlackFlag(string text, int[] starts, int index)
        {
            int start = starts[ElementOf(starts, index)];
            return char.IsHighSurrogate(text[start]) && start + 1 < text.Length && char.ConvertToUtf32(text[start], text[start + 1]) == 0x1F3F4;
        }

        // True for a zero width joiner that is inside one text element (neither the first nor the last char of it): the rules of the
        // Unicode text segmentation keep an emoji sequence together across it, and cut after it when it joins nothing.
        internal static bool IsJoiner(string text, int[] starts, int index) => text[index] == (char)0x200D && Holds(text, starts, index, 1, mayEnd: false);

        // The character at `index` (`length` units) is inside a text element, after its first char (and before its last one unless mayEnd).
        private static bool Holds(string text, int[] starts, int index, int length, bool mayEnd)
        {
            int element = ElementOf(starts, index);
            int start = starts[element];
            int end = element + 1 < starts.Length ? starts[element + 1] : text.Length;
            return index > start && (mayEnd || index + length < end);
        }

        // The name as it was written before the format characters were refused: they are taken out, except those that hold an emoji together.
        internal static string WithoutInvisibleFormat(string text)
        {
            var kept = new StringBuilder(text.Length);
            var starts = StringInfo.ParseCombiningCharacters(text);   // once for the text, however many format characters it holds
            int i = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                if (!IsInvisible(text, starts, i, rune)) kept.Append(rune.ToString());
                i += rune.Utf16SequenceLength;
            }

            return kept.ToString();
        }

        // True when the list on screen is what the saved profile holds (same targets, any order, same rule as "the same
        // target" everywhere). No saved profile means the list has not been saved: it is only "the same" when it is empty.
        public static bool SameTargets(IEnumerable<string> current, Profile? saved) =>
            new HashSet<string>(current, TargetKey.Comparer).SetEquals(saved?.Hosts ?? new List<string>());

        // The names and limits the window uses for the hosts of a profile that is being loaded: ONLY what that profile defines. The table is
        // by address and shared by every profile, so the options an earlier profile left for the same host must go first, or a profile that
        // says nothing about a host would keep running it with the name and the limits of another one (instead of the global limits).
        public static void ApplyTargetOptions(Dictionary<string, TargetOptions> table, Profile profile)
        {
            foreach (var host in profile.Hosts) table.Remove(host);
            foreach (var (address, options) in profile.TargetOptions) table[address] = options;
        }

        public static Profile? Find(IEnumerable<Profile> book, string? name)
        {
            string key = KeyOf(name);
            return book.FirstOrDefault(p => string.Equals(KeyOf(p.Name), key, StringComparison.OrdinalIgnoreCase));
        }

        // Replaces the profile of the same name where it is, or appends. False when the book is full
        // (and the name is new): the caller says so, nothing is lost silently.
        public static bool Upsert(List<Profile> book, Profile profile)
        {
            profile.Name = KeyOf(profile.Name);   // Find and Remove compare in the same form: the same profile must not be added twice
            int at = book.FindIndex(p => string.Equals(KeyOf(p.Name), profile.Name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                book[at] = profile;
                return true;
            }

            if (book.Count >= MaxProfiles) return false;
            book.Add(profile);
            return true;
        }

        public static bool Remove(List<Profile> book, string? name)
        {
            string key = KeyOf(name);
            return book.RemoveAll(p => string.Equals(KeyOf(p.Name), key, StringComparison.OrdinalIgnoreCase)) > 0;
        }

        // What a hand-edited or damaged settings.json may contain: nulls, blank or repeated names,
        // a missing host list, more profiles than allowed. Keeps what is usable, first one wins.
        // leftOut: when given, receives one line per profile that was not kept and why, so that the caller can say it (nothing is lost
        // silently: a profile of a hand-edited file that vanishes with all its hosts, with no word, is worse than a refusal).
        // trimmed: when given, receives one line per profile that was KEPT but with part of its content dropped (hosts beyond the limit, the
        // names and limits of targets it does not hold), for DescribeTrimmed.
        public static List<Profile> Sanitize(IEnumerable<Profile?>? loaded, List<string>? leftOut = null, List<string>? trimmed = null)
        {
            var clean = new List<Profile>();
            foreach (var p in loaded ?? Enumerable.Empty<Profile?>())
            {
                if (p is null) { leftOut?.Add(Loc.T("pr.left.empty")); continue; }
                // A name of a hand-edited file may be of any size: it is refused before any of it is cut into characters (see MaxNameUnits).
                if ((p.Name?.Length ?? 0) > MaxNameUnits)
                {
                    leftOut?.Add(Loc.T("pr.left.long", Clip(p.Name!), MaxNameLength));
                    continue;
                }

                // A profile saved before the format characters were refused keeps its place, under the name as it looked.
                p.Name = WithoutInvisibleFormat(p.Name ?? "");
                if (!TryName(p.Name, out string name, out string why))
                {
                    leftOut?.Add(Loc.T("pr.left.why", Clip(p.Name), why.TrimEnd('.')));
                    continue;
                }

                if (Find(clean, name) is not null)
                {
                    leftOut?.Add(Loc.T("pr.left.dup", Clip(name)));
                    continue;
                }

                if (clean.Count >= MaxProfiles)
                {
                    leftOut?.Add(Loc.T("pr.left.max", Clip(name), MaxProfiles));
                    continue;
                }

                p.Name = name;
                p.IntervalMs = Limits.Clamp(p.IntervalMs, Limits.IntervalMs);
                p.TimeoutMs = Limits.Clamp(p.TimeoutMs, Limits.TimeoutMs);
                p.PacketSize = Limits.Clamp(p.PacketSize, Limits.PacketSize);
                p.DegradedLatencyMs = Limits.Clamp(p.DegradedLatencyMs, Limits.DegradedLatencyMs);
                p.DegradedLossPercent = Limits.Clamp(p.DegradedLossPercent, Limits.DegradedLossPercent);
                p.DegradedMos = Limits.ClampMos(p.DegradedMos);
                p.DownAfter = Limits.Clamp(p.DownAfter, Limits.DownAfter);
                // Same rules as an imported profile: one entry per address (case ignored), at most MaxHostsPerProfile.
                // A blank entry is not a target and a repeat of one is the same target: neither is a loss. Hosts beyond the limit are.
                var distinctHosts = (p.Hosts ?? new List<string>()).Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim())
                    .Distinct(TargetKey.Comparer).ToList();
                p.Hosts = distinctHosts.Take(ProfileExchange.MaxHostsPerProfile).ToList();
                // The names and limits are those "of some of the hosts above": an entry for an address the profile does not hold (left out,
                // empty, or cut by the limit on hosts) would be applied to the table of the whole window when the profile is loaded.
                var held = new HashSet<string>(p.Hosts, TargetKey.Comparer);
                var cleaned = PingTool.TargetOptions.Clean(p.TargetOptions!);
                p.TargetOptions = cleaned.Where(entry => held.Contains(entry.Key)).ToDictionary(entry => entry.Key, entry => entry.Value, TargetKey.Comparer);

                // What the profile keeps is less than the file held: said, with the profile's name, not only done.
                int cutHosts = distinctHosts.Count - p.Hosts.Count, cutOptions = cleaned.Count - p.TargetOptions.Count;
                if (trimmed is not null && (cutHosts > 0 || cutOptions > 0))
                {
                    var parts = new List<string>();
                    if (cutHosts > 0) parts.Add(Loc.Count("pr.trim.hosts", cutHosts, ProfileExchange.MaxHostsPerProfile));
                    if (cutOptions > 0) parts.Add(Loc.Count("pr.trim.options", cutOptions));
                    trimmed.Add(Loc.T("pr.left.why", Clip(name), string.Join(Loc.T("pr.and"), parts)));
                }

                clean.Add(p);
            }

            return clean;
        }

        // The sentence for the profiles that were loaded but not whole (see Sanitize): how many, then the first reasons.
        public static string DescribeTrimmed(IReadOnlyList<string> trimmed) =>
            Loc.Count("pr.described.trimmed", trimmed.Count, string.Join("; ", trimmed.Take(3)), trimmed.Count > 3 ? Loc.T("pr.more", trimmed.Count - 3) : "");

        private static string Clip(string text) => text.Length <= 20 ? text : TextCut.Units(text, 20) + "...";

        // The sentence for the start-up message: how many, then the first reasons (three at most, the rest counted).
        public static string DescribeLeftOut(IReadOnlyList<string> leftOut) =>
            Loc.Count("pr.described.left", leftOut.Count, string.Join("; ", leftOut.Take(3)), leftOut.Count > 3 ? Loc.T("pr.more", leftOut.Count - 3) : "");
    }
}
