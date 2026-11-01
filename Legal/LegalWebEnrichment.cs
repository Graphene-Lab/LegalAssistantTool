using System.Net;
using System.Text;

namespace AIOrchestrator.API.Legal;

/// <summary>One web page considered as extra context for a case.</summary>
/// <param name="Url">Page address.</param>
/// <param name="Title">Page title as returned by the search engine.</param>
/// <param name="Excerpt">Short extract kept as context.</param>
/// <param name="Usefulness">How useful the page is for this case (0-10).</param>
/// <param name="Reliability">Reliability of the source.</param>
/// <param name="Obsolete">True when the page looks superseded by later law.</param>
/// <param name="Assessment">Short justification of the rating.</param>
/// <param name="Used">True when the page entered the opinion context.</param>
internal sealed record WebSource(string Url, string Title, string Excerpt, int Usefulness,
    SourceReliability Reliability, bool Obsolete, string Assessment, bool Used);

/// <summary>
/// Optional internet enrichment. When the machine is offline, the search engine is blocked or the
/// provider refuses, every failure is swallowed and the opinion is produced from the archive
/// alone. Each page that does come back is rated for usefulness, source reliability and
/// obsolescence, and only pages that pass all three checks are used.
/// </summary>
internal static class LegalWebEnrichment
{
    private const int MaxQueries = 2;
    private const int MaxCandidates = 8;
    private const int MaxPagesToRead = 4;
    private const int MaxRatings = 2;
    private const double MinCoverage = 0.12;
    private const int MinUsefulness = 6;
    private const int MaxPageChars = 18000;
    private const int ExcerptChars = 1200;

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(12) };

    /// <summary>Domains whose content can be treated as authoritative without further checking.</summary>
    private static readonly string[] AuthoritativeHosts =
    [
        "normattiva.it", "gazzettaufficiale.it", "cortecostituzionale.it", "cortedicassazione.it",
        "cassazione.it", "giustizia.it", "giurisprudenzapenale.com", "cortedicassazione.it",
        "unionedellecamerapenali.it", "questionegiustizia.it", "dirittopenalecontemporaneo.it"
    ];

    /// <summary>Domains that are informative but not authoritative.</summary>
    private static readonly string[] ReputableHosts =
    [
        "altalex.com", "ilsole24ore.com", "diritto.it", "studiolegalepetralli.it", "laleggepertutti.it",
        "brocardi.it", "camera.it", "senato.it", "consiglionazionaleforense.it", "milanotoday.it",
        "filodiritto.com", "iusinitinere.it", "sistemapenale.it", "penaledp.it", "giurcost.org"
    ];

    /// <summary>Set to false to skip the internet entirely (used by tests and benchmarks).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// Searches the web for material that helps the case and rates every page it manages to read.
    /// </summary>
    /// <param name="caseType">Branch of law.</param>
    /// <param name="facts">Description of the facts.</param>
    /// <param name="topics">Topics detected from the facts.</param>
    /// <param name="articles">Articles already selected for the case.</param>
    /// <returns>The pages kept as context, best first; empty when the internet is unavailable.</returns>
    public static List<WebSource> Enrich(CaseType caseType, string facts,
        IReadOnlyList<LegalTopic> topics, IReadOnlyList<LegalDocument> articles)
    {
        var sources = new List<WebSource>();
        if (!Enabled) return sources;

        try
        {
            var queries = BuildQueries(caseType, facts, topics, articles);
            var candidates = new List<(string Url, string Title, string Preview)>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var query in queries)
            {
                WebSearchEngine.SearchResult[] results;
                try
                {
                    results = WebSearchEngine.Search(query, filetype: null,
                        provider: WebSearchEngine.SearchProvider.DuckDuckGo,
                        timeRestriction: WebSearchEngine.SearchTimeRestriction.Year);
                }
                catch (Exception ex)
                {
                    Log.LogStep($"Web search unavailable for '{query}': {ex.GetType().Name}. Enrichment skipped.");
                    return sources;
                }

                Log.LogStep($"Web search '{query}': {results?.Length ?? 0} results.");
                if (results == null) continue;
                foreach (var result in results)
                {
                    if (string.IsNullOrWhiteSpace(result.Url)) continue;
                    if (!seenUrls.Add(result.Url)) continue;
                    var title = FirstLine(result.MdDescription);
                    candidates.Add((result.Url, title, result.MdDescription ?? string.Empty));
                    if (candidates.Count >= MaxCandidates) break;
                }

                if (candidates.Count >= MaxCandidates) break;
            }

            if (candidates.Count == 0)
            {
                Log.LogStep("Web enrichment produced no candidates.");
                return sources;
            }

            // Deterministic pre-filter: a page that shares almost no vocabulary with the facts is
            // discarded before spending a model call on it. The model is slow, and most search
            // results are generic commentary that cannot help this specific case.
            var anchors = LegalLexicon.Distinctive(ItalianText.Tokenize(facts)).Take(16).ToList();
            var read = 0;
            var rated = 0;
            foreach (var candidate in candidates)
            {
                if (read >= MaxPagesToRead) break;
                var page = ReadPage(candidate.Url);
                if (page == null) continue;
                read++;
                var coverage = anchors.Count == 0 ? 1.0 : ItalianText.Coverage(anchors, ItalianText.TermSet(page));
                if (coverage < MinCoverage)
                {
                    Log.LogStep($"Web page {candidate.Url}: skipped, coverage {ItalianText.Num(coverage)}.");
                    continue;
                }

                if (rated >= MaxRatings) break;
                rated++;
                var source = Assess(caseType, facts, articles, candidate.Url, candidate.Title, page);
                if (source == null) continue;
                Log.LogStep($"Web page {candidate.Url}: usefulness={source.Usefulness} " +
                    $"reliability={source.Reliability} obsolete={source.Obsolete} used={source.Used}.");
                sources.Add(source);
            }

            var ordered = sources.OrderByDescending(s => s.Used).ThenByDescending(s => s.Usefulness).ToList();
            Log.LogStep($"Web enrichment: {ordered.Count} pages rated, {ordered.Count(s => s.Used)} used.");
            return ordered;
        }
        catch (Exception ex)
        {
            Log.LogStep($"Web enrichment skipped: {ex.GetType().Name}: {ex.Message}");
            return sources;
        }
    }

    private static List<string> BuildQueries(CaseType caseType, string facts, IReadOnlyList<LegalTopic> topics,
        IReadOnlyList<LegalDocument> articles)
    {
        var queries = new List<string>();
        var year = DateTime.Now.Year;
        var topicName = topics.Count > 0 ? topics[0].Name : string.Empty;
        var focus = LegalRetriever.BuildQuery(topics.Count > 0 ? topics[0].Name : facts, facts);
        var articleList = string.Join(", ", articles.Take(3).Select(a => "art. " + a.ArticleNumber));

        if (articleList.Length > 0)
            queries.Add($"Cassazione {topicName} {articleList} orientamento {year - 1} {year}");
        if (focus.Length > 0)
            queries.Add($"{(caseType == CaseType.Criminal ? "penale" : "civile")} {topicName} {focus} giurisprudenza");

        return queries.Where(q => q.Length > 8).Take(MaxQueries).ToList();
    }

    private static string? ReadPage(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) LegalAssistantTool/1.0");
            using var response = Client.Send(request);
            if (!response.IsSuccessStatusCode) return null;
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (mediaType.Contains("pdf", StringComparison.OrdinalIgnoreCase)) return null;
            using var stream = response.Content.ReadAsStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var buffer = new char[MaxPageChars * 2];
            var total = reader.ReadBlock(buffer, 0, buffer.Length);
            var html = new string(buffer, 0, total);
            var text = LegalAttachments.HtmlToText(html);
            return string.IsNullOrWhiteSpace(text) ? null : ItalianText.Clip(text, MaxPageChars);
        }
        catch (Exception ex)
        {
            Log.LogStep($"Web page {url} not readable: {ex.GetType().Name}. Skipped.");
            return null;
        }
    }

    private static WebSource? Assess(CaseType caseType, string facts, IReadOnlyList<LegalDocument> articles,
        string url, string title, string page)
    {
        var hostReliability = ReliabilityOf(url);
        var coverage = ItalianText.Coverage(LegalLexicon.Distinctive(ItalianText.Tokenize(facts)),
            ItalianText.TermSet(page));
        var fallback = new PageAssessment
        {
            Usefulness = (int)Math.Round(Math.Clamp(coverage, 0, 1) * 10),
            Reliable = hostReliability >= SourceReliability.Medium,
            Obsolete = MentionsRepeal(page),
            Reason = "valutazione deterministica (copertura lessicale e autorevolezza del dominio)",
        };

        // The model rates the page only when there is budget left: the deterministic rating below is
        // always available, and the opinion itself is more valuable than one more rated page.
        var assessment = LegalLlm.RemainingSeconds >= 90 ? LegalLlm.AskJson<PageAssessment>(
            "Sei un avvocato italiano. Valuta la pagina web seguente rispetto al caso.\n" +
            $"CASO ({(caseType == CaseType.Criminal ? "penale" : "civile")}): {ItalianText.Clip(facts, 1500)}\n" +
            $"ARTICOLI RILEVANTI: {string.Join("; ", articles.Take(6).Select(a => a.Citation))}\n" +
            $"URL: {url}\nTITOLO: {title}\nTESTO: {ItalianText.Clip(page, 3000)}\n\n" +
            "Restituisci JSON con: usefulness (0-10, utilità per questa specifica difesa), " +
            "reliable (true se la fonte è autorevole), obsolete (true se le informazioni appaiono superate " +
            "da abrogazioni o riforme), reason (max 25 parole).", "web-rating", 220, timeoutSeconds: 45) : null;
        assessment ??= fallback;

        var usefulness = Math.Clamp(assessment.Usefulness, 0, 10);
        var reliability = hostReliability;
        if (assessment.Reliable && reliability < SourceReliability.Medium) reliability = SourceReliability.Medium;
        if (!assessment.Reliable && reliability > SourceReliability.Medium) reliability = SourceReliability.Medium;

        var obsolete = assessment.Obsolete || MentionsRepeal(page);
        var used = usefulness >= MinUsefulness
                   && !obsolete
                   && reliability >= SourceReliability.Medium;

        return new WebSource(url, string.IsNullOrWhiteSpace(title) ? url : title,
            ItalianText.Clip(page, ExcerptChars), usefulness, reliability, obsolete,
            assessment.Reason ?? string.Empty, used);
    }

    /// <summary>Detects a page that reports a repeal of the rules it discusses.</summary>
    /// <param name="page">Page text.</param>
    /// <returns>True when the page mentions a repeal.</returns>
    private static bool MentionsRepeal(string page)
    {
        var text = ItalianText.Normalize(page);
        return text.Contains("abrogat", StringComparison.Ordinal)
               || text.Contains("dichiarata illegittim", StringComparison.Ordinal)
               || text.Contains("non piu in vigore", StringComparison.Ordinal);
    }

    /// <summary>Maps a URL to a reliability level using the domain lists.</summary>
    /// <param name="url">Page address.</param>
    /// <returns>The reliability level.</returns>
    public static SourceReliability ReliabilityOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return SourceReliability.Unknown;
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        if (AuthoritativeHosts.Any(h => host.EndsWith(h, StringComparison.Ordinal))) return SourceReliability.High;
        if (ReputableHosts.Any(h => host.EndsWith(h, StringComparison.Ordinal))) return SourceReliability.Medium;
        if (host.EndsWith(".gov.it", StringComparison.Ordinal) || host.EndsWith(".it", StringComparison.Ordinal))
            return SourceReliability.Low;
        return SourceReliability.Low;
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var line = text.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? string.Empty;
        return line.TrimStart('#', ' ', '\r');
    }

    /// <summary>Shape of the rating the LLM returns for one page.</summary>
    private sealed class PageAssessment
    {
        /// <summary>Usefulness for the case, 0-10.</summary>
        public int Usefulness { get; set; }

        /// <summary>Whether the model considers the source authoritative.</summary>
        public bool Reliable { get; set; }

        /// <summary>Whether the model considers the content superseded.</summary>
        public bool Obsolete { get; set; }

        /// <summary>Short justification.</summary>
        public string? Reason { get; set; }
    }
}