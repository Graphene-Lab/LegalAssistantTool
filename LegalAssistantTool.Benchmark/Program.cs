namespace LegalAssistantToolBenchmark;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using AIOrchestrator.API.Legal;

/// <summary>
/// Compares the candidate retrieval engines on the same corpus, the same queries and the same gold
/// set, and prints the table published in README.md.
/// </summary>
internal static class Program
{
    private const int TopK = 10;
    private const int PAt = 5;

    private sealed class Metrics
    {
        public List<double> Precision { get; } = [];
        public List<double> Recall { get; } = [];
        public List<double> ReciprocalRank { get; } = [];
        public List<double> Affinity { get; } = [];
        public List<double> LatencyMs { get; } = [];

        public double MeanPrecision => Precision.Count == 0 ? 0 : Precision.Average();
        public double MeanRecall => Recall.Count == 0 ? 0 : Recall.Average();
        public double Mrr => ReciprocalRank.Count == 0 ? 0 : ReciprocalRank.Average();
        public double MeanAffinity => Affinity.Count == 0 ? 0 : Affinity.Average();
        public double MeanLatency => LatencyMs.Count == 0 ? 0 : LatencyMs.Average();
    }

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var reportPath = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "benchmark-results.md");

        Console.WriteLine("== legal retrieval benchmark ==");
        var readiness = LegalArchive.EnsureReady(TimeSpan.FromSeconds(180));
        if (readiness.Status != ArchiveStatus.Ready)
        {
            Console.WriteLine($"archive not ready: {readiness.Status} ({readiness.Message})");
            return 1;
        }

        var corpus = LegalCorpus.Load();
        var articles = corpus.Count(d => d.Kind == LegalDocKind.Article);
        var judgments = corpus.Count(d => d.Kind == LegalDocKind.Judgment);
        Console.WriteLine($"corpus: {articles} articles, {judgments} judgments");

        // The standard-analyser index is not built by the archive (it is only needed here).
        foreach (var engineId in LegalLucene.Engines)
        {
            if (LegalLucene.Exists(engineId)) continue;
            Console.WriteLine($"building Lucene index '{engineId}'...");
            var watch = Stopwatch.StartNew();
            LegalLucene.Build(engineId, corpus);
            Console.WriteLine($"  built in {watch.Elapsed.TotalSeconds:F1}s");
        }

        var articleIndex = corpus
            .Where(d => d.Kind == LegalDocKind.Article && !string.IsNullOrEmpty(d.ArticleNumber))
            .Select(d => Key(d.ArticleNumber))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var gold = new List<(GoldQuery Query, HashSet<string> Articles)>();
        var missing = new List<string>();
        foreach (var query in GoldSet.Queries)
        {
            var known = query.Articles.Where(articleIndex.Contains).Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var unknown in query.Articles.Where(a => !articleIndex.Contains(Key(a))))
                missing.Add($"{query.Id}:{unknown}");
            if (known.Count == 0)
            {
                Console.WriteLine($"  WARNING {query.Id}: no gold article exists in the archive, query skipped");
                continue;
            }

            gold.Add((query, known));
        }

        Console.WriteLine($"gold queries: {gold.Count} (missing articles dropped: {missing.Count})");
        if (missing.Count > 0) Console.WriteLine("  dropped: " + string.Join(", ", missing));
        Console.WriteLine();

        var systems = new (string Id, string Label, Func<GoldQuery, HashSet<string>, (List<string> Articles, List<LegalDocument> Judgments)> Run)[]
        {
            ("fts5-bm25", "SQLite FTS5 + BM25 (Italian fold+stem)",
                (query, _) => EngineRun(LegalSearchEngines.Resolve(LegalIndex.EngineId), query)),
            ("lucene-standard", "Lucene.Net 4.8 BM25 + StandardAnalyzer",
                (query, _) => EngineRun(LegalSearchEngines.Resolve(LegalLucene.StandardEngine), query)),
            ("lucene-italian", "Lucene.Net 4.8 BM25 + ItalianAnalyzer",
                (query, _) => EngineRun(LegalSearchEngines.Resolve(LegalLucene.ItalianEngine), query)),
            ("legal-hybrid", "Hybrid: curated topics + relevance floor + court mix (FTS5)",
                (query, curated) => HybridRun(query, curated)),
        };

        var diag = args.Contains("diag", StringComparer.OrdinalIgnoreCase);
        var results = new List<(string Label, Metrics Metrics)>();
        foreach (var system in systems)
        {
            var metrics = new Metrics();
            foreach (var (query, goldArticles) in gold)
            {
                var watch = Stopwatch.StartNew();
                var (foundArticles, foundJudgments) = system.Run(query, goldArticles);
                watch.Stop();
                metrics.LatencyMs.Add(watch.Elapsed.TotalMilliseconds);

                var distinct = foundArticles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (diag)
                    Console.WriteLine($"  [{system.Id,-16}] {query.Id,-22} gold={string.Join(",", goldArticles)} " +
                                      $"top={string.Join(",", distinct.Take(TopK))}");
                var hitsAtP = distinct.Take(PAt).Count(goldArticles.Contains);
                metrics.Precision.Add((double)hitsAtP / PAt);

                var hitsAtK = distinct.Take(TopK).Count(goldArticles.Contains);
                metrics.Recall.Add((double)hitsAtK / goldArticles.Count);

                var rank = distinct.Take(TopK).ToList().FindIndex(goldArticles.Contains);
                metrics.ReciprocalRank.Add(rank < 0 ? 0 : 1.0 / (rank + 1));

                var anchored = foundJudgments.Take(TopK)
                    .Count(j => LegalRetriever.ArticleAffinity(j, goldArticles) > 0);
                metrics.Affinity.Add(foundJudgments.Count == 0 ? 0 : (double)anchored / Math.Min(TopK, foundJudgments.Count));
            }

            results.Add((system.Label, metrics));
            Console.WriteLine($"{system.Id,-16} P@5={metrics.MeanPrecision:F3} R@10={metrics.MeanRecall:F3} " +
                              $"MRR@10={metrics.Mrr:F3} affinity@10={metrics.MeanAffinity:F3} " +
                              $"{metrics.MeanLatency:F1} ms/query");
        }

        var markdown = BuildReport(gold.Count, articles, judgments, missing, results);
        var full = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, markdown, new UTF8Encoding(false));
        Console.WriteLine();
        Console.WriteLine($"report written to {full}");
        return 0;
    }

    private static (List<string>, List<LegalDocument>) EngineRun(ILegalSearchEngine engine, GoldQuery query)
    {
        var articleHits = engine.Search(query.Query, query.CaseType, TopK, LegalDocKind.Article);
        var judgmentHits = engine.Search(query.Query, query.CaseType, TopK, LegalDocKind.Judgment);
        return (articleHits.Select(h => Key(h.Document.ArticleNumber)).ToList(),
                judgmentHits.Select(h => h.Document).ToList());
    }

    private static (List<string>, List<LegalDocument>) HybridRun(GoldQuery query, HashSet<string> curated)
    {
        var research = LegalRetriever.Research(query.CaseType, query.Query, null,
            maxArticles: TopK, maxJudgments: TopK, rounds: 2);
        _ = curated;
        return (research.Articles.Select(h => Key(h.Document.ArticleNumber)).ToList(),
                research.Judgments.Select(h => h.Document).ToList());
    }

    /// <summary>Formats a metric with a dot decimal separator, whatever the machine locale is.</summary>
    private static string N(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

    private static string Key(string? articleNumber) =>
        (articleNumber ?? string.Empty).Replace(" ", string.Empty).Replace("\u2013", "-").ToLowerInvariant();

    private static string BuildReport(int queryCount, int articles, int judgments, List<string> missing,
        List<(string Label, Metrics Metrics)> results)
    {
        var best = results.OrderByDescending(r => r.Metrics.Mrr).ThenByDescending(r => r.Metrics.MeanPrecision).First();
        var builder = new StringBuilder();
        builder.AppendLine("# Retrieval benchmark");
        builder.AppendLine();
        builder.AppendLine($"Corpus: **{articles.ToString("N0", CultureInfo.InvariantCulture)} articles**, **{judgments.ToString("N0", CultureInfo.InvariantCulture)} judgments** (Italian civil and criminal sources).");
        builder.AppendLine($"Gold set: **{queryCount} queries** ({GoldSet.Queries.Count(q => q.CaseType == CaseType.Criminal)} criminal, " +
                           $"{GoldSet.Queries.Count(q => q.CaseType == CaseType.Civil)} civil), hand-labelled with the articles a lawyer opens for that fact pattern.");
        if (missing.Count > 0)
            builder.AppendLine($"Articles of the gold set that the archive does not contain were dropped: {string.Join(", ", missing)}.");
        builder.AppendLine();
        builder.AppendLine("| Engine | P@5 | R@10 | MRR@10 | Case-law affinity@10 | Latency |");
        builder.AppendLine("|---|---|---|---|---|---|");
        foreach (var (label, metrics) in results.OrderByDescending(r => r.Metrics.Mrr))
            builder.AppendLine($"| {label} | {N(metrics.MeanPrecision)} | {N(metrics.MeanRecall)} | {N(metrics.Mrr)} | " +
                               $"{N(metrics.MeanAffinity)} | {metrics.MeanLatency.ToString("F1", CultureInfo.InvariantCulture)} ms |");
        builder.AppendLine();
        builder.AppendLine("- **P@5** — share of the first five articles that are relevant.");
        builder.AppendLine("- **R@10** — share of the relevant articles found in the first ten.");
        builder.AppendLine("- **MRR@10** — mean reciprocal rank of the first relevant article.");
        builder.AppendLine("- **Case-law affinity@10** — share of the first ten judgments that cite at least one relevant article of the case: it measures whether the retrieved case law is anchored to the right norms, not just topically similar.");
        builder.AppendLine("- **Latency** — mean per query on the machine that produced this report.");
        builder.AppendLine();
        builder.AppendLine($"Winner: **{best.Label}**.");
        return builder.ToString();
    }
}
