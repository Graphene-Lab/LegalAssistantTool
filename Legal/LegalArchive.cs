using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace AIOrchestrator.API.Legal;

/// <summary>State of the local legal archive, as seen by a tool call.</summary>
internal enum ArchiveStatus
{
    /// <summary>Corpus and index are on disk and queryable.</summary>
    Ready,

    /// <summary>A synchronisation is running right now.</summary>
    Preparing,

    /// <summary>No usable archive and the synchronisation failed.</summary>
    Unavailable,
}

/// <summary>Outcome of an archive readiness check.</summary>
/// <param name="Status">Readiness status.</param>
/// <param name="Message">Human readable detail, safe to show to the agent.</param>
internal readonly record struct ArchiveReadiness(ArchiveStatus Status, string? Message);

/// <summary>
/// Keeps the two source repositories in sync with a local copy, rebuilds the normalised corpus
/// and the search index whenever a repository actually changed, and schedules the refresh at
/// night in the user's local time. Every network failure degrades silently: a missing or
/// unreachable GitHub leaves the last good archive in place.
/// </summary>
internal static class LegalArchive
{
    /// <summary>Normalisation format version; bumping it forces a corpus and index rebuild.</summary>
    public const int FormatVersion = 6;

    /// <summary>Local hour (0-23) of the nightly refresh.</summary>
    public const int NightlyHour = 3;

    /// <summary>Local minute of the nightly refresh.</summary>
    public const int NightlyMinute = 30;

    /// <summary>Repository archive of the Italian codes (our own project).</summary>
    public const string LeggiZipUrl = "https://codeload.github.com/Andrea-Bruno/leggi-italiane/zip/refs/heads/master";

    /// <summary>Repository archive of the case-law database (third party: schede CC BY 4.0, Consulta open data CC BY-SA 3.0).</summary>
    public const string GiuriZipUrl = "https://codeload.github.com/Synthos-Logic/giurisprudenza-db/zip/refs/heads/main";

    /// <summary>
    /// Environment variable that also builds the Lucene indexes during a refresh. They are not
    /// needed to answer: the tool searches the SQLite FTS5 index, and the benchmark builds the
    /// Lucene indexes on demand. Off by default, so a refresh writes only what the tool uses.
    /// </summary>
    public const string LuceneVariable = "LEGALASSISTANTTOOL_LUCENE";

    /// <summary>True when a refresh should also build the Lucene indexes.</summary>
    public static bool LuceneIndexEnabled { get; } =
        Environment.GetEnvironmentVariable(LuceneVariable) is { Length: > 0 } value
        && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
    private const string LeggiCommitApi = "https://api.github.com/repos/Andrea-Bruno/leggi-italiane/commits/master";
    private const string GiuriCommitApi = "https://api.github.com/repos/Synthos-Logic/giurisprudenza-db/commits/main";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Http = CreateHttp();
    private static readonly object NightlyLock = new();

    private static Task? _nightly;
    private static Task<ArchiveReadiness>? _sync;
    private static volatile string _progress = "idle";
    private static bool _indexLoaded;

    /// <summary>Current step of the synchronisation, for progress reporting.</summary>
    public static string Progress => _progress;

    /// <summary>
    /// Makes sure a usable archive exists, waiting at most <paramref name="maxWait"/> for a
    /// synchronisation that is already running or that this call starts.
    /// </summary>
    /// <param name="maxWait">How long the caller is willing to block.</param>
    /// <returns>Readiness of the archive.</returns>
    public static ArchiveReadiness EnsureReady(TimeSpan maxWait)
    {
        StartNightly();

        var state = LegalStorage.ReadState();
        if (IsUsable(state))
        {
            _indexLoaded = true;
            StartBackgroundRefreshIfStale(state);
            return new ArchiveReadiness(ArchiveStatus.Ready, null);
        }

        var task = StartSync(force: false);
        try
        {
            if (!task.Wait(maxWait))
                return new ArchiveReadiness(ArchiveStatus.Preparing, DescribeProgress());
        }
        catch (AggregateException)
        {
            // Reported through the task result below.
        }

        state = LegalStorage.ReadState();
        if (IsUsable(state))
        {
            _indexLoaded = true;
            return new ArchiveReadiness(ArchiveStatus.Ready, null);
        }

        return new ArchiveReadiness(ArchiveStatus.Unavailable, DescribeProgress());
    }

    /// <summary>True when the corpus and the index on disk match the current code version.</summary>
    /// <param name="state">State read from disk.</param>
    /// <returns>True when queries can be served.</returns>
    public static bool IsUsable(ArchiveState state) =>
        state.IndexBuiltUtc != null
        && !state.NeedsRebuild(FormatVersion)
        && state.ArticleCount > 0
        && File.Exists(LegalStorage.SqlitePath);

    /// <summary>Starts (or joins) a synchronisation.</summary>
    /// <param name="force">True to re-download even when the remote commit is unchanged.</param>
    /// <returns>The synchronisation task.</returns>
    public static Task<ArchiveReadiness> StartSync(bool force)
    {
        lock (NightlyLock)
        {
            if (_sync is { IsCompleted: false }) return _sync;
            _sync = Task.Run(() => SyncCore(force));
            return _sync;
        }
    }

    /// <summary>Runs a full synchronisation and waits for it (used by tests and benchmarks).</summary>
    /// <param name="force">True to re-download even when the remote commit is unchanged.</param>
    /// <returns>Readiness after the run.</returns>
    public static ArchiveReadiness SyncNow(bool force = true) => StartSync(force).GetAwaiter().GetResult();

    /// <summary>Local instant of the next nightly refresh.</summary>
    /// <param name="now">Current local time.</param>
    /// <returns>The next scheduled instant.</returns>
    public static DateTime NextNightlyRun(DateTime now)
    {
        var today = now.Date.AddHours(NightlyHour).AddMinutes(NightlyMinute);
        return today > now ? today : today.AddDays(1);
    }

    private static void StartNightly()
    {
        lock (NightlyLock)
        {
            if (_nightly is { IsCompleted: false }) return;
            _nightly = Task.Run(NightlyLoop);
        }
    }

    private static async Task NightlyLoop()
    {
        while (true)
        {
            var delay = NextNightlyRun(DateTime.Now) - DateTime.Now;
            if (delay < TimeSpan.FromMinutes(1)) delay = TimeSpan.FromMinutes(1);
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            Log.LogStep($"Nightly refresh starting (scheduled {NightlyHour:00}:{NightlyMinute:00} local).");
            try
            {
                var result = await StartSync(force: false).ConfigureAwait(false);
                Log.LogStep($"Nightly refresh finished: {result.Status}.");
            }
            catch (Exception ex)
            {
                Log.LogStep($"Nightly refresh failed silently: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void StartBackgroundRefreshIfStale(ArchiveState state)
    {
        var last = state.LastCheckUtc ?? DateTime.MinValue;
        if (DateTime.UtcNow - last < TimeSpan.FromHours(12)) return;
        _ = StartSync(force: false);
    }

    private static async Task<ArchiveReadiness> SyncCore(bool force)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            LegalStorage.EnsureDirectories();
            var state = LegalStorage.ReadState();
            state.LastCheckUtc = DateTime.UtcNow;
            LegalStorage.WriteState(state);

            Log.LogStep($"Archive sync start: force={force} root={LegalStorage.Root} " +
                $"leggi={state.LeggiCommit ?? "none"} giuri={state.GiuriCommit ?? "none"}");

            var changed = false;

            _progress = "checking leggi-italiane";
            var leggiChanged = force || await IsRemoteChanged(LeggiCommitApi, state.LeggiCommit).ConfigureAwait(false);
            if (leggiChanged || !Directory.Exists(Path.Combine(LegalStorage.RawDirectory, "leggi")))
            {
                _progress = "downloading leggi-italiane";
                var commit = await DownloadAndExtract(LeggiZipUrl, "leggi").ConfigureAwait(false);
                if (commit != null)
                {
                    state.LeggiCommit = commit;
                    changed = true;
                }
            }

            _progress = "checking giurisprudenza-db";
            var giuriChanged = force || await IsRemoteChanged(GiuriCommitApi, state.GiuriCommit).ConfigureAwait(false);
            if (giuriChanged || !Directory.Exists(Path.Combine(LegalStorage.RawDirectory, "giuri")))
            {
                _progress = "downloading giurisprudenza-db";
                var commit = await DownloadAndExtract(GiuriZipUrl, "giuri").ConfigureAwait(false);
                if (commit != null)
                {
                    state.GiuriCommit = commit;
                    changed = true;
                }
            }

            var leggiRoot = Path.Combine(LegalStorage.RawDirectory, "leggi");
            var giuriRoot = Path.Combine(LegalStorage.RawDirectory, "giuri");
            var haveSources = Directory.Exists(leggiRoot) && Directory.Exists(giuriRoot);

            if (!haveSources)
            {
                _progress = "no sources available";
                Log.LogStep("Archive sync aborted: sources missing after download attempt.");
                return new ArchiveReadiness(ArchiveStatus.Unavailable,
                    "the source repositories are not available locally and could not be downloaded");
            }

            if (changed || state.NeedsRebuild(FormatVersion))
            {
                _progress = "normalising sources";
                var counts = LegalNormalizer.NormalizeAll(leggiRoot, giuriRoot);
                state.ArticleCount = counts.Articles;
                state.JudgmentCount = counts.Judgments;
                state.FormatVersion = FormatVersion;

                _progress = "building index";
                var corpus = LegalCorpus.Load(forceReload: true);
                LegalIndex.Build(corpus);
                if (LuceneIndexEnabled) LegalLucene.Build(LegalLucene.DefaultEngine, corpus);

                state.IndexBuiltUtc = DateTime.UtcNow;
                state.IndexEngine = LegalIndex.EngineId;
                _indexLoaded = true;
                Log.LogStep($"Archive rebuilt: articles={state.ArticleCount} judgments={state.JudgmentCount} " +
                    $"engine={state.IndexEngine}");
            }
            else
            {
                _progress = "up to date";
                Log.LogStep("Archive sync: no remote change, index reused.");
            }

            state.LastSyncUtc = DateTime.UtcNow;
            LegalStorage.WriteState(state);
            _progress = "ready";
            return new ArchiveReadiness(ArchiveStatus.Ready, null);
        }
        catch (Exception ex)
        {
            _progress = "failed: " + ex.Message;
            Log.LogStep($"Archive sync failed: {ex.GetType().Name}: {ex.Message}");
            return new ArchiveReadiness(ArchiveStatus.Unavailable, ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<bool> IsRemoteChanged(string apiUrl, string? localSha)
    {
        if (string.IsNullOrEmpty(localSha)) return true;
        try
        {
            using var response = await Http.GetAsync(apiUrl).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.LogStep($"Commit probe {apiUrl} answered {(int)response.StatusCode}; assuming unchanged.");
                return false;
            }

            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var document = await System.Text.Json.JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("sha", out var sha)) return true;
            var remote = sha.GetString();
            var changed = !string.Equals(remote, localSha, StringComparison.OrdinalIgnoreCase);
            Log.LogStep($"Commit probe {apiUrl}: remote={remote} local={localSha} changed={changed}");
            return changed;
        }
        catch (Exception ex)
        {
            Log.LogStep($"Commit probe {apiUrl} failed ({ex.GetType().Name}); assuming unchanged.");
            return false;
        }
    }

    private static async Task<string?> DownloadAndExtract(string zipUrl, string name)
    {
        var tempZip = Path.Combine(Path.GetTempPath(), $"legalassistanttool-{name}-{Guid.NewGuid():N}.zip");
        var tempDir = Path.Combine(Path.GetTempPath(), $"legalassistanttool-{name}-{Guid.NewGuid():N}");
        try
        {
            Log.LogStep($"Downloading {zipUrl}");
            using (var response = await Http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var target = File.Create(tempZip);
                await response.Content.CopyToAsync(target).ConfigureAwait(false);
            }

            var size = new FileInfo(tempZip).Length;
            Log.LogStep($"Downloaded {name} zip: {size / 1024 / 1024} MB");

            Directory.CreateDirectory(tempDir);
            ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            var roots = Directory.GetDirectories(tempDir);
            if (roots.Length != 1) throw new InvalidOperationException($"unexpected zip layout for {name}");
            var extracted = roots[0];

            var destination = Path.Combine(LegalStorage.RawDirectory, name);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            Directory.CreateDirectory(LegalStorage.RawDirectory);
            Directory.Move(extracted, destination);

            var commit = Path.GetFileName(extracted);
            Log.LogStep($"Extracted {name} to {destination} (archive root {commit})");
            return commit;
        }
        catch (Exception ex)
        {
            Log.LogStep($"Download of {name} failed silently: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            TryDelete(tempZip);
            TryDeleteDirectory(tempDir);
        }
    }

    private static string DescribeProgress() =>
        $"the local legal archive is being prepared (step: {_progress}); call the tool again in a few minutes";

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Graphene-LegalAssistantTool/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    /// <summary>SHA-256 of a file, lowercase hex.</summary>
    /// <param name="path">File to hash.</param>
    /// <returns>Hex digest.</returns>
    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>True when the corpus was already loaded in this process.</summary>
    public static bool IndexLoaded => _indexLoaded;
}