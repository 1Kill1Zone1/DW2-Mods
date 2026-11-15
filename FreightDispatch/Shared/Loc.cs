using System.Text.Json;

namespace DW2Mod;

// The words a mod shows in the game: the launcher's in-game status widget and the text a mod puts in the game's own screens. Only that text:
// log lines, errors and warnings are always English (they are what gets sent in for debugging).
//
// Every text has a symbolic key, PascalCase, named for what it says and where (FacilityRefusedBusy), and is a whole sentence with its changing
// parts as named placeholders (the launcher's rules; DW2-Mod-Launcher docs/Localization.md):
//   Loc.T("FacilityRefusedBusy", ("Colony", name), ("Share", 0.9))      "{Colony} is busy; {Share:P0} of ..."
// The English is a file like the others: status.en.json next to the dll maps every key to its English text. Editing the English changes that
// one file and no translation. A key the code asks for that status.en.json lacks is an Error (once), since the text does not exist.
//
// The language is the launcher's "Language" option (the same one the widget speaks: the enabled translation mod's, or the launcher's own).
// Its text comes from, in this order, the later one winning per key:
//   status.<language>.json next to the dll        { "FacilityRefusedBusy": "{Colony}は使用中です" }
//   each file in the launcher's TranslationFiles option (translation packs, in load order; DW2-Mod-Launcher docs/Localization.md)
// A key a language does not have shows its English text. No file for a language is fine (a mod need not speak every language); a file that
// exists but cannot be read is an Error naming it, and that file adds nothing.
//
// Shared source (dll/Shared): every code mod compiles its own internal copy.
internal static class Loc
{
    public static string Language { get; private set; } = "en";

    // key -> English text (status.en.json)
    static Dictionary<string, string> english = new(StringComparer.Ordinal);

    // the language's text, by key
    static Dictionary<string, string> words = new(StringComparer.Ordinal);

    // a problem with one text is reported once, not on every call
    static readonly HashSet<string> reported = new(StringComparer.Ordinal);

    // Called once at start-up with the launcher's Language option and the translation pack files it listed for this mod; "en" (or blank) means
    // the English text as written.
    public static void Use(string language, IEnumerable<string> translationFiles = null)
    {
        Language = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim();
        english = new Dictionary<string, string>(StringComparer.Ordinal);
        words = new Dictionary<string, string>(StringComparer.Ordinal);
        lock (reported)
        {
            reported.Clear();
        }

        string englishPath = Path.Combine(ModHost.ModDir, "status.en.json");
        if (File.Exists(englishPath))
        {
            Merge(english, englishPath);
        }

        List<string> packs = translationFiles?.ToList() ?? new List<string>();
        if (!Language.Equals("en", StringComparison.OrdinalIgnoreCase))
        {
            string path = Path.Combine(ModHost.ModDir, $"status.{Language}.json");
            if (File.Exists(path))
            {
                Merge(words, path);
            }
            else if (packs.Count == 0)
            {
                ModHost.Info($"no {Path.GetFileName(path)}, so the status text stays English");
            }
        }
        foreach (string pack in packs)
        {
            Merge(words, pack);
        }
    }

    // Reads a flat JSON file of key to text into the dictionary; a file that cannot be read is an Error and adds nothing.
    static void Merge(Dictionary<string, string> into, string path)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("expected a JSON object of text keys to text");
            }
            Dictionary<string, string> read = new(StringComparer.Ordinal);
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException($"\"{p.Name}\" must be a string");
                }
                read[p.Name] = p.Value.GetString();
            }
            foreach (KeyValuePair<string, string> pair in read)
            {
                into[pair.Key] = pair.Value;
            }
        }
        catch (Exception ex)
        {
            ModHost.Error($"reading {path} failed, its text is not used: {ex.Message}");
        }
    }

    // The text for a key in the current language with its named placeholders filled; the English for that key where the language has none.
    // A key status.en.json lacks is an Error, once, and the key itself is shown. A placeholder the code does not give stays in the text as
    // written and is reported, so a typo in a translation is visible.
    public static string T(string key, params (string Name, object Value)[] values)
    {
        string template;
        if (words.TryGetValue(key, out string translated) && !string.IsNullOrWhiteSpace(translated))
        {
            template = translated;
        }
        else if (english.TryGetValue(key, out string text))
        {
            template = text;
        }
        else
        {
            ReportOnce("missing " + key, $"the text key '{key}' is not in status.en.json, so there is no text to show");
            return key;
        }
        return NamedText.Fill(template, placeholder => ReportOnce(key + "{" + placeholder, $"the text '{key}' ({Language}) has the placeholder {{{placeholder}}}, which the code does not give"), values);
    }

    // The English text of a key with its placeholders filled, whatever the language, for the log (log lines stay English). A key status.en.json lacks is shown as itself.
    public static string LogText(string key, params (string Name, object Value)[] values)
        => english.TryGetValue(key, out string text) ? NamedText.Fill(text, values) : key;

    static void ReportOnce(string id, string message)
    {
        bool first;
        lock (reported)
        {
            first = reported.Add(id);
        }
        if (first)
        {
            ModHost.Error(message);
        }
    }
}
