namespace AIOrchestrator.API.Legal;

/// <summary>
/// Vocabulary that carries no discriminating power in Italian legal text. A term like <em>reato</em>,
/// <em>pena</em> or <em>legge</em> occurs in tens of thousands of documents, so using it as a query
/// term (or as evidence that a document is relevant) produces confident nonsense: a question about
/// "pirateria spaziale su Marte" would match every article that merely speaks of a <em>reato</em>.
/// The lexicon is applied both when building queries and when gating relevance.
/// </summary>
internal static class LegalLexicon
{
    /// <summary>Stem prefixes treated as non-discriminating (stems are truncated, so prefixes are used).</summary>
    private static readonly string[] GenericPrefixes =
    [
        "reat", "pen", "legg", "codic", "articol", "disposizion", "norm", "fatt", "caso", "casi",
        "speci", "preved", "previst", "previs", "applic", "giudiz", "giudic", "tribun", "sentenz",
        "decis", "question", "problem", "materi", "gener", "divers", "possibil", "consider", "valut",
        "verific", "second", "relativ", "stess", "quest", "definit", "comport", "esemp", "ipotes",
        "contrari", "ulter", "quindi", "ancor", "comunqu", "tutt", "sempr", "alcun", "esser", "aver",
        "venir", "dover", "voler", "andar", "senz", "anche", "quand", "qual", "cioe", "dove", "oltre",
        "entro", "presso", "durant", "attravers", "riguard", "concern", "rifer", "tratt", "consist",
        "propri", "altru", "veng", "sull", "nell", "dell", "all", "dagl", "dall", "quell", "ogn",
        "colu", "colui", "costui", "medesim", "nonch", "ovver", "inolt", "pertan", "tuttavi"
    ];

    /// <summary>True when the token carries no discriminating power.</summary>
    /// <param name="token">Folded or stemmed token.</param>
    /// <returns><c>true</c> when the token is generic.</returns>
    public static bool IsGeneric(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length <= 2) return true;
        foreach (var prefix in GenericPrefixes)
            if (token.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Keeps only the terms that can actually discriminate one case from another, preserving order
    /// and uniqueness. When the filter would leave nothing (a topic made only of boilerplate) the
    /// original list is returned so the caller still has something to search with.
    /// </summary>
    /// <param name="tokens">Candidate terms.</param>
    /// <returns>Discriminating terms.</returns>
    public static List<string> Distinctive(IEnumerable<string> tokens)
    {
        var all = tokens.Where(t => !string.IsNullOrEmpty(t)).Distinct(StringComparer.Ordinal).ToList();
        var kept = all.Where(t => !IsGeneric(t)).ToList();
        return kept.Count >= 2 ? kept : all;
    }
}