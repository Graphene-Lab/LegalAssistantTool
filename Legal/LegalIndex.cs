using System.Text;
using Microsoft.Data.Sqlite;

namespace AIOrchestrator.API.Legal;

/// <summary>A ranked search hit.</summary>
/// <param name="Document">The matched document.</param>
/// <param name="Score">Higher is better.</param>
internal readonly record struct LegalHit(LegalDocument Document, double Score);

/// <summary>
/// SQLite FTS5 index over the normalised corpus, with BM25 ranking and the Italian fold+stem
/// applied identically to the indexed text and to the query. This is the engine the tool uses
/// by default: it needs no server, no native dependency beyond the bundled SQLite, and it is
/// fully cross-platform (see the benchmark table in README.md for the comparison that led here).
/// </summary>
internal static class LegalIndex
{
    /// <summary>Engine identifier persisted in the archive state.</summary>
    public const string EngineId = "sqlite-fts5-italian";

    private const string CreateTableSql =
        "CREATE VIRTUAL TABLE docs USING fts5(docid UNINDEXED, kind UNINDEXED, branch UNINDEXED, court UNINDEXED, " +
        "heading, body, citation, tokenize = 'unicode61 remove_diacritics 2');";

    private const string RankExpression = "bm25(docs, 0.0, 0.0, 0.0, 0.0, 8.0, 1.0, 4.0)";

    /// <summary>Rebuilds the index from scratch.</summary>
    /// <param name="documents">Corpus to index.</param>
    public static void Build(IReadOnlyList<LegalDocument> documents)
    {
        LegalNative.EnsureSqlite();
        LegalStorage.EnsureDirectories();
        var path = LegalStorage.SqlitePath;
        var temp = path + ".building";
        foreach (var candidate in new[] { temp, temp + "-wal", temp + "-shm" })
            if (File.Exists(candidate)) File.Delete(candidate);

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = temp,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString()))
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=MEMORY;");
            Execute(connection, "PRAGMA synchronous=OFF;");
            Execute(connection, CreateTableSql);

            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO docs(docid, kind, branch, court, heading, body, citation) " +
                                  "VALUES ($id, $kind, $branch, $court, $heading, $body, $citation);";
            var id = command.Parameters.Add("$id", SqliteType.Text);
            var kind = command.Parameters.Add("$kind", SqliteType.Text);
            var court = command.Parameters.Add("$court", SqliteType.Text);
            var branch = command.Parameters.Add("$branch", SqliteType.Text);
            var heading = command.Parameters.Add("$heading", SqliteType.Text);
            var body = command.Parameters.Add("$body", SqliteType.Text);
            var citation = command.Parameters.Add("$citation", SqliteType.Text);

            var indexed = 0;
            foreach (var document in documents)
            {
                id.Value = document.Id;
                kind.Value = document.Kind == LegalDocKind.Article ? "article" : "judgment";
                branch.Value = document.CaseType switch
                {
                    Legal.CaseType.Criminal => "criminal",
                    Legal.CaseType.Civil => "civil",
                    _ => "any",
                };
                court.Value = LegalRetriever.CourtFamily(document);
                heading.Value = ItalianText.ToSearchText(document.Heading + " " + document.Matter);
                body.Value = ItalianText.ToSearchText(document.Body);
                citation.Value = ItalianText.ToSearchText(document.Citation);
                command.ExecuteNonQuery();
                indexed++;
            }

            transaction.Commit();
            Execute(connection, "INSERT INTO docs(docs) VALUES('optimize');");
            Log.LogStep($"FTS5 index built: {indexed} documents at {temp}");
        }

        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
        Log.LogStep($"FTS5 index published: {path}");
    }

    /// <summary>Runs a BM25 query.</summary>
    /// <param name="query">Free text query.</param>
    /// <param name="caseType">Branch of law to keep.</param>
    /// <param name="limit">Maximum number of hits.</param>
    /// <param name="kind">Document kind to keep, or null for both.</param>
    /// <param name="court">Court filter, or null for every court.</param>
    /// <returns>Hits, best first.</returns>
    public static List<LegalHit> Search(string query, CaseType caseType, int limit, LegalDocKind? kind = null, string? court = null)
    {
        LegalNative.EnsureSqlite();
        var hits = new List<LegalHit>();
        var expression = BuildMatchExpression(query);
        if (expression.Length == 0) return hits;
        if (!File.Exists(LegalStorage.SqlitePath)) return hits;

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = LegalStorage.SqlitePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = true,
            }.ToString());
            connection.Open();

            using var command = connection.CreateCommand();
            var branchFilter = caseType == Legal.CaseType.Criminal ? "criminal" : "civil";
            var kindFilter = kind switch
            {
                LegalDocKind.Article => "AND kind = 'article'",
                LegalDocKind.Judgment => "AND kind = 'judgment'",
                _ => string.Empty,
            };
            var courtFilter = court == null ? string.Empty : "AND court = $c";
            command.CommandText =
                $"SELECT docid, {RankExpression} AS rank FROM docs " +
                $"WHERE docs MATCH $q {kindFilter} {courtFilter} AND (branch = $b OR branch = 'any') ORDER BY rank LIMIT $n;";
            command.Parameters.AddWithValue("$q", expression);
            command.Parameters.AddWithValue("$b", branchFilter);
            if (court != null) command.Parameters.AddWithValue("$c", court);
            command.Parameters.AddWithValue("$n", limit * 4 + 20);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var documentId = reader.GetString(0);
                var score = -reader.GetDouble(1);
                var document = LegalCorpus.ById(documentId);
                if (document == null) continue;
                if (kind is { } required && document.Kind != required) continue;
                if (!document.ServesCase(caseType)) continue;
                hits.Add(new LegalHit(document, score));
                if (hits.Count >= limit) break;
            }
        }
        catch (SqliteException ex)
        {
            Log.LogStep($"FTS5 query failed: {ex.Message} (query: {expression})");
        }

        return hits;
    }

    /// <summary>Number of indexed documents, or 0 when the index is missing.</summary>
    /// <returns>Document count.</returns>
    public static long Count()
    {
        LegalNative.EnsureSqlite();
        if (!File.Exists(LegalStorage.SqlitePath)) return 0;
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = LegalStorage.SqlitePath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM docs;";
            return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    /// <summary>Builds a safe FTS5 MATCH expression from free text.</summary>
    /// <param name="query">Free text query.</param>
    /// <returns>The expression, or an empty string when there is nothing to search.</returns>
    internal static string BuildMatchExpression(string query)
    {
        var terms = ItalianText.Tokenize(query);
        if (terms.Count == 0) return string.Empty;
        var unique = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in terms)
            if (seen.Add(term)) unique.Add(term);

        var builder = new StringBuilder(unique.Count * 12);
        foreach (var term in unique)
        {
            var safe = term.Replace("\"", string.Empty);
            if (safe.Length == 0) continue;
            if (builder.Length > 0) builder.Append(" OR ");
            builder.Append('"').Append(safe).Append('"');
        }

        return builder.ToString();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}