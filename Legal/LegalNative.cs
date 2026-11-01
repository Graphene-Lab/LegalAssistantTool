using System.Reflection;
using System.Runtime.InteropServices;

namespace AIOrchestrator.API.Legal;

/// <summary>
/// Locates the native SQLite library when the plugin runs from a folder that is not the
/// process base directory (the host loads it from <c>Tools\LegalAssistantTool\</c>, and the
/// default DllImport probing never looks there). The managed side of SQLitePCLRaw asks the
/// OS loader for "e_sqlite3"; once that module is already loaded from an absolute path the
/// request resolves to it, so a single explicit preload is enough. When the library is
/// already reachable (NuGet consumer, test harness, app base) nothing happens.
/// </summary>
internal static class LegalNative
{
    private const string LibraryName = "e_sqlite3";

    private static readonly object Gate = new();
    private static bool _done;

    /// <summary>Makes the native SQLite library loadable, once per process.</summary>
    public static void EnsureSqlite()
    {
        if (_done) return;
        lock (Gate)
        {
            if (_done) return;
            _done = true;
            try
            {
                if (NativeLibrary.TryLoad(LibraryName, out _)) return;
            }
            catch
            {
                // Probing failed: fall through to the explicit candidates.
            }

            foreach (var candidate in Candidates())
            {
                if (!File.Exists(candidate)) continue;
                try
                {
                    NativeLibrary.Load(candidate);
                    Log.LogStep($"Native SQLite loaded from {candidate}");
                    return;
                }
                catch (Exception ex)
                {
                    Log.LogStep($"Native SQLite candidate {candidate} failed: {ex.GetType().Name}");
                }
            }

            Log.LogStep("Native SQLite not found next to the plugin: relying on the host probe paths.");
        }
    }

    private static IEnumerable<string> Candidates()
    {
        var pluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var names = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { LibraryName + ".dll" }
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? new[] { "lib" + LibraryName + ".dylib" }
                : new[] { "lib" + LibraryName + ".so" };

        var roots = new List<string>();
        if (!string.IsNullOrEmpty(pluginDirectory)) roots.Add(pluginDirectory);
        roots.Add(AppContext.BaseDirectory);

        foreach (var root in roots)
        {
            foreach (var name in names)
            {
                yield return Path.Combine(root, name);
                yield return Path.Combine(root, "runtimes", RuntimeIdentifier(), "native", name);
            }
        }
    }

    private static string RuntimeIdentifier()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => "x64",
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "win-" + architecture;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "osx-" + architecture;
        return "linux-" + architecture;
    }
}
