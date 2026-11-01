using System.Globalization;
using System.Text;

namespace AIOrchestrator.API.Legal;

/// <summary>
/// Italian text normalisation shared by every layer (normaliser, index, retrieval, verifier).
/// Archive text is formal ("impossessamento", "cosa mobile altrui") while a user query is
/// colloquial ("mi hanno rubato l'auto"), so recall depends on a deterministic fold + a light
/// Italian stemmer applied identically on both sides.
/// </summary>
public static class ItalianText
{
    private static readonly Dictionary<char, char> Fold = new()
    {
        ['à'] = 'a', ['á'] = 'a', ['â'] = 'a', ['ä'] = 'a', ['ã'] = 'a', ['å'] = 'a',
        ['è'] = 'e', ['é'] = 'e', ['ê'] = 'e', ['ë'] = 'e',
        ['ì'] = 'i', ['í'] = 'i', ['î'] = 'i', ['ï'] = 'i',
        ['ò'] = 'o', ['ó'] = 'o', ['ô'] = 'o', ['ö'] = 'o', ['õ'] = 'o',
        ['ù'] = 'u', ['ú'] = 'u', ['û'] = 'u', ['ü'] = 'u',
        ['ç'] = 'c', ['ñ'] = 'n',
        ['’'] = '\'', ['‘'] = '\'', ['“'] = '"', ['”'] = '"', ['«'] = '"', ['»'] = '"',
        ['\u00A0'] = ' ', ['\u2009'] = ' ', ['\u202F'] = ' '
    };

    /// <summary>Italian function words removed before indexing and querying.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a","ad","al","alla","alle","allo","agli","ai","all","anche","avrei","aveva","avevo","avuto",
        "c","che","chi","ci","coi","col","come","con","contro","cui","da","dal","dalla","dalle","dallo","dagli","dai",
        "dei","del","della","delle","dello","degli","di","dov","dove","e","è","ed","era","erano","essere","fa","fatto",
        "fra","gli","ha","hai","hanno","ho","i","il","in","io","la","le","lei","lo","loro","lui","ma","mi","mia","mie",
        "miei","mio","ne","nei","nel","nella","nelle","nello","negli","nell","noi","non","o","per","perché","più","può",
        "qual","quale","quali","quando","quanto","quella","quelle","quelli","quello","questa","queste","questi","questo",
        "sarà","se","sei","si","sia","siamo","solo","sono","su","sua","sue","sui","sul","sulla","sulle","sullo","suo",
        "suoi","te","ti","tra","tu","tua","tue","tuo","tuoi","un","una","uno","vi","voi","vostra","vostro","y","dell",
        "nell","sull","dall","all","l","un","d","poi","già","ancora","sempre","mai","molto","tanto","essere","avere",
        "detto","suddetto","presente","articolo","art","comma","legge","norma"
    };

    /// <summary>Suffixes stripped by the light stemmer, longest first. Deliberately conservative:
    /// over-stemming merges distinct legal concepts (furto/furtivo is fine, pena/penale is not).</summary>
    private static readonly string[] Suffixes =
    {
        "amente","azioni","azione","mente","zioni","zione","mente","menti","mento","amenti","amento",
        "azioni","amento","evole","ibile","abile","istico","istica","istici","istiche",
        "trice","trici","mente","ando","endo","arre","orre","urre","are","ere","ire",
        "ato","ata","ati","ate","ito","ita","iti","ite","uto","uta","uti","ute",
        "ivo","iva","ivi","ive","oso","osa","osi","ose","ale","ali","ile","ili",
        "are","ere","ire","ano","ono","ando","endo","ato","ito","uto",
        "i","e","o","a"
    };

    /// <summary>Lower-cases, folds accents/quotes and collapses whitespace.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        var lastSpace = true;
        foreach (var raw in text)
        {
            var c = char.ToLowerInvariant(raw);
            if (Fold.TryGetValue(c, out var folded)) c = folded;
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (!lastSpace) { sb.Append(' '); lastSpace = true; }
                continue;
            }
            sb.Append(c);
            lastSpace = false;
        }
        return sb.ToString().Trim();
    }

    /// <summary>Light Italian stemmer: folds the word, strips one derivational/inflexional suffix.</summary>
    public static string Stem(string word)
    {
        if (word.Length <= 4) return word;
        foreach (var suffix in Suffixes)
        {
            if (word.Length - suffix.Length < 4) continue;
            if (!word.EndsWith(suffix, StringComparison.Ordinal)) continue;
            return word[..^suffix.Length];
        }
        return word;
    }

    /// <summary>Tokenises into folded, stemmed, de-stop-worded terms (order preserved).</summary>
    public static List<string> Tokenize(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var normalized = Normalize(text);
        var sb = new StringBuilder(32);
        foreach (var c in normalized)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '\'')
            {
                sb.Append(c);
                continue;
            }
            Flush(sb, result);
        }
        Flush(sb, result);
        return result;
    }

    private static void Flush(StringBuilder sb, List<string> sink)
    {
        if (sb.Length == 0) return;
        var token = sb.ToString().Trim('-', '\'');
        sb.Clear();
        if (token.Length < 2) return;
        if (StopWords.Contains(token)) return;
        if (token.All(char.IsDigit)) { sink.Add(token); return; }
        sink.Add(Stem(token));
    }

    /// <summary>Search text for indexing: folded, stop-words kept out, stems joined by spaces.</summary>
    public static string ToSearchText(string? text) => string.Join(' ', Tokenize(text));

    /// <summary>Distinct significant stems of a text, useful for overlap scoring.</summary>
    public static HashSet<string> TermSet(string? text) => new(Tokenize(text), StringComparer.Ordinal);

    /// <summary>Fraction of <paramref name="needles"/> stems present in <paramref name="haystack"/> (0..1).</summary>
    public static double Coverage(IEnumerable<string> needles, ICollection<string> haystack)
    {
        var total = 0;
        var hit = 0;
        foreach (var needle in needles)
        {
            total++;
            if (haystack.Contains(needle)) hit++;
        }
        return total == 0 ? 0 : (double)hit / total;
    }

    /// <summary>Splits a text into sentences on Italian punctuation, keeping short fragments out.</summary>
    public static List<string> Sentences(string? text, int minLength = 25)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            sb.Append(c);
            if (c is '.' or '!' or '?' or ';' or '\n')
            {
                var sentence = sb.ToString().Trim();
                sb.Clear();
                if (sentence.Length >= minLength) result.Add(sentence);
                else if (sentence.Length > 0) result.Add(sentence);
            }
        }
        var tail = sb.ToString().Trim();
        if (tail.Length > 0) result.Add(tail);
        return result;
    }

    /// <summary>Truncates on a word boundary, appending an explicit omission marker.</summary>
    public static string Clip(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Length <= maxChars) return text;
        var cut = text.LastIndexOf(' ', Math.Min(maxChars, text.Length - 1));
        if (cut < maxChars / 2) cut = maxChars;
        return text[..cut].TrimEnd() + $" [... testo abbreviato: {text.Length - cut} caratteri omessi]";
    }

    /// <summary>Formats a number with the Italian decimal separator (deterministic, culture-free).</summary>
    /// <summary>Trims and collapses runs of whitespace, leaving words untouched.</summary>
    /// <param name="text">Input text.</param>
    /// <returns>Single-line, whitespace-collapsed text.</returns>
    public static string CollapseWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space) builder.Append(' ');
            space = false;
            builder.Append(character);
        }

        return builder.ToString();
    }
    /// <summary>Formats a number with the invariant culture, at most two decimals.</summary>
    /// <param name="value">Value to format.</param>
    /// <returns>The formatted number.</returns>
    public static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}