using System.Text;
using System.Text.Json;

namespace AIOrchestrator.API.Legal;

/// <summary>
/// In-memory view of the normalised corpus. Loading the JSONL once per process keeps retrieval
/// deterministic and fast: the search engines return document ids, the corpus turns them back
/// into the full record (verbatim text, exact citation, verifiable link).
/// </summary>
internal static class LegalCorpus
{
    private static readonly object Sync = new();
    private static List<LegalDocument>? _documents;
    private static Dictionary<string, LegalDocument>? _byId;
    private static Dictionary<string, LegalDocument>? _articlesByKey;

    /// <summary>All documents, loading them on first use.</summary>
    /// <param name="forceReload">True to drop the cache and read the JSONL again.</param>
    /// <returns>The corpus.</returns>
    public static List<LegalDocument> Load(bool forceReload = false)
    {
        lock (Sync)
        {
            if (_documents != null && !forceReload) return _documents;

            var documents = new List<LegalDocument>(24000);
            ReadJsonLines(LegalStorage.ArticlesJsonl, documents);
            ReadJsonLines(LegalStorage.JudgmentsJsonl, documents);

            var byId = new Dictionary<string, LegalDocument>(documents.Count, StringComparer.Ordinal);
            var articlesByKey = new Dictionary<string, LegalDocument>(StringComparer.OrdinalIgnoreCase);
            foreach (var document in documents)
            {
                byId[document.Id] = document;
                if (document.Kind == LegalDocKind.Article)
                    articlesByKey[document.CodeId + ":" + document.ArticleNumber] = document;
            }

            _documents = documents;
            _byId = byId;
            _articlesByKey = articlesByKey;
            Log.LogStep($"Corpus loaded: {documents.Count} documents " +
                $"({documents.Count(d => d.Kind == LegalDocKind.Article)} articles, " +
                $"{documents.Count(d => d.Kind == LegalDocKind.Judgment)} judgments).");
            return _documents;
        }
    }

    /// <summary>Looks a document up by its stable id.</summary>
    /// <param name="id">Document id.</param>
    /// <returns>The document, or null.</returns>
    public static LegalDocument? ById(string id)
    {
        Load();
        return _byId!.TryGetValue(id, out var document) ? document : null;
    }

    /// <summary>Looks an article up by code and published number (for example penale + 624).</summary>
    /// <param name="codeId">Code id ("penale" or "civile").</param>
    /// <param name="number">Article number as published.</param>
    /// <returns>The article, or null when the archive does not contain it.</returns>
    public static LegalDocument? Article(string codeId, string number)
    {
        Load();
        return _articlesByKey!.TryGetValue(codeId + ":" + number, out var document) ? document : null;
    }

    /// <summary>Drops the cache (used after a rebuild).</summary>
    public static void Invalidate()
    {
        lock (Sync)
        {
            _documents = null;
            _byId = null;
            _articlesByKey = null;
        }
    }

    private static void ReadJsonLines(string path, List<LegalDocument> sink)
    {
        if (!File.Exists(path))
        {
            Log.LogStep($"Corpus file missing: {path}");
            return;
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.Length < 3) continue;
            try
            {
                var document = JsonSerializer.Deserialize<LegalDocument>(line, options);
                if (document != null && document.Id.Length > 0) sink.Add(document);
            }
            catch (JsonException ex)
            {
                Log.LogStep($"Corpus line skipped: {ex.Message}");
            }
        }
    }
}