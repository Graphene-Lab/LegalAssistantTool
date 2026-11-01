using System.Text;
using System.Text.RegularExpressions;

namespace AIOrchestrator.API.Legal;

/// <summary>Outcome of checking a drafted text against the sources actually retrieved.</summary>
/// <param name="Text">The text with every unsupported citation removed.</param>
/// <param name="RemovedSentences">Sentences dropped because they cited something not in the archive.</param>
/// <param name="RemovedReferences">The offending references themselves.</param>
internal sealed record CitationAudit(string Text, List<string> RemovedSentences, List<string> RemovedReferences);

/// <summary>
/// Hard guard against invented law. A model draft may only cite articles and judgments that the
/// deterministic retrieval actually returned from the archive: any sentence that mentions a
/// different article number or a different judgment is deleted before the opinion is rendered.
/// </summary>
internal static partial class LegalCitationGuard
{
    /// <summary>Matches "art. 624", "art.624", "articolo 624" and "artt. 624 e 625".</summary>
    [GeneratedRegex(@"\b(?:art(?:icolo|t)?\.?)\s*(\d{1,4})(?:\s*(?:e|,|-|/)\s*(\d{1,4}))?", RegexOptions.IgnoreCase)]
    private static partial Regex ArticlePattern();

    /// <summary>Matches "n. 12345/2023" and "n. 12345/23".</summary>
    [GeneratedRegex(@"\bn\.\s*(\d{1,6})\s*/\s*(\d{2,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex JudgmentNumberPattern();

    /// <summary>Matches a full Cassazione or Consulta citation.</summary>
    [GeneratedRegex(@"(?:Cass\.|Cassazione|Corte\s+cost\.|Corte\s+costituzionale)", RegexOptions.IgnoreCase)]
    private static partial Regex CourtPattern();

    /// <summary>Removes every sentence whose citations are not backed by the retrieved sources.</summary>
    /// <param name="draft">Text drafted by the model.</param>
    /// <param name="research">Sources that were actually retrieved.</param>
    /// <returns>The audit result.</returns>
    public static CitationAudit Audit(string draft, LegalResearch research)
    {
        var removedSentences = new List<string>();
        var removedReferences = new List<string>();
        if (string.IsNullOrWhiteSpace(draft)) return new CitationAudit(string.Empty, removedSentences, removedReferences);

        var allowedArticles = research.Articles.Select(a => a.Document.ArticleNumber)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowedJudgments = research.Judgments
            .Select(j => JudgmentKey(j.Document.Citation))
            .Where(k => k != null)
            .Select(k => k!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var kept = new List<string>();
        foreach (var paragraph in SplitParagraphs(draft))
        {
            var sentences = ItalianText.Sentences(paragraph);
            var keptSentences = new List<string>();
            foreach (var sentence in sentences)
            {
                var problems = new List<string>();

                foreach (Match match in ArticlePattern().Matches(sentence))
                {
                    for (var group = 1; group <= 2; group++)
                    {
                        var value = match.Groups[group].Value;
                        if (string.IsNullOrEmpty(value)) continue;
                        if (allowedArticles.Contains(value)) continue;
                        problems.Add($"art. {value}");
                    }
                }

                var courtMentioned = CourtPattern().IsMatch(sentence);
                if (courtMentioned)
                {
                    var keys = JudgmentNumberPattern().Matches(sentence)
                        .Select(m => NormalizeKey(m.Groups[1].Value, m.Groups[2].Value))
                        .Where(k => k != null)
                        .Select(k => k!)
                        .ToList();
                    if (keys.Count == 0 && allowedJudgments.Count > 0)
                    {
                        // A court is named without an identifiable number: not verifiable.
                        problems.Add("provvedimento senza numero");
                    }

                    foreach (var key in keys.Where(k => !allowedJudgments.Contains(k)))
                        problems.Add($"sentenza n. {key}");
                }

                if (problems.Count == 0)
                {
                    keptSentences.Add(sentence);
                    continue;
                }

                removedSentences.Add(sentence.Trim());
                removedReferences.AddRange(problems.Distinct(StringComparer.OrdinalIgnoreCase));
                Log.LogStep($"Citation guard removed a sentence citing {string.Join(", ", problems.Distinct())}.");
            }

            if (keptSentences.Count > 0) kept.Add(string.Join(' ', keptSentences));
        }

        return new CitationAudit(string.Join("\n\n", kept), removedSentences,
            removedReferences.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Extracts the "number/year" key of a citation, or null when there is none.</summary>
    /// <param name="citation">Citation text such as "Cass., Sez. V, n. 12345/2023".</param>
    /// <returns>The normalized key, or null.</returns>
    public static string? JudgmentKey(string? citation)
    {
        if (string.IsNullOrWhiteSpace(citation)) return null;
        var match = JudgmentNumberPattern().Match(citation);
        return match.Success ? NormalizeKey(match.Groups[1].Value, match.Groups[2].Value) : null;
    }

    private static string? NormalizeKey(string number, string year)
    {
        if (!int.TryParse(number, out var n) || !int.TryParse(year, out var y)) return null;
        if (y < 100) y += y < 50 ? 2000 : 1900;
        return $"{n}/{y}";
    }

    private static List<string> SplitParagraphs(string text)
    {
        var paragraphs = new List<string>();
        var builder = new StringBuilder();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (builder.Length > 0)
                {
                    paragraphs.Add(builder.ToString());
                    builder.Clear();
                }

                continue;
            }

            if (builder.Length > 0) builder.Append(' ');
            builder.Append(line.Trim());
        }

        if (builder.Length > 0) paragraphs.Add(builder.ToString());
        return paragraphs;
    }
}