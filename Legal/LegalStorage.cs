using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIOrchestrator.API.Legal;

/// <summary>
/// Where the synchronised archive, the normalised corpus and the search indexes live.
/// Everything sits under the user's local app-data folder (never next to the host executable
/// and never inside the plugin folder, which is replaced on update); the environment variable
/// <c>LEGALASSISTANTTOOL_DATA</c> redirects the whole tree for tests and benchmarks.
/// </summary>
internal static class LegalStorage
{
    /// <summary>Environment variable that overrides the data root.</summary>
    public const string DataRootVariable = "LEGALASSISTANTTOOL_DATA";

    private static readonly JsonSerializerOptions StateOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Absolute path of the data root.</summary>
    public static string Root { get; } = ResolveRoot();

    /// <summary>Folder holding the two extracted repositories.</summary>
    public static string RawDirectory => Path.Combine(Root, "raw");

    /// <summary>Folder holding the normalised JSONL corpus.</summary>
    public static string NormalizedDirectory => Path.Combine(Root, "normalized");

    /// <summary>Folder holding the search indexes.</summary>
    public static string IndexDirectory => Path.Combine(Root, "index");

    /// <summary>File recording the last synchronisation and the index state.</summary>
    public static string StatePath => Path.Combine(Root, "state.json");

    /// <summary>SQLite database with the FTS5 index.</summary>
    public static string SqlitePath => Path.Combine(IndexDirectory, "legal.db");

    /// <summary>Normalised articles, one JSON object per line.</summary>
    public static string ArticlesJsonl => Path.Combine(NormalizedDirectory, "articles.jsonl");

    /// <summary>Normalised judgments, one JSON object per line.</summary>
    public static string JudgmentsJsonl => Path.Combine(NormalizedDirectory, "judgments.jsonl");

    /// <summary>Lucene index folder for the given engine id.</summary>
    /// <param name="engine">Engine id (for example <c>lucene-standard</c>).</param>
    /// <returns>Absolute folder path.</returns>
    public static string LuceneDirectory(string engine) => Path.Combine(IndexDirectory, "lucene-" + engine);

    /// <summary>Creates the folder tree.</summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(RawDirectory);
        Directory.CreateDirectory(NormalizedDirectory);
        Directory.CreateDirectory(IndexDirectory);
    }

    /// <summary>Reads the persisted state, or a fresh one when the file is missing or unreadable.</summary>
    /// <returns>The archive state.</returns>
    public static ArchiveState ReadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return new ArchiveState();
            var json = File.ReadAllText(StatePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<ArchiveState>(json, StateOptions) ?? new ArchiveState();
        }
        catch (Exception)
        {
            return new ArchiveState();
        }
    }

    /// <summary>Persists the archive state.</summary>
    /// <param name="state">State to persist.</param>
    public static void WriteState(ArchiveState state)
    {
        EnsureDirectories();
        var temp = StatePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, StateOptions), new UTF8Encoding(false));
        File.Move(temp, StatePath, overwrite: true);
    }

    private static string ResolveRoot()
    {
        var custom = Environment.GetEnvironmentVariable(DataRootVariable);
        if (!string.IsNullOrWhiteSpace(custom)) return Path.GetFullPath(custom);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(appData, "Graphene", "LegalAssistantTool");
    }
}

/// <summary>
/// Persisted synchronisation and index state: which commit each repository is at, when it was
/// last checked, and how big the resulting corpus and index are.
/// </summary>
internal sealed class ArchiveState
{
    /// <summary>UTC instant of the last successful synchronisation (null = never).</summary>
    public DateTime? LastSyncUtc { get; set; }

    /// <summary>UTC instant of the last check, successful or not.</summary>
    public DateTime? LastCheckUtc { get; set; }

    /// <summary>Commit sha of <c>leggi-italiane</c> that is on disk.</summary>
    public string? LeggiCommit { get; set; }

    /// <summary>Commit sha of <c>giurisprudenza-db</c> that is on disk.</summary>
    public string? GiuriCommit { get; set; }

    /// <summary>Number of normalised articles.</summary>
    public int ArticleCount { get; set; }

    /// <summary>Number of normalised judgments.</summary>
    public int JudgmentCount { get; set; }

    /// <summary>Engine id that produced the current index.</summary>
    public string? IndexEngine { get; set; }

    /// <summary>UTC instant the current index was built.</summary>
    public DateTime? IndexBuiltUtc { get; set; }

    /// <summary>Version stamp of the normalisation format, so a schema change forces a rebuild.</summary>
    public int FormatVersion { get; set; }

    /// <summary>True when the corpus on disk was produced by the current format version.</summary>
    /// <param name="version">Format version expected by the running code.</param>
    /// <returns>True when a rebuild is needed.</returns>
    public bool NeedsRebuild(int version) =>
        FormatVersion != version || !File.Exists(LegalStorage.ArticlesJsonl) || !File.Exists(LegalStorage.JudgmentsJsonl);
}