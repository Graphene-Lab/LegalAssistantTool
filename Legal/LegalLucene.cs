using Lucene.Net.Analysis;
using Lucene.Net.Analysis.It;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Directory = System.IO.Directory;
using LuceneDirectory = Lucene.Net.Store.Directory;

namespace AIOrchestrator.API.Legal;

/// <summary>
/// Lucene.Net index over the same normalised corpus, kept as the alternative engine so the
/// benchmark can compare it with SQLite FTS5 on identical documents and identical queries.
/// Two analysers are offered: the language-neutral standard one and the Italian one.
/// </summary>
internal static class LegalLucene
{
    /// <summary>Engine id of the Lucene index built with the Italian analyser.</summary>
    public const string ItalianEngine = "lucene-italian";

    /// <summary>Engine id of the Lucene index built with the standard analyser.</summary>
    public const string StandardEngine = "lucene-standard";

    /// <summary>Engine used by the tool when Lucene is selected.</summary>
    public const string DefaultEngine = ItalianEngine;

    private static readonly LuceneVersion Version = LuceneVersion.LUCENE_48;
    private static readonly object BuildLock = new();

    /// <summary>All engine ids this class can build.</summary>
    public static readonly string[] Engines = [StandardEngine, ItalianEngine];

    /// <summary>Creates the analyser for an engine id.</summary>
    /// <param name="engineId">Engine id.</param>
    /// <returns>The analyser (the caller owns it).</returns>
    public static Analyzer CreateAnalyzer(string engineId) => engineId == StandardEngine
        ? new StandardAnalyzer(Version)
        : new ItalianAnalyzer(Version);

    /// <summary>True when the index folder for the engine contains a readable index.</summary>
    /// <param name="engineId">Engine id.</param>
    /// <returns>True when the index can be searched.</returns>
    public static bool Exists(string engineId)
    {
        var path = LegalStorage.LuceneDirectory(engineId);
        if (!Directory.Exists(path)) return false;
        try
        {
            using var directory = FSDirectory.Open(path);
            return DirectoryReader.IndexExists(directory);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Rebuilds the Lucene index for the given engine.</summary>
    /// <param name="engineId">Engine id.</param>
    /// <param name="documents">Corpus to index.</param>
    public static void Build(string engineId, IReadOnlyList<LegalDocument> documents)
    {
        lock (BuildLock)
        {
            var path = LegalStorage.LuceneDirectory(engineId);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            Directory.CreateDirectory(path);

            using var analyzer = CreateAnalyzer(engineId);
            using var directory = FSDirectory.Open(path);
            using var writer = new IndexWriter(directory, new IndexWriterConfig(Version, analyzer)
            {
                OpenMode = OpenMode.CREATE,
            });

            foreach (var document in documents)
            {
                var luceneDocument = new Document
                {
                    new StringField("id", document.Id, Field.Store.YES),
                    new StringField("kind", document.Kind.ToString(), Field.Store.YES),
                    new StringField("casetype", document.CaseType?.ToString() ?? "both", Field.Store.YES),
                    new TextField("heading", document.Heading + " " + document.Matter, Field.Store.NO),
                    new TextField("body", document.Body, Field.Store.NO),
                    new TextField("citation", document.Citation, Field.Store.NO),
                };
                writer.AddDocument(luceneDocument);
            }

            writer.Commit();
            Log.LogStep($"Lucene index '{engineId}' built: {writer.NumDocs} documents at {path}");
        }
    }

    /// <summary>Runs a Lucene BM25 query.</summary>
    /// <param name="engineId">Engine id.</param>
    /// <param name="query">Free text query.</param>
    /// <param name="caseType">Branch of law to keep.</param>
    /// <param name="limit">Maximum number of hits.</param>
    /// <param name="kind">Document kind to keep, or null for both.</param>
    /// <param name="court">Court filter, or null for every court.</param>
    /// <returns>Hits, best first.</returns>
    public static List<LegalHit> Search(string engineId, string query, CaseType caseType, int limit, LegalDocKind? kind = null, string? court = null)
    {
        var hits = new List<LegalHit>();
        if (!Exists(engineId)) return hits;

        var path = LegalStorage.LuceneDirectory(engineId);
        try
        {
            using var analyzer = CreateAnalyzer(engineId);
            using var directory = FSDirectory.Open(path);
            using var reader = DirectoryReader.Open(directory);
            var searcher = new IndexSearcher(reader);
            var boolean = new BooleanQuery();

            foreach (var term in ItalianText.Tokenize(query).Distinct(StringComparer.Ordinal))
            {
                var heading = new TermQuery(new Term("heading", term));
                var body = new TermQuery(new Term("body", term));
                var citation = new TermQuery(new Term("citation", term));
                boolean.Add(heading, Occur.SHOULD);
                boolean.Add(body, Occur.SHOULD);
                boolean.Add(citation, Occur.SHOULD);
            }

            if (boolean.Clauses.Count == 0) return hits;

            var top = searcher.Search(boolean, limit * 8 + 40);
            foreach (var score in top.ScoreDocs)
            {
                var stored = searcher.Doc(score.Doc);
                var document = LegalCorpus.ById(stored.Get("id"));
                if (document == null) continue;
                if (kind is { } required && document.Kind != required) continue;
                if (!document.ServesCase(caseType)) continue;
                if (court != null && !string.Equals(LegalRetriever.CourtFamily(document), court, StringComparison.OrdinalIgnoreCase)) continue;
                hits.Add(new LegalHit(document, score.Score));
                if (hits.Count >= limit) break;
            }
        }
        catch (Exception ex)
        {
            Log.LogStep($"Lucene query failed on '{engineId}': {ex.GetType().Name}: {ex.Message}");
        }

        return hits;
    }
}