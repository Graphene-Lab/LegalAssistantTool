using System.Text;

namespace AIOrchestrator.API.Legal;

/// <summary>Pluggable retrieval engine: same corpus, same query, different index technology.</summary>
internal interface ILegalSearchEngine
{
    /// <summary>Engine id.</summary>
    string Id { get; }

    /// <summary>Runs a query.</summary>
    /// <param name="query">Free text query.</param>
    /// <param name="caseType">Branch of law to keep.</param>
    /// <param name="limit">Maximum number of hits.</param>
    /// <param name="kind">Document kind to keep, or null for both.</param>
        /// <param name="court">Court family to keep, or null for every court.</param>
    /// <returns>Hits, best first.</returns>
    List<LegalHit> Search(string query, CaseType caseType, int limit, LegalDocKind? kind, string? court = null);
}

/// <summary>SQLite FTS5 + BM25 with the Italian fold and stem on both sides.</summary>
internal sealed class Fts5LegalSearchEngine : ILegalSearchEngine
{
    /// <summary>Engine id.</summary>
    public string Id => LegalIndex.EngineId;

    /// <summary>Runs a query.</summary>
    /// <param name="query">Free text query.</param>
    /// <param name="caseType">Branch of law to keep.</param>
    /// <param name="limit">Maximum number of hits.</param>
    /// <param name="kind">Document kind to keep, or null for both.</param>
    /// <param name="court">Court filter, or null for every court.</param>
    /// <returns>Hits, best first.</returns>
    public List<LegalHit> Search(string query, CaseType caseType, int limit, LegalDocKind? kind, string? court = null) =>
        LegalIndex.Search(query, caseType, limit, kind, court);
}

/// <summary>Lucene.Net BM25 with the Italian analyser.</summary>
internal sealed class LuceneLegalSearchEngine : ILegalSearchEngine
{
    /// <summary>Engine id.</summary>
    public string Id => LegalLucene.ItalianEngine;

    /// <summary>Runs a query.</summary>
    /// <param name="query">Free text query.</param>
    /// <param name="caseType">Branch of law to keep.</param>
    /// <param name="limit">Maximum number of hits.</param>
    /// <param name="kind">Document kind to keep, or null for both.</param>
    /// <param name="court">Court filter, or null for every court.</param>
    /// <returns>Hits, best first.</returns>
    public List<LegalHit> Search(string query, CaseType caseType, int limit, LegalDocKind? kind, string? court = null) =>
        LegalLucene.Search(LegalLucene.ItalianEngine, query, caseType, limit, kind, court);
}

/// <summary>Lucene.Net BM25 with the language-neutral standard analyser.</summary>
internal sealed class LuceneStandardLegalSearchEngine : ILegalSearchEngine
{
    /// <summary>Engine id.</summary>
    public string Id => LegalLucene.StandardEngine;

    /// <summary>Runs a query.</summary>
    /// <param name="query">Free text query.</param>
    /// <param name="caseType">Branch of law to keep.</param>
    /// <param name="limit">Maximum number of hits.</param>
    /// <param name="kind">Document kind to keep, or null for both.</param>
    /// <param name="court">Court filter, or null for every court.</param>
    /// <returns>Hits, best first.</returns>
    public List<LegalHit> Search(string query, CaseType caseType, int limit, LegalDocKind? kind, string? court = null) =>
        LegalLucene.Search(LegalLucene.StandardEngine, query, caseType, limit, kind, court);
}

/// <summary>Resolves the engine id used by the tool.</summary>
internal static class LegalSearchEngines
{
    /// <summary>Environment variable that overrides the engine (used by the benchmark and tests).</summary>
    public const string EngineVariable = "LEGALASSISTANTTOOL_ENGINE";

    private static readonly Dictionary<string, ILegalSearchEngine> Registry = new(StringComparer.OrdinalIgnoreCase)
    {
        [LegalIndex.EngineId] = new Fts5LegalSearchEngine(),
        [LegalLucene.ItalianEngine] = new LuceneLegalSearchEngine(),
        [LegalLucene.StandardEngine] = new LuceneStandardLegalSearchEngine(),
    };

    /// <summary>All known engine ids.</summary>
    public static IReadOnlyCollection<string> Ids => Registry.Keys;

    /// <summary>Engine used when nothing overrides it.</summary>
    public static string DefaultId => LegalIndex.EngineId;

    /// <summary>Returns the engine for an id, falling back to the default one.</summary>
    /// <param name="engineId">Requested engine id, or null.</param>
    /// <returns>The engine.</returns>
    public static ILegalSearchEngine Resolve(string? engineId = null)
    {
        var requested = engineId;
        if (string.IsNullOrWhiteSpace(requested))
            requested = Environment.GetEnvironmentVariable(EngineVariable);
        if (!string.IsNullOrWhiteSpace(requested) && Registry.TryGetValue(requested, out var engine))
            return engine;
        return Registry[DefaultId];
    }
}

/// <summary>Accumulated research for one case: the topics it matched and the sources it found.</summary>
internal sealed class LegalResearch
{
    /// <summary>Bonus given to a curated article the engine already found for these very facts.</summary>
    private const double CuratedBonus = 0.24;

    /// <summary>Bonus when the rubric of the article is a multi-word phrase of the question ("furto aggravato" → art. 625).</summary>
    private const double RubricPhraseBonus = 0.20;

    /// <summary>Score given to a curated article the engine missed: the topic list is the norm set of the case.</summary>
    private const double CuratedMissBase = 0.55;

    /// <summary>Lowest score a curated article can receive, so it stays above the lexical long tail.</summary>
    private const double CuratedMissFloor = 0.40;

    /// <summary>Bonus when a one-word rubric is a distinctive word of the question ("furto" → art. 624).</summary>
    private const double RubricWordBonus = 0.12;

    private readonly Dictionary<string, double> _articleScores = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _judgmentScores = new(StringComparer.Ordinal);

    /// <summary>Branch of law being researched.</summary>
    public required CaseType CaseType { get; init; }

    /// <summary>Topics detected from the facts, best first.</summary>
    public List<LegalTopic> Topics { get; } = [];

    /// <summary>Queries actually executed, in order (for the log and the audit trail).</summary>
    public List<string> Queries { get; } = [];

    /// <summary>Article numbers the curated topics point at.</summary>
    public HashSet<string> CuratedNumbers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Article hits, best first.</summary>
    public List<LegalHit> Articles { get; private set; } = [];

    /// <summary>Judgment hits, best first.</summary>
    public List<LegalHit> Judgments { get; private set; } = [];

    /// <summary>True when nothing relevant was found at all.</summary>
    public bool IsEmpty => Articles.Count == 0 && Judgments.Count == 0;

    /// <summary>Merges article hits, keeping the best score per document.</summary>
    /// <param name="hits">Hits to merge.</param>
    /// <param name="weight">Multiplier applied to the incoming scores.</param>
    public void AddArticles(IEnumerable<LegalHit> hits, double weight = 1.0)
    {
        foreach (var hit in Normalize(hits))
        {
            var score = hit.Score * weight;
            if (_articleScores.TryGetValue(hit.Document.Id, out var existing) && existing >= score) continue;
            _articleScores[hit.Document.Id] = score;
        }
    }

    /// <summary>Merges judgment hits, keeping the best score per document.</summary>
    /// <param name="hits">Hits to merge.</param>
    /// <param name="weight">Multiplier applied to the incoming scores.</param>
    public void AddJudgments(IEnumerable<LegalHit> hits, double weight = 1.0)
    {
        foreach (var hit in Normalize(hits))
        {
            var score = hit.Score * weight;
            if (_judgmentScores.TryGetValue(hit.Document.Id, out var existing) && existing >= score) continue;
            _judgmentScores[hit.Document.Id] = score;
        }
    }

    /// <summary>
    /// Merges the editorial article list into the ranking. The list is a recall guarantee, not an
    /// answer: an article the engine already found for these very facts is lifted by a bonus, one the
    /// engine missed is appended below the lexical evidence, and an article whose rubric is the
    /// question itself ("furto aggravato" → "Furto aggravato") is lifted above the term statistics,
    /// because a code rubric that names the case is the norm the case is about.
    /// </summary>
    /// <param name="curated">Curated articles, in editorial order.</param>
    /// <param name="question">Text of the question (facts and suggestions).</param>
    public void BoostCurated(IReadOnlyList<LegalDocument> curated, string question)
    {
        var questionTerms = ItalianText.TermSet(question);
        var normalizedQuestion = ItalianText.Normalize(question);
        for (var index = 0; index < curated.Count; index++)
        {
            var document = curated[index];
            var score = _articleScores.TryGetValue(document.Id, out var found)
                ? found + CuratedBonus
                : Math.Max(CuratedMissFloor, CuratedMissBase - index * 0.003);
            score += RubricBonus(document, normalizedQuestion, questionTerms);
            _articleScores[document.Id] = Math.Min(0.98, score);
        }
    }

    /// <summary>Guarantees the curated articles a slot: the size limit may never hide the norm of the topic.</summary>
    /// <param name="curated">Curated articles, in editorial order.</param>
    /// <param name="maxArticles">Size of the answer.</param>
    public void EnsureCurated(IReadOnlyList<LegalDocument> curated, int maxArticles)
    {
        if (curated.Count == 0) return;
        var kept = Articles.ToList();
        var present = new HashSet<string>(kept.Select(h => h.Document.Id), StringComparer.Ordinal);
        foreach (var article in curated)
        {
            if (present.Contains(article.Id) || !article.IsCitable) continue;
            var score = _articleScores.TryGetValue(article.Id, out var known) ? known : 0.22;
            if (kept.Count >= maxArticles) break;
            kept.Add(new LegalHit(article, score));
            present.Add(article.Id);
            Log.LogStep($"Curated article {article.Citation} kept in the answer (the engine missed it).");
        }

        Articles = kept.OrderByDescending(hit => hit.Score).ToList();
    }

    /// <summary>Rewards an article whose rubric names the question.</summary>
    /// <param name="document">Curated article.</param>
    /// <param name="normalizedQuestion">Normalised question text.</param>
    /// <param name="questionTerms">Stemmed terms of the question.</param>
    /// <returns>The bonus to add, zero when the rubric does not match.</returns>
    private static double RubricBonus(LegalDocument document, string normalizedQuestion, HashSet<string> questionTerms)
    {
        var rubric = ItalianText.Normalize(document.Matter);
        if (rubric.Length < 4) return 0;
        if (rubric.Contains(' ') && normalizedQuestion.Contains(rubric, StringComparison.Ordinal))
            return RubricPhraseBonus;

        var words = ItalianText.Tokenize(rubric);
        if (words.Count != 1) return 0;
        return questionTerms.Contains(ItalianText.Stem(words[0])) ? RubricWordBonus : 0;
    }

    /// <summary>Drops the case law, used when no norm anchors it.</summary>
    public void ClearJudgments()
    {
        _judgmentScores.Clear();
        Judgments = [];
    }

    /// <summary>Sorts both lists by score and trims them to the requested sizes.</summary>
    /// <param name="maxArticles">Maximum articles to keep.</param>
    /// <param name="maxJudgments">Maximum judgments to keep.</param>
    public void Finalize(int maxArticles, int maxJudgments)
    {
        Articles = Rank(_articleScores, maxArticles);
        Judgments = Rank(_judgmentScores, maxJudgments);
    }

    /// <summary>
    /// Drops the hits that do not share enough discriminating terms with the question, and keeps the
    /// court mix balanced so the answer is not six decisions of the same panel. Curated articles are
    /// never dropped: they come from the editorial topic list, not from term statistics.
    /// </summary>
    /// <param name="anchors">Discriminating terms of the question.</param>
    /// <param name="minimum">How many of them a document must contain.</param>
    /// <param name="maxJudgments">Maximum number of judgments to keep.</param>
    public void ApplyRelevanceFloor(IReadOnlyList<string> anchors, int minimum, int maxJudgments = 8)
    {
        if (anchors.Count == 0) return;
        var cache = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        int AnchorHits(LegalDocument document)
        {
            if (!cache.TryGetValue(document.Id, out var terms))
            {
                terms = ItalianText.TermSet(document.SearchText);
                cache[document.Id] = terms;
            }

            return anchors.Count(terms.Contains);
        }

        bool RelevantArticle(LegalDocument document) =>
            CuratedNumbers.Contains(document.ArticleNumber) || AnchorHits(document) >= minimum;

        // A massima states the principle in abstract language, so it legitimately shares less
        // vocabulary with the facts than an article does: it may pass with one anchor less, and a
        // judgment that cites one of the articles of the case always passes (affinity is a precise
        // signal, not a lexical one).
        var judgmentMinimum = Math.Clamp((int)Math.Ceiling(anchors.Count * 0.25), 2, 4);
        bool RelevantJudgment(LegalDocument document) =>
            LegalRetriever.ArticleAffinity(document, CuratedNumbers) > 0 || AnchorHits(document) >= judgmentMinimum;

        Articles = Articles.Where(h => RelevantArticle(h.Document)).ToList();
        Judgments = Diversify(Judgments.Where(h => RelevantJudgment(h.Document)).ToList(), maxJudgments);
    }

    /// <summary>
    /// Builds the case-law section with a court policy instead of a pure ranking: half of the slots
    /// are reserved for the Court of Cassation, because that is the interpretation a judge applies,
    /// while the Constitutional Court - which fills 97% of the archive - only takes the remaining
    /// slots. Within each group the score order is preserved, and when a group has fewer candidates
    /// the other one fills the gap, so the section is never artificially short.
    /// </summary>
    /// <param name="hits">Hits, best first.</param>
    /// <param name="maxJudgments">Size of the section.</param>
    /// <returns>The balanced list.</returns>
    private static List<LegalHit> Diversify(List<LegalHit> hits, int maxJudgments)
    {
        if (hits.Count == 0) return hits;
        var quota = Math.Max(1, maxJudgments / 2);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<LegalHit>(Math.Min(hits.Count, maxJudgments));

        foreach (var hit in hits.Where(h => LegalRetriever.CourtFamily(h.Document) == "Corte di cassazione").Take(quota))
            if (taken.Add(hit.Document.Id)) result.Add(hit);

        var perCourt = Math.Max(2, maxJudgments / 3);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in result)
        {
            var family = LegalRetriever.CourtFamily(hit.Document);
            counts[family] = counts.TryGetValue(family, out var used) ? used + 1 : 1;
        }

        foreach (var hit in hits)
        {
            if (result.Count >= maxJudgments) break;
            if (taken.Contains(hit.Document.Id)) continue;
            var family = LegalRetriever.CourtFamily(hit.Document);
            counts.TryGetValue(family, out var used);
            if (used >= perCourt) continue;
            counts[family] = used + 1;
            if (taken.Add(hit.Document.Id)) result.Add(hit);
        }

        // Fill any remaining slot ignoring the per-court cap: a section that is one item short is
        // worse than a section with three decisions of the same court.
        foreach (var hit in hits)
        {
            if (result.Count >= maxJudgments) break;
            if (taken.Add(hit.Document.Id)) result.Add(hit);
        }

        return result;
    }

    /// <summary>Adds a document with an explicit score.</summary>
    /// <param name="document">Document to add.</param>
    /// <param name="score">Score to assign when higher than the current one.</param>
    public void AddDocument(LegalDocument document, double score)
    {
        var bucket = document.Kind == LegalDocKind.Article ? _articleScores : _judgmentScores;
        if (bucket.TryGetValue(document.Id, out var existing) && existing >= score) return;
        bucket[document.Id] = score;
    }

    /// <summary>
    /// Rescales a batch of engine scores to 0..0.9 so that a curated article (1.0 and below) always
    /// outranks a purely statistical hit: the legal hand-list is an editorial judgement about what
    /// the case is about, and it must not be overruled by BM25 term statistics.
    /// </summary>
    /// <param name="hits">Hits returned by the engine.</param>
    /// <returns>Hits with comparable scores.</returns>
    private static IEnumerable<LegalHit> Normalize(IEnumerable<LegalHit> hits)
    {
        var list = hits.ToList();
        if (list.Count == 0) return list;
        var max = list.Max(h => h.Score);
        if (max <= 0) return list;
        return list.Select(h => h with { Score = h.Score / max * 0.9 });
    }
    private static List<LegalHit> Rank(Dictionary<string, double> scores, int limit) =>
        scores
            .Select(pair => (Document: LegalCorpus.ById(pair.Key), Score: pair.Value))
            .Where(pair => pair.Document != null)
            .OrderByDescending(pair => pair.Score)
            .ThenBy(pair => pair.Document!.Id, StringComparer.Ordinal)
            .Take(limit)
            .Select(pair => new LegalHit(pair.Document!, pair.Score))
            .ToList();
}

/// <summary>
/// Legal-aware retrieval: the engine ranks the corpus, then deterministic legal knowledge is
/// layered on top. The curated article set of a matched topic always enters the candidate pool,
/// judgments that cite one of those articles are promoted, and repealed or unconstitutional
/// articles are dropped so an opinion can never rest on dead law.
/// </summary>
internal static class LegalRetriever
{
    /// <summary>Maximum number of query terms handed to an engine (keeps BM25 focused and fast).</summary>
    public const int MaxQueryTerms = 48;

    /// <summary>Runs a free-text query on the given engine.</summary>
    /// <param name="query">Query text.</param>
    /// <param name="caseType">Branch of law.</param>
    /// <param name="limit">Maximum hits.</param>
    /// <param name="kind">Document kind, or null for both.</param>
    /// <param name="engineId">Engine id, or null for the default one.</param>
    /// <returns>Hits, best first.</returns>
    public static List<LegalHit> Search(string query, CaseType caseType, int limit, LegalDocKind? kind, string? engineId = null) =>
        LegalSearchEngines.Resolve(engineId).Search(query, caseType, limit, kind);

    /// <summary>Builds a compact query from several fragments, keeping the most specific terms.</summary>
    /// <param name="fragments">Text fragments in priority order.</param>
    /// <returns>A space-joined term string capped at <see cref="MaxQueryTerms"/> distinct stems.</returns>
    public static string BuildQuery(params string?[] fragments)
    {
        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fragment in fragments)
        {
            if (string.IsNullOrWhiteSpace(fragment)) continue;
            foreach (var token in ItalianText.Tokenize(fragment))
            {
                if (tokens.Count >= MaxQueryTerms * 2) break;
                if (seen.Add(token)) tokens.Add(token);
            }
        }

        var distinctive = LegalLexicon.Distinctive(tokens);
        return string.Join(' ', distinctive.Take(MaxQueryTerms));
    }

    /// <summary>Curated articles of the matched topics, looked up directly in the corpus.</summary>
    /// <param name="caseType">Branch of law.</param>
    /// <param name="topics">Matched topics, best first.</param>
    /// <returns>The articles that actually exist in the archive, in curated order.</returns>
    public static List<LegalDocument> CuratedArticles(CaseType caseType, IReadOnlyList<LegalTopic> topics)
    {
        var result = new List<LegalDocument>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var topic in topics)
        {
            var codeId = caseType == CaseType.Criminal ? "penale" : "civile";
            foreach (var number in topic.ArticlesFor(caseType))
            {
                var article = LegalCorpus.Article(codeId, number);
                if (article == null)
                {
                    Log.LogStep($"Curated article {codeId} {number} not found in the archive (topic {topic.Id}).");
                    continue;
                }

                if (!article.IsCitable)
                {
                    Log.LogStep($"Curated article {article.Citation} excluded: {article.StateLabel}.");
                    continue;
                }

                if (seen.Add(article.Id)) result.Add(article);
            }
        }

        return result;
    }

    /// <summary>
    /// Collapses the court name to its institution ("Corte di cassazione", "Corte costituzionale").
    /// Sections are panels of the same court, so they must not defeat the diversity cap.
    /// </summary>
    /// <param name="judgment">Judgment to inspect.</param>
    /// <returns>The court family.</returns>
    public static string CourtFamily(LegalDocument judgment)
    {
        var court = string.IsNullOrWhiteSpace(judgment.Court) ? judgment.Heading : judgment.Court;
        var comma = court.IndexOf(',');
        var family = comma > 0 ? court[..comma].Trim() : court.Trim();
        return family.StartsWith("Cass.", StringComparison.OrdinalIgnoreCase) ? "Corte di cassazione" : family;
    }

    /// <summary>
    /// Editorial weight of a court for a defence-oriented research: the Court of Cassation decides
    /// on the merits of the legal question and is the primary source of a winning argument, the
    /// Constitutional Court only rules on the legitimacy of the norm.
    /// </summary>
    /// <param name="judgment">Judgment to weigh.</param>
    /// <returns>Multiplier applied to the score.</returns>
    public static double CourtWeight(LegalDocument judgment) => CourtFamily(judgment) switch
    {
        "Corte di cassazione" => 1.40,
        "Corte costituzionale" => 1.00,
        _ => 0.95
    };

    /// <summary>
    /// Final score of a judgment. Lexical similarity alone is not enough: the archive is dominated
    /// by Constitutional Court decisions that quote every article of the penal code, so the score is
    /// dominated by how precisely the decision interprets the articles of the case (affinity) and,
    /// secondarily, by the authority of the court.
    /// </summary>
    /// <param name="hit">Judgment hit, score already normalised.</param>
    /// <param name="curatedNumbers">Article numbers of the case.</param>
    /// <returns>The blended score.</returns>
    public static double JudgmentScore(LegalHit hit, IReadOnlyCollection<string> curatedNumbers)
    {
        var affinity = ArticleAffinity(hit.Document, curatedNumbers);
        return 0.6 * hit.Score + 1.2 * affinity * CourtWeight(hit.Document);
    }

    /// <summary>How strongly a judgment is about the given article numbers (0..1).</summary>
    /// <param name="judgment">Judgment to inspect.</param>
    /// <param name="articleNumbers">Article numbers of the case.</param>
    /// <returns>Affinity score.</returns>
    public static double ArticleAffinity(LegalDocument judgment, IReadOnlyCollection<string> articleNumbers)
    {
        if (articleNumbers.Count == 0) return 0;
        var text = ItalianText.Normalize(judgment.SearchText);
        var hits = 0;
        foreach (var number in articleNumbers)
        {
            if (text.Contains("art. " + number, StringComparison.Ordinal)
                || text.Contains("art." + number, StringComparison.Ordinal)
                || text.Contains("articolo " + number, StringComparison.Ordinal))
                hits++;
        }

        return Math.Min(1.0, (double)hits / Math.Min(3, articleNumbers.Count));
    }

    /// <summary>
    /// Runs the deterministic research loop: repeated engine queries built from the facts, the
    /// topic vocabulary and the defence checklist, merged with the curated article set.
    /// </summary>
    /// <param name="caseType">Branch of law.</param>
    /// <param name="facts">Description of the facts.</param>
    /// <param name="extraTerms">Additional vocabulary (suggestions, attachment highlights).</param>
    /// <param name="maxArticles">Maximum articles to keep.</param>
    /// <param name="maxJudgments">Maximum judgments to keep.</param>
    /// <param name="rounds">Maximum number of research rounds.</param>
    /// <param name="engineId">Engine id, or null for the default one.</param>
    /// <returns>The accumulated research.</returns>
    public static LegalResearch Research(CaseType caseType, string facts, IEnumerable<string>? extraTerms = null,
        int maxArticles = 12, int maxJudgments = 8, int rounds = 3, string? engineId = null)
    {
        var engine = LegalSearchEngines.Resolve(engineId);
        var research = new LegalResearch { CaseType = caseType };

        var suggestionText = extraTerms == null ? string.Empty : string.Join(" ", extraTerms);
        var topics = LegalThesaurus.Match(caseType, facts + " " + suggestionText);
        research.Topics.AddRange(topics);
        Log.LogStep($"Research topics ({topics.Count}): {string.Join(", ", topics.Select(t => t.Id))}");

        var curated = CuratedArticles(caseType, topics);
        foreach (var number in curated.Select(a => a.ArticleNumber)) research.CuratedNumbers.Add(number);
        Log.LogStep($"Curated articles: {curated.Count} ({string.Join(", ", curated.Take(12).Select(a => a.ArticleNumber))})");

        var expansion = LegalThesaurus.ExpansionTerms(topics);
        var defenceTerms = LegalThesaurus.DefencePoints(topics).SelectMany(ItalianText.Tokenize).Distinct().Take(24).ToList();

        var queries = new List<string>
        {
            facts,
            BuildQuery(string.Join(' ', expansion), string.Join(' ', defenceTerms), suggestionText),
            BuildQuery(string.Join(' ', curated.Select(a => a.NumberAndRubric())), facts),
        };

        var executed = 0;
        foreach (var query in queries.Distinct(StringComparer.Ordinal))
        {
            if (executed >= rounds || query.Length == 0) break;
            executed++;
            research.Queries.Add(query);
            var articleHits = engine.Search(query, caseType, maxArticles * 2, LegalDocKind.Article);
            var judgmentHits = engine.Search(query, caseType, maxJudgments * 3, LegalDocKind.Judgment);
            Log.LogStep($"Round {executed} on {engine.Id}: query='{ItalianText.Clip(query, 160)}' " +
                $"articles={articleHits.Count} judgments={judgmentHits.Count}");

            // The words of the caller are the strongest signal: the expansion round reaches the code
            // language, the rubric round reaches the editorial vocabulary, but neither may outrank a
            // norm that matches the facts as written.
            var weight = executed == 1 ? 1.0 : executed == 2 ? 0.80 : 0.65;
            research.AddArticles(articleHits.Where(h => h.Document.IsCitable), weight);
            research.AddJudgments(judgmentHits.Select(h => h with
            {
                Score = JudgmentScore(h, research.CuratedNumbers)
            }));
        }

        if (curated.Count > 0) research.BoostCurated(curated, facts + " " + suggestionText);

        if (curated.Count > 0)
        {
            // Dedicated case-law pass: the article numbers of the case are the sharpest key the
            // archive offers, because every scheda lists the articles it interprets.
            var fallback = BuildQuery(string.Join(' ', curated.Select(a => a.Citation)), facts);
            research.Queries.Add(fallback);
            // The archive is dominated by the Constitutional Court, so a wide candidate pool is needed
            // for the Court of Cassation - the court that actually decides the legal question - to
            // reach the ranking at all.
            var hits = engine.Search(fallback, caseType, Math.Max(80, maxJudgments * 12), LegalDocKind.Judgment);
            research.AddJudgments(hits.Select(h => h with
            {
                Score = JudgmentScore(h, research.CuratedNumbers)
            }));
            Log.LogStep($"Judgment citation query returned {hits.Count} hits.");

            // The archive holds 22k Constitutional Court decisions against a few hundred Cassation
            // ones, so a plain BM25 ranking never lets the Court of Cassation emerge. Cassation is
            // the court whose interpretation a judge actually applies, so it gets a dedicated pass.
            var cassation = engine.Search(fallback, caseType, Math.Max(30, maxJudgments * 4),
                LegalDocKind.Judgment, "Corte di cassazione");
            research.AddJudgments(cassation.Select(h => h with
            {
                Score = JudgmentScore(h, research.CuratedNumbers)
            }));
            Log.LogStep($"Cassation pass returned {cassation.Count} hits.");
        }

        research.Finalize(maxArticles, maxJudgments);

        // Relevance floor: a document is kept only when it shares enough discriminating vocabulary
        // with the question. Without this gate a nonsense topic still returns "relevant" articles,
        // because every article in the archive is about a reato or a contratto.
        var anchors = LegalLexicon.Distinctive(ItalianText.Tokenize(facts + " " + suggestionText));
        anchors = anchors.Take(24).ToList();
        var minimum = anchors.Count >= 4 ? 2 : 1;
        var beforeArticles = research.Articles.Count;
        var beforeJudgments = research.Judgments.Count;
        research.ApplyRelevanceFloor(anchors, minimum, maxJudgments);
        research.EnsureCurated(curated, maxArticles);
        if (beforeArticles != research.Articles.Count || beforeJudgments != research.Judgments.Count)
            Log.LogStep($"Relevance floor ({minimum} of {anchors.Count} anchors): " +
                $"articles {beforeArticles}->{research.Articles.Count}, judgments {beforeJudgments}->{research.Judgments.Count}.");

        // Case law is only usable when a norm anchors it: if no article of the case survived, the
        // question is outside what the archive covers, and showing decisions alone would suggest a
        // relevance that does not exist.
        if (research.Articles.Count == 0 && research.Judgments.Count > 0)
        {
            Log.LogStep($"No article anchors the question: {research.Judgments.Count} judgments dropped.");
            research.ClearJudgments();
        }

        Log.LogStep($"Research finalised: {research.Articles.Count} articles, {research.Judgments.Count} judgments.");
        return research;
    }

    /// <summary>Formats an article for a query fragment ("art. 624 furto").</summary>
    /// <param name="article">Article document.</param>
    /// <returns>Compact reference text.</returns>
    private static string NumberAndRubric(this LegalDocument article) =>
        $"art. {article.ArticleNumber} {article.Matter}";
}