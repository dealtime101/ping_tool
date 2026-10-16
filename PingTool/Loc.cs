using System.Globalization;

namespace PingTool
{
    // The words PingTool shows, in English (the default) and French. A key is a CODE, never the sentence itself, so that a
    // new language is one more column and a missing translation is found by a test (every key used in the source exists,
    // both columns are filled and carry the same {n} placeholders), not by a user reading the wrong language.
    // The language is the one of the Windows display language; numbers and dates keep following the regional format.
    internal static class Loc
    {
        // Set by tests; null = follow the display language of Windows. The program itself never assigns it, hence the explicit initial
        // value (without it the compiler warns, CS0649, that the field "will always be null": true of the program, not of the tests).
        internal static string? ForcedLanguage = null;

        internal static string Language =>
            (ForcedLanguage ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) == "fr" ? "fr" : "en";

        internal static readonly IReadOnlyDictionary<string, (string En, string Fr)> Table = new Dictionary<string, (string, string)>
        {
            ["timeline.empty"] = ("No data yet: start pinging this host.", "Aucune donnée pour l'instant : lancez les pings sur cet hôte."),
            // {3} and {4} are phrases made with the count.* keys, which agree with the number (1 ping, 2 pings; French: 0 or 1 perdu, 2 perdus).
            ["timeline.summary"] = ("{0} to {1} ({2}) | {3}, {4} ({5}%)", "De {0} à {1} ({2}) | {3}, {4} ({5} %)"),
            ["log.dropped"] = ("The log keeps the latest {0} pings: the {1} older ones are no longer in it.", "Le journal garde les {0} derniers pings : les {1} plus anciens n'y sont plus."),
            ["count.ping.one"] =("{0} ping", "{0} ping"),
            ["count.ping.many"] = ("{0} pings", "{0} pings"),
            ["count.lost.one"] = ("{0} lost", "{0} perdu"),
            ["count.lost.many"] = ("{0} lost", "{0} perdus"),
            ["timeline.slowest"] = ("Highest average: {0} ms around {1}", "Moyenne la plus haute : {0} ms vers {1}"),
            ["timeline.lossiest"] = ("Most losses: {0}% of the pings ({1} of {2}) around {3}", "Le plus de pertes : {0} % des pings ({1} sur {2}) vers {3}"),
            ["timeline.peak"] = ("Highest reply: {0} ms, above the scale of the chart ({1} ms): those columns are cut at the top and marked.", "Réponse la plus haute : {0} ms, au-dessus de l'échelle du graphique ({1} ms) : ces colonnes sont coupées en haut et marquées."),
            ["timeline.axis.top"] = ("{0} ms", "{0} ms"),   // the top value of the vertical axis; a language that writes the unit another way changes it here
            ["timeline.axis.clipped"] = ("{0} ms (peak {1})", "{0} ms (pic {1})"),
            ["timeline.incidents.none"] = ("No incident on this host in this period.", "Aucun incident sur cet hôte pendant cette période."),
            ["timeline.incidents"] = ("Incidents: {0}.", "Incidents : {0}."),
            // "depuis" in French says that it still goes on: a finished incident is "le {0}" (a date and a time), and one that is still going on
            // says so itself, with "timeline.ongoing" in the brackets.
            ["timeline.outage"] = ("outage from {0} ({1})", "panne le {0} ({1})"),
            ["timeline.slowdown"] = ("slowdown from {0} ({1})", "ralentissement le {0} ({1})"),
            ["timeline.ongoing"] = ("still going on, {0}", "toujours en cours, {0}"),
            ["timeline.small"] = ("The window is too small to draw the chart: make it taller.", "La fenêtre est trop petite pour dessiner le graphique : agrandissez-la."),
            ["timeline.network"] = ("Network changes of this PC and notes (cyan lines): ", "Changements du réseau de ce PC et notes (traits cyan) : "),
            ["timeline.more"] = (" (+{0} more)", " (+{0} de plus)"),
            ["timeline.periods"] = ("Over the session, period by period: {0}.", "Au fil de la session, période par période : {0}."),
            ["timeline.period"] = ("from {0}: average {1} ms, {2}% lost", "dès {0} : moyenne {1} ms, {2} % perdus"),
            ["timeline.period.one"] = ("from {0}: average {1} ms, {2}% lost", "dès {0} : moyenne {1} ms, {2} % perdu"),   // French: below 2 the word is singular
            ["timeline.title"] = ("PingTool - Session timeline", "PingTool - Chronologie de la session"),
            ["timeline.legend"] = ("Each column is a slice of the session: grey = lowest to highest reply, green = average, red at the bottom = lost pings (stronger = more), striped red background = outage, plain orange tint = slowdown, dotted cyan line = network change of this PC, red-orange triangle at the top = highest reply above the scale (the column is cut).",
                "Chaque colonne est une tranche de la session : gris = de la réponse la plus basse à la plus haute, vert = moyenne, rouge en bas = pings perdus (plus fort = plus), fond rouge rayé = panne, teinte orange unie = ralentissement, trait cyan pointillé = changement de réseau de ce PC, triangle rouge-orange en haut = réponse la plus haute au-dessus de l'échelle (la colonne est coupée)."),
            ["timeline.close"] = ("Close", "Fermer"),
            ["timeline.target"] = ("Target", "Cible"),
            ["timeline.chart"] = ("Session timeline", "Chronologie de la session"),
            ["timeline.chart.of"] = ("{0}: {1}", "{0} : {1}"),   // the name of the chart for a screen reader: what it is, then the target it shows now
            ["graph.empty"] = ("No data yet: start pinging.", "Aucune donnée pour l'instant : lancez les pings."),
            ["graph.selected"] = ("Selected target", "Cible sélectionnée"),
            ["graph.noping"] = ("{0}: no ping yet", "{0} : aucun ping pour l'instant"),
            ["graph.last.lost"] = ("last ping lost", "dernier ping perdu"),
            ["graph.last"] = ("last {0} ms", "dernier ping : {0} ms"),
            ["graph.p95"] = (", 95th percentile {0} ms", ", 95e centile {0} ms"),
            // {0} target, {1} last ping, {2} percentile, {3} lost, {4} how many pings, {5} trend, {6} the lost count with its word ("1 perdu",
            // "3 perdus": the agreement follows the number LOST, not the number of pings, see Loc.Lost); one ping and several pings are two sentences
            ["graph.line.one"] = ("{0}: {1}{2}, {3} of the last {4} ping lost{5}", "{0} : {1}{2}, {3} perdu sur {4} ping{5}"),
            ["graph.line.many"] = ("{0}: {1}{2}, {3} of the last {4} pings lost{5}", "{0} : {1}{2}, {6} sur les {4} derniers pings{5}"),
            // {1} = how many pings the graph holds: the trend is the curve as DRAWN, which is longer than the "last 60" of the figures before it,
            // and a listener who adds up the losses of the stretches must not expect the total of those.
            ["graph.trend"] = ("; over the {1} pings of the graph, oldest first: {0}", " ; sur les {1} pings du graphe, du plus ancien au plus récent : {0}"),
            ["graph.trend.part"] = ("{0}, {1}", "{0}, {1}"),   // {1} is a count.lost phrase; a comma between the two figures, so that a screen reader does not run them together
            ["graph.trend.nodata"] = ("not measured", "non mesuré"),   // a stretch of the window in which no ping was made (a silence)
            ["graph.noreply"] = ("no reply", "aucune réponse"),
            ["graph.ms"] = ("{0} ms", "{0} ms"),
            ["graph.p95"] = ("p95 {0}", "p95 {0}"),   // the label of the dotted line: the percentile is written p95 in both languages
            ["graph.legend.more"] = ("{0}-{1} of {2}: click or press Page Down for more", "{0}-{1} sur {2} : cliquez ou appuyez sur Page suivante pour voir la suite"),   // the line under a legend of more hosts than it shows
            ["graph.legend.page"] = ("{0}. The legend is on page {1} of {2}: Page Down shows the next one, Page Up the one before.", "{0}. La légende est à la page {1} sur {2} : Page suivante montre la suivante, Page précédente la précédente."),   // read by a screen reader with the figures of the graph
            // What the automatic log says to the person (balloons): our own sentences, the ones that name a folder, a file or a count.
            // The text of an error that the system gives (an exception) stays in the language of the system.
            ["log.notwritten"] = ("Log file not written, will retry: {0}", "Fichier du journal non écrit, nouvel essai plus tard : {0}"),
            ["log.drop.reason.failing"] = ("the log folder was not writable for too long", "le dossier du journal est resté trop longtemps inaccessible en écriture"),
            ["log.drop.reason.slow"] = ("writing to the log folder did not keep up and no write error was reported (a slow or stalled drive?)", "l'écriture dans le dossier du journal n'a pas suivi et aucune erreur d'écriture n'a été signalée (disque lent ou bloqué ?)"),
            ["log.drop.span"] = ("from {0} to {1}", "du {0} au {1}"),
            ["log.drop.one"] = ("{0} ping(s) {1} could not be saved to the log files: {2}. The log has a gap there.", "{0} ping(s) {1} n'ont pas pu être enregistrés dans les fichiers du journal : {2}. Le journal a un trou à cet endroit."),
            ["log.drop.many"] = ("{0} ping(s) could not be saved to the log files: {1}. The log has {2} gaps, {3}; what was written between them is in the files.", "{0} ping(s) n'ont pas pu être enregistrés dans les fichiers du journal : {1}. Le journal a {2} trous, {3} ; ce qui a été écrit entre eux est dans les fichiers."),
            ["log.drop.more"] = ("; and {0} more", " ; et {0} de plus"),
            ["log.retired.cap"] = ("{0} ping(s) from earlier runs ({1}) were given up: the log folder stayed unwritable through several runs, and what waits for it is kept to {2} pings. The log has a gap there.", "{0} ping(s) des lancements précédents ({1}) ont été abandonnés : le dossier du journal est resté inaccessible en écriture pendant plusieurs lancements, et ce qui l'attend est limité à {2} pings. Le journal a un trou à cet endroit."),
            ["log.takeback.changed"] = ("Rows of an earlier failed write could not be taken back from {0} because the file changed meanwhile: some rows may appear twice.", "Des lignes d'une écriture précédente qui a échoué n'ont pas pu être retirées de {0} car le fichier a changé entre-temps : certaines lignes peuvent apparaître deux fois."),
            ["log.takeback.wait"] = ("An earlier write to {0} left rows on disk that cannot be taken back yet ({1}); these rows wait so that they are not written twice.", "Une écriture précédente dans {0} a laissé des lignes sur le disque qui ne peuvent pas encore être retirées ({1}) ; ces lignes attendent pour ne pas être écrites deux fois."),
            ["log.other.window"] = ("Another PingTool window is writing this file; these rows wait for the next flush.", "Une autre fenêtre PingTool écrit dans ce fichier ; ces lignes attendent la prochaine écriture."),
            ["stats.waiting"] =("Last pings: waiting for data", "Derniers pings : en attente de données"),
            // {0} of the next two is the window (stats.window.*): "Last 60" / "60 derniers pings" (the French puts the number first and agrees).
            ["stats.need"] = ("{0}: need {1} replies for percentiles", "{0} : il faut {1} réponses pour les percentiles"),
            ["stats.last"] = ("{0}: p50 {1}", "{0} : p50 {1}"),
            ["stats.window.one"] = ("Last {0}", "{0} dernier ping"),
            ["stats.window.many"] = ("Last {0}", "{0} derniers pings"),
            ["stats.p95needs"] = (" (p95 needs {0} replies)", " (le p95 demande {0} réponses)"),
            ["stats.jitter"] = ("Recent jitter {0} ms | loss {1}% ({2}/{3})", "Gigue récente {0} ms | perte {1} % ({2}/{3})"),
            ["stats.voice"] = (" | voice {0} ({1})", " | voix {0} ({1})"),
            ["stats.voice.good"] = ("good", "bonne"),
            ["stats.voice.fair"] = ("fair", "correcte"),
            ["stats.voice.poor"] = ("poor", "médiocre"),
            ["stats.voice.bad"] = ("bad", "mauvaise"),
            ["alert.down"] = ("{0} is down", "{0} ne répond plus"),
            ["alert.up"] = ("{0} is back up", "{0} répond de nouveau"),
            ["alert.up.after"] = ("{0} is back up after {1}", "{0} répond de nouveau après {1}"),
            ["alert.group.down"] = ("{0} targets are down: {1}", "{0} cibles ne répondent plus : {1}"),
            ["alert.group.up"] = ("{0} targets are back up: {1}", "{0} cibles répondent de nouveau : {1}"),
            ["alert.group.up.after"] = ("{0} targets are back up: {1} (the longest outage: {2})", "{0} cibles répondent de nouveau : {1} (la plus longue coupure : {2})"),
            ["alert.group.allup"] = ("All {0} targets of the outage are back up (it lasted {1})", "Les {0} cibles de la panne répondent toutes de nouveau (elle a duré {1})"),
            ["alert.group.more"] = ("(+{0} more)", "(+{0} autres)"),
            ["alert.degraded"] = ("{0} is degraded: {1:0.#}% loss, average {2} ms over the last {3} pings", "{0} est dégradé : {1:0.#} % de perte, moyenne {2} ms sur les {3} derniers pings"),
            ["alert.degraded.noreply"] = ("{0} is degraded: {1:0.#}% loss, no reply in the last {2} pings", "{0} est dégradé : {1:0.#} % de perte, aucune réponse sur les {2} derniers pings"),
            ["alert.degraded.mos.noavg"] = ("{0} is degraded: voice quality (MOS) {1:0.0} is under {2:0.0} ({3:0.#}% loss over the last {4} pings)", "{0} est dégradé : la qualité vocale (MOS) de {1:0.0} est sous {2:0.0} ({3:0.#} % de perte sur les {4} derniers pings)"),
            ["alert.degraded.mos"] = ("{0} is degraded: voice quality (MOS) {1:0.0} is under {2:0.0} ({3:0.#}% loss, average {4} ms over the last {5} pings)", "{0} est dégradé : la qualité vocale (MOS) de {1:0.0} est sous {2:0.0} ({3:0.#} % de perte, moyenne {4} ms sur les {5} derniers pings)"),
            ["alert.recovered"] = ("{0} is back to normal", "{0} est revenu à la normale"),
        };

        // "1 ping" / "5 pings", "0 perdu" / "1 perdu" / "2 perdus": the count with the word that agrees with it (the rule of each language
        // lives in the table: English has one form for "lost", French splits at 2).
        // English: "0 pings", "1 ping". French puts 0 with the singular, like 1: "0 ping", "1 ping", "2 pings".
        internal static string Pings(long n) => T((Language == "fr" ? n >= 2 : n != 1) ? "count.ping.many" : "count.ping.one", n.ToString(CultureInfo.CurrentCulture));
        // "Last 60" / "60 derniers pings": the window of the recent figures, the count first in French and the adjective in agreement with it.
        internal static string Window(long n) => T(n >= 2 ? "stats.window.many" : "stats.window.one", n.ToString(CultureInfo.CurrentCulture));
        internal static string Lost(long n) => T(n >= 2 ? "count.lost.many" : "count.lost.one", n.ToString(CultureInfo.CurrentCulture));

        // True when the text has a place to fill, "{0}" or "{3:0.#}": a brace that opens a number. "{{" and "}}" are the braces themselves.
        internal static bool HasPlace(string text) =>
            System.Text.RegularExpressions.Regex.IsMatch(text.Replace("{{", "", StringComparison.Ordinal).Replace("}}", "", StringComparison.Ordinal), @"\{\d");

        // French puts a space before : ; ? ! % and » and after «, and a line must not break there: a "%" or a ":" alone at the start of
        // the next line reads as a mistake. The table is written with plain spaces (the source stays readable and a test can compare it),
        // and they become no-break spaces (U+00A0) here, once for all, in the one place where a French text leaves the table.
        private static readonly System.Text.RegularExpressions.Regex FrenchBreakableSpace = new(" (?=[:;?!%»])|(?<=«) ");

        internal static string FrenchSpaces(string text) => FrenchBreakableSpace.Replace(text, ((char)0xA0).ToString());

        // The text for a key in the current language, with its arguments formatted in the current regional format.
        public static string T(string key, params object[] args)
        {
            if (!Table.TryGetValue(key, out var pair)) return key;   // a missing key shows itself: ugly, and found at once
            string text = Language == "fr" && pair.Fr.Length > 0 ? FrenchSpaces(pair.Fr) : pair.En;
            if (args.Length == 0)
            {
                // A text that is a plain sentence is returned as it is. One with a place to fill ({0}) that is called with nothing to put in
                // it is a fault of the caller, and is told like the one with too few: the sentence with its key, and a line for the developer.
                if (!HasPlace(text)) return text;
                System.Diagnostics.Debug.WriteLine($"Loc.T(\"{key}\"): called with no argument, the text has a place to fill");
                return text + " [" + key + "]";
            }

            try
            {
                return string.Format(CultureInfo.CurrentCulture, text, args);
            }
            catch (FormatException ex)
            {
                // A caller that gives fewer arguments than the sentence has places (or a sentence with a broken place) must not take the
                // alert or the timeline down with it: the sentence as it is, with its key, shows the fault at once and stays readable.
                System.Diagnostics.Debug.WriteLine($"Loc.T(\"{key}\"): {ex.Message}");
                return text + " [" + key + "]";
            }
        }
    }
}
