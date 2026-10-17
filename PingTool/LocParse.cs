namespace PingTool
{
    // The reasons given when something typed or read is refused (an address, a profile name, a profiles file, the settings file) and the
    // help of the address box: the third part of the table of Loc (see Loc.cs and LocUi.cs). English first, as it was; the options and
    // examples that a person types (#family=6, tcp://example.com:443) are the same in both languages.
    internal static class LocParse
    {
        internal static readonly IReadOnlyDictionary<string, (string En, string Fr)> Entries = new Dictionary<string, (string, string)>
        {
            // ---- the address box
            ["pt.help"] = ("No prefix = ping.  tcp://host:port = open a TCP connection.  http://url or https://url = web request.  dns://name = name lookup.  "
                + "Web options after #, joined by &: #contains=text (the page must hold it; spaces as %20) and #cert=days (https only: warn that many days before the certificate ends, 0 = never), "
                + "for example https://example.com/#contains=Welcome&cert=30. Also #status=200 (the exact status), #final=host/path (where the redirects must end) and #json=path=value (a value in a JSON body). dns://name takes #server=ip,ip (resolvers to compare), #type=AAAA|MX|NS|TXT|CNAME, #same and #change.",
                "Sans préfixe = ping.  tcp://hôte:port = ouvrir une connexion TCP.  http://url ou https://url = requête web.  dns://nom = résolution de nom.  "
                + "Options web après #, jointes par & : #contains=texte (la page doit le contenir ; espaces écrits %20) et #cert=jours (https seulement : avertir ce nombre de jours avant la fin du certificat, 0 = jamais), "
                + "par exemple https://example.com/#contains=Welcome&cert=30. Aussi #status=200 (le code exact), #final=hôte/chemin (où les redirections doivent finir) et #json=chemin=valeur (une valeur dans un corps JSON). dns://nom accepte #server=ip,ip (résolveurs à comparer), #type=AAAA|MX|NS|TXT|CNAME, #same et #change."),
            ["pt.nopair"] = ("There is no #family=both in this address.", "Il n'y a pas de #family=both dans cette adresse."),
            ["pt.empty"] = ("Enter an address first, or add a host to the list.", "Saisissez d'abord une adresse, ou ajoutez un hôte à la liste."),
            ["pt.noprefix"] = ("Without a prefix the address is pinged, so it must be just a host name or an IP address. For a port use tcp://example.com:443, for a web page https://example.com/page",
                "Sans préfixe, l'adresse est pingée : ce doit être seulement un nom d'hôte ou une adresse IP. Pour un port, utilisez tcp://example.com:443 ; pour une page web, https://example.com/page"),
            ["pt.badname"] = ("That is not a valid host name or IP address. Without a prefix the address is pinged, so it must be just a name such as example.com or an address such as 8.8.8.8 (no ?, @ or # parts).",
                "Ce n'est pas un nom d'hôte ni une adresse IP valide. Sans préfixe, l'adresse est pingée : ce doit être seulement un nom comme example.com ou une adresse comme 8.8.8.8 (sans partie ?, @ ou #)."),
            ["pt.tcp.option"] = ("The only option after # of a tcp address is #family=4 or #family=6, for example tcp://example.com:443#family=6",
                "La seule option après # d'une adresse tcp est #family=4 ou #family=6, par exemple tcp://example.com:443#family=6"),
            ["pt.tcp.hostport"] = ("tcp:// needs a host and a port from 1 to 65535, for example tcp://example.com:443", "tcp:// demande un hôte et un port de 1 à 65535, par exemple tcp://example.com:443"),
            ["pt.tcp.brackets"] = (" (brackets are only for an IPv6 address, for example tcp://[2001:db8::1]:443)", " (les crochets ne servent que pour une adresse IPv6, par exemple tcp://[2001:db8::1]:443)"),
            ["pt.family.ip"] = ("#family= needs a host name, not an IP address: an address has one family already.", "#family= demande un nom d'hôte, pas une adresse IP : une adresse n'a déjà qu'une famille."),
            ["pt.family.value"] = ("#family= needs 4 or 6, for example https://example.com/#family=6", "#family= demande 4 ou 6, par exemple https://example.com/#family=6"),
            ["pt.web.invalid"] = ("{0}://: that is not a valid web address, for example https://example.com/", "{0}:// : ce n'est pas une adresse web valide, par exemple https://example.com/"),
            ["pt.web.port"] = ("{0}://: the port must be from 1 to 65535, for example https://example.com:8443/", "{0}:// : le port doit être de 1 à 65535, par exemple https://example.com:8443/"),
            ["pt.web.expect"] = ("#{0}= needs a value of 1 to {1} characters (write spaces as %20), for example https://example.com/#status=200",
                "#{0}= demande une valeur de 1 à {1} caractères (écrivez les espaces %20), par exemple https://example.com/#status=200"),
            ["pt.web.status"] = ("#status= needs a status code from 100 to 599, for example https://example.com/#status=200", "#status= demande un code d'état de 100 à 599, par exemple https://example.com/#status=200"),
            ["pt.web.json"] = ("#json= needs a dotted path, optionally followed by =value, for example https://example.com/health#json=status=ok",
                "#json= demande un chemin à points, suivi si on veut de =valeur, par exemple https://example.com/health#json=status=ok"),
            ["pt.web.contains"] = ("#contains= needs a text of 1 to {0} characters (write spaces as %20), for example https://example.com/#contains=Welcome",
                "#contains= demande un texte de 1 à {0} caractères (écrivez les espaces %20), par exemple https://example.com/#contains=Welcome"),
            ["pt.web.certhttp"] = ("#cert= only applies to https:// addresses: an http:// address has no certificate to check. Use https://, or leave #cert= out.",
                "#cert= ne vaut que pour les adresses https:// : une adresse http:// n'a pas de certificat à vérifier. Utilisez https://, ou retirez #cert=."),
            ["pt.web.certnum"] = ("#cert= needs a number of days from 0 (never warn) to {0}, for example https://example.com/#cert=30",
                "#cert= demande un nombre de jours de 0 (ne jamais avertir) à {0}, par exemple https://example.com/#cert=30"),
            ["pt.web.twice"] = ("#{0}= is given twice in the web address: use each option once, separated by & (#contains=text&cert=days).",
                "#{0}= est donné deux fois dans l'adresse web : utilisez chaque option une fois, séparées par & (#contains=texte&cert=jours)."),
            ["pt.web.unknown"] = ("Unknown option after # in the web address: use #contains=text, #cert=days, #status=code, #final=host/path, #json=path=value or #family=4|6 (separated by &), each once.",
                "Option inconnue après # dans l'adresse web : utilisez #contains=texte, #cert=jours, #status=code, #final=hôte/chemin, #json=chemin=valeur ou #family=4|6 (séparées par &), chacune une fois."),
            ["pt.dns.ip"] = ("dns:// needs a name, not an IP address: an address is not looked up, so nothing would be tested. For example dns://example.com (to test an address, write it without a prefix to ping it).",
                "dns:// demande un nom, pas une adresse IP : une adresse n'est pas résolue, donc rien ne serait testé. Par exemple dns://example.com (pour tester une adresse, écrivez-la sans préfixe pour la pinguer)."),
            ["pt.dns.name"] = ("dns:// needs just a name, for example dns://example.com", "dns:// demande seulement un nom, par exemple dns://example.com"),
            ["pt.dns.unknown"] = ("Unknown option after # in the dns address: use #server=ip,ip, #type=A|AAAA|CNAME|MX|NS|TXT, #same and #change (separated by &), each once.",
                "Option inconnue après # dans l'adresse dns : utilisez #server=ip,ip, #type=A|AAAA|CNAME|MX|NS|TXT, #same et #change (séparées par &), chacune une fois."),
            ["pt.dns.twice"] = ("#{0} is given twice in the dns address: use each option once.", "#{0} est donné deux fois dans l'adresse dns : utilisez chaque option une fois."),
            ["pt.dns.server"] = ("#server= needs 1 to {0} IP addresses separated by commas (a name would need a lookup itself), for example dns://example.com#server=1.1.1.1,8.8.8.8",
                "#server= demande de 1 à {0} adresses IP séparées par des virgules (un nom demanderait lui-même une résolution), par exemple dns://example.com#server=1.1.1.1,8.8.8.8"),
            ["pt.dns.type"] = ("#type= needs A, AAAA, CNAME, MX, NS or TXT, for example dns://example.com#type=AAAA", "#type= demande A, AAAA, CNAME, MX, NS ou TXT, par exemple dns://example.com#type=AAAA"),
            ["pt.dns.novalue"] = ("#{0} takes no value: write it alone, for example dns://example.com#server=1.1.1.1,8.8.8.8&{0}",
                "#{0} ne prend pas de valeur : écrivez-le seul, par exemple dns://example.com#server=1.1.1.1,8.8.8.8&{0}"),
            ["pt.prefix"] = ("Unknown prefix \"{0}://\". Use tcp://, http://, https:// or dns://, or no prefix to ping.", "Préfixe inconnu « {0}:// ». Utilisez tcp://, http://, https:// ou dns://, ou aucun préfixe pour pinguer."),

            // ---- the name of a profile, and the profiles left out of settings.json
            ["pr.name.invalid"] = ("The name contains characters that are not valid text.", "Le nom contient des caractères qui ne sont pas du texte valide."),
            ["pr.name.empty"] = ("Give the profile a name.", "Donnez un nom au profil."),
            ["pr.name.reserved"] = ("That name is reserved for the built-in diagnosis: choose another.", "Ce nom est réservé au diagnostic intégré : choisissez-en un autre."),
            ["pr.name.long"] = ("The name is limited to {0} characters.", "Le nom est limité à {0} caractères."),
            ["pr.name.invisible"] = ("The name cannot contain control or invisible formatting characters.", "Le nom ne peut pas contenir de caractères de contrôle ni de mise en forme invisibles."),
            ["pr.left.empty"] = ("an empty entry", "une entrée vide"),
            ["pr.left.long"] = ("\"{0}\": the name is limited to {1} characters", "« {0} » : le nom est limité à {1} caractères"),
            ["pr.left.why"] = ("\"{0}\": {1}", "« {0} » : {1}"),
            ["pr.left.dup"] = ("\"{0}\": another profile already has that name", "« {0} » : un autre profil porte déjà ce nom"),
            ["pr.left.max"] = ("\"{0}\": at most {1} profiles are kept", "« {0} » : {1} profils au plus sont gardés"),
            ["pr.trim.hosts.one"] = ("{0} host beyond the limit of {1} per profile", "{0} hôte au-delà de la limite de {1} par profil"),
            ["pr.trim.hosts.many"] = ("{0} hosts beyond the limit of {1} per profile", "{0} hôtes au-delà de la limite de {1} par profil"),
            ["pr.trim.options.one"] = ("{0} name or limit of targets that are not in the profile", "{0} nom ou limite de cibles qui ne sont pas dans le profil"),
            ["pr.trim.options.many"] = ("{0} names or limits of targets that are not in the profile", "{0} noms ou limites de cibles qui ne sont pas dans le profil"),
            ["pr.and"] = (" and ", " et "),
            ["pr.more"] = (" (and {0} more)", " (et {0} de plus)"),
            ["pr.described.trimmed.one"] = ("{0} profile in settings.json was loaded with part of its content left out: {1}{2}.", "{0} profil de settings.json a été chargé avec une partie de son contenu écartée : {1}{2}."),
            ["pr.described.trimmed.many"] = ("{0} profiles in settings.json were loaded with part of their content left out: {1}{2}.", "{0} profils de settings.json ont été chargés avec une partie de leur contenu écartée : {1}{2}."),
            ["pr.described.left.one"] = ("{0} profile in settings.json was left out: {1}{2}.", "{0} profil de settings.json a été écarté : {1}{2}."),
            ["pr.described.left.many"] = ("{0} profiles in settings.json were left out: {1}{2}.", "{0} profils de settings.json ont été écartés : {1}{2}."),

            // ---- a profiles file that is imported
            ["px.large"] = ("That file is too large to be a list of profiles.", "Ce fichier est trop gros pour être une liste de profils."),
            ["px.twice"] = ("This file has \"{0}\" more than once: it is not clear which one counts. Keep one.", "Ce fichier a « {0} » plus d'une fois : on ne sait pas lequel compte. N'en gardez qu'un."),
            ["px.notformat"] = ("This file is not a PingTool profiles file.", "Ce fichier n'est pas un fichier de profils PingTool."),
            ["px.version.bad"] = ("This file has a version number that PingTool cannot read.", "Ce fichier a un numéro de version que PingTool ne sait pas lire."),
            ["px.version.new"] = ("This file was made by a newer PingTool: update PingTool to read it.", "Ce fichier a été fait par un PingTool plus récent : mettez PingTool à jour pour le lire."),
            ["px.nolist"] = ("This file does not contain a list of profiles.", "Ce fichier ne contient pas de liste de profils."),
            ["px.badprofile"] = ("A profile in this file is not valid: a value is missing, or is not of the kind expected there (at {0}).", "Un profil de ce fichier n'est pas valide : une valeur manque, ou n'est pas du genre attendu à cet endroit (à {0})."),
            ["px.json.at"] = ("This file is not valid JSON: it breaks at line {0}, position {1}.", "Ce fichier n'est pas du JSON valide : il casse à la ligne {0}, position {1}."),
            ["px.json"] = ("This file is not valid JSON.", "Ce fichier n'est pas du JSON valide."),
            ["px.none"] = ("No usable profile in this file.", "Aucun profil utilisable dans ce fichier."),

            // ---- the settings file
            ["st.unread"] = ("The settings file could not be read ({0}). ", "Le fichier de réglages n'a pas pu être lu ({0}). "),
            ["st.unread.nokeep"] = ("It could not be kept aside either: PingTool will not replace it without first keeping a copy (settings.json.unread-...). ",
                "Il n'a pas pu non plus être mis de côté : PingTool ne le remplacera pas sans en avoir d'abord gardé une copie (settings.json.unread-...). "),
            ["st.unread.kept"] = ("It was kept as {0} so that you can fix it. ", "Il a été gardé sous le nom {0} pour que vous puissiez le réparer. "),
            ["st.unread.tail"] = ("PingTool starts with its default settings.", "PingTool démarre avec ses réglages par défaut."),
            ["st.open"] = ("The settings file could not be opened ({0}). The file was left as it is; PingTool starts with its default settings. It will not replace the file without first keeping a copy of it (settings.json.unread-...), and not at all while it cannot make that copy.",
                "Le fichier de réglages n'a pas pu être ouvert ({0}). Le fichier a été laissé tel quel ; PingTool démarre avec ses réglages par défaut. Il ne remplacera pas le fichier sans en avoir d'abord gardé une copie (settings.json.unread-...), et pas du tout tant qu'il ne peut pas faire cette copie."),
            ["st.webhooks.one"] = ("{0} webhook address in settings.json was ignored: only distinct http:// or https:// addresses are used (at most {1}).",
                "{0} adresse de webhook de settings.json a été ignorée : seules des adresses http:// ou https:// distinctes sont utilisées ({1} au plus)."),
            ["st.webhooks.many"] = ("{0} webhook addresses in settings.json were ignored: only distinct http:// or https:// addresses are used (at most {1}).",
                "{0} adresses de webhook de settings.json ont été ignorées : seules des adresses http:// ou https:// distinctes sont utilisées ({1} au plus)."),
            ["st.windows.one"] = ("{0} maintenance window in settings.json was ignored (write them like \"Mon-Fri 02:00-03:30\" or \"22:00-02:00\", at most {1}): {2}.",
                "{0} fenêtre de maintenance de settings.json a été ignorée (écrivez-les comme « Mon-Fri 02:00-03:30 » ou « 22:00-02:00 », {1} au plus) : {2}."),
            ["st.windows.many"] = ("{0} maintenance windows in settings.json were ignored (write them like \"Mon-Fri 02:00-03:30\" or \"22:00-02:00\", at most {1}): {2}.",
                "{0} fenêtres de maintenance de settings.json ont été ignorées (écrivez-les comme « Mon-Fri 02:00-03:30 » ou « 22:00-02:00 », {1} au plus) : {2}."),

            // ---- "Diagnose my connection"
            ["dg.nogateway"] = ("No network gateway was found (is the PC connected?). The router is not in the list: without it, the report cannot say whether the fault is on your side. The Internet references, and the DNS servers of the active network card when it has some, are tested all the same.",
                "Aucune passerelle réseau n'a été trouvée (le PC est-il connecté ?). Le routeur n'est pas dans la liste : sans lui, le rapport ne peut pas dire si la panne est de votre côté. Les références Internet, et les serveurs DNS de la carte réseau active quand elle en a, sont testés quand même."),
            ["dg.cards.one"] = ("{0} network card could not be read ({1}): the gateway may be on it.", "{0} carte réseau n'a pas pu être lue ({1}) : la passerelle est peut-être dessus."),
            ["dg.cards.many"] = ("{0} network cards could not be read ({1}): the gateway may be on one of them.", "{0} cartes réseau n'ont pas pu être lues ({1}) : la passerelle est peut-être sur l'une d'elles."),
        };
    }
}
