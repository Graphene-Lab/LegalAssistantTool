using System.Diagnostics;
using System.Text;
using AIOrchestrator;
using AIOrchestrator.API;
using AIOrchestrator.API.Legal;

namespace LegalAssistantToolHarness;

/// <summary>
/// End-to-end harness: it drives the tool exactly as the agent does, keeps every log line on disk
/// and checks each answer against the archive (no invented article, no invented judgment).
/// </summary>
internal static class Program
{
    private static readonly List<string> Failures = [];
    private static string _outDir = string.Empty;
    private static bool _echo;

    private static int Main(string[] args)
    {
        var root = FindProjectRoot();
        var dataDir = Path.Combine(root, ".data");
        Directory.CreateDirectory(dataDir);
        Environment.SetEnvironmentVariable("LEGALASSISTANTTOOL_DATA", dataDir);
        _outDir = Path.Combine(root, ".out");
        Directory.CreateDirectory(_outDir);
        _echo = args.Contains("--echo");

        Log.IsEnabled = true;
        if (_echo) Log.OnLogStepMonitor += (line, monitor) => Console.WriteLine((monitor ? ">> " : "   ") + line);

        Console.WriteLine($"Data:   {dataDir}");
        Console.WriteLine($"Output: {_outDir}");
        Console.WriteLine($"Log:    {Log.CurrentLogFile ?? "(created on the first step)"}");
        Console.WriteLine();

        var scenario = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "all";

        if (scenario is "sync" or "all") RunSync();
        if (scenario is "laws" or "all") RunLaws();
        if (scenario is "opinion" or "all") RunOpinions();
        if (scenario is "verify" or "all") VerifyAllOutputs();

        Console.WriteLine();
        if (Failures.Count == 0)
        {
            Console.WriteLine("RESULT: all checks passed.");
            return 0;
        }

        Console.WriteLine($"RESULT: {Failures.Count} failure(s).");
        foreach (var failure in Failures) Console.WriteLine(" - " + failure);
        return 1;
    }

    private static void RunSync()
    {
        Console.WriteLine("== synchronisation ==");
        var watch = Stopwatch.StartNew();
        var readiness = LegalArchive.SyncNow(force: false);
        watch.Stop();
        Console.WriteLine($"status={readiness.Status} ({readiness.Message}) in {watch.Elapsed.TotalSeconds:F1}s");
        Check(readiness.Status == ArchiveStatus.Ready, $"sync status is {readiness.Status}");

        var state = LegalStorage.ReadState();
        Console.WriteLine($"articles={state.ArticleCount} judgments={state.JudgmentCount} " +
                          $"index={state.IndexEngine} built={state.IndexBuiltUtc:u} leggi={state.LeggiCommit?[..7]} giuri={state.GiuriCommit?[..7]}");
        Check(state.ArticleCount > 3000, $"only {state.ArticleCount} articles normalised");
        Check(state.JudgmentCount > 200, $"only {state.JudgmentCount} judgments normalised");

        var corpus = LegalCorpus.Load();
        Console.WriteLine($"corpus in memory: {corpus.Count} documents");
        var articles = corpus.Count(d => d.Kind == LegalDocKind.Article);
        var judgments = corpus.Count(d => d.Kind == LegalDocKind.Judgment);
        Console.WriteLine($"  articles={articles} judgments={judgments} " +
                          $"citable={corpus.Count(d => d.IsCitable)}");
        Check(articles > 3000, $"corpus holds only {articles} articles");
        Check(judgments > 200, $"corpus holds only {judgments} judgments");

        var sample = corpus.FirstOrDefault(d => d.ArticleNumber == "624" && d.CodeId == "penale");
        Check(sample != null, "Art. 624 c.p. missing from the corpus");
        if (sample != null)
        {
            Console.WriteLine($"  Art. 624 c.p. -> {ItalianText.Clip(sample.Body, 120)}");
            Check(sample.Body.Contains("chiunque", StringComparison.OrdinalIgnoreCase),
                "Art. 624 c.p. body does not look like the real text");
        }

        Console.WriteLine();
    }

    private static void RunLaws()
    {
        Console.WriteLine("== GetLaws ==");
        var tool = new LegalAssistantTool();
        var cases = new (string Name, LegalJurisdiction Jurisdiction, CaseType Branch, string Topic)[]
        {
            ("laws-furto", LegalJurisdiction.IT, CaseType.Criminal, "furto aggravato"),
            ("laws-rapina", LegalJurisdiction.IT, CaseType.Criminal, "rapina e estorsione"),
            ("laws-truffa", LegalJurisdiction.IT, CaseType.Criminal, "truffa online e frode informatica"),
            ("laws-locazione", LegalJurisdiction.IT, CaseType.Civil, "inadempimento del contratto di locazione e sfratto"),
            ("laws-danno", LegalJurisdiction.IT, CaseType.Civil, "risarcimento del danno da sinistro stradale"),
            ("laws-lavoro", LegalJurisdiction.IT, CaseType.Civil, "licenziamento illegittimo e tutela del lavoratore"),
            ("laws-vuoto", LegalJurisdiction.IT, CaseType.Criminal, "reato di pirateria spaziale su Marte"),
        };

        foreach (var testCase in cases)
        {
            var watch = Stopwatch.StartNew();
            var result = tool.GetLaws(testCase.Jurisdiction, testCase.Branch, testCase.Topic);
            watch.Stop();
            Save(testCase.Name, result);
            var articles = CountOccurrences(result, "\n### Art. ");
            var judgments = CountOccurrences(result, "\n### Cass.,") + CountOccurrences(result, "\n### Corte cost.,");
            Console.WriteLine($"{testCase.Name,-16} {watch.ElapsedMilliseconds,6} ms  {result.Length,7} chars  " +
                              $"articles={articles} judgments={judgments}");
            Check(!result.StartsWith("Error:", StringComparison.Ordinal), $"{testCase.Name} returned an error: {ItalianText.Clip(result, 200)}");
            if (testCase.Name != "laws-vuoto")
            {
                Check(articles > 0, $"{testCase.Name} returned no articles");
                Check(result.Contains("**Fonte:**", StringComparison.Ordinal), $"{testCase.Name} returned no source link");
            }
        }

        Console.WriteLine();
    }

    private static void RunOpinions()
    {
        Console.WriteLine("== LegalOpinion ==");
        var tool = new LegalAssistantTool();
        var attachment = Path.Combine(_outDir, "verbale-carabinieri.txt");
        File.WriteAllText(attachment,
            "VERBALE DI DENUNCIA\n" +
            "Il giorno 12 marzo 2025, in Milano, la signora Rossi Anna dichiarava che il proprio autoveicolo, " +
            "parcheggiato in via Roma 15, era stato asportato. Il figlio riferiva di aver lasciato le chiavi " +
            "nel cruscotto e lo sportello non chiuso a chiave. Il veicolo è stato ritrovato il giorno successivo " +
            "in via Verdi, con il vetro posteriore infranto. Valore del veicolo: 12.000 euro.\n");

        var criminalFacts =
            "Il mio assistito, imputato in un procedimento penale davanti al Tribunale di Milano, è accusato di " +
            "furto aggravato ex art. 625 c.p. per aver asportato un autoveicolo del valore di 12.000 euro dal " +
            "parcheggio condominiale il 12 marzo 2025. Il veicolo era aperto e le chiavi erano nel cruscotto. " +
            "L'udienza è fissata per il 20 giugno 2025. Il mio assistito ha 24 anni e non ha precedenti penali. " +
            "La persona offesa ha presentato querela lo stesso giorno.";

        var civilFacts =
            "Il mio assistito è convenuto in un giudizio civile davanti al Tribunale di Roma: il locatore chiede " +
            "la risoluzione del contratto di locazione e il pagamento di 9.600 euro di canoni non corrisposti, " +
            "oltre agli interessi. Il contratto è stato stipulato il 1 febbraio 2022 con canone mensile di 800 euro. " +
            "Il conduttore ha sospeso i pagamenti da gennaio 2024 perché nell'appartamento si sono manifestate " +
            "infiltrazioni di umidità che hanno reso inagibile una stanza. L'udienza di prima comparizione è " +
            "fissata per il 15 settembre 2025.";

        var thinFacts = "Mio fratello ha litigato con il vicino.";

        var scenarios = new (string Name, CaseType Branch, string Facts, string[]? Attachments, string[]? Suggestions)[]
        {
            ("opinion-furto", CaseType.Criminal, criminalFacts, null,
                ["considera che le chiavi erano nel cruscotto e lo sportello non era chiuso: questo può cambiare tutto"]),
            ("opinion-locazione", CaseType.Civil, civilFacts, null,
                ["valuta se le infiltrazioni di umidità legittimano l'eccezione di inadempimento del locatore"]),
            ("opinion-allegato", CaseType.Criminal, criminalFacts, [attachment], null),
            ("opinion-insufficiente", CaseType.Civil, thinFacts, null, null),
            ("opinion-vuota", CaseType.Criminal, "   ", null, null),
        };

        foreach (var scenario in scenarios)
        {
            var watch = Stopwatch.StartNew();
            var result = tool.LegalOpinion(LegalJurisdiction.IT, scenario.Branch, scenario.Facts,
                scenario.Attachments, scenario.Suggestions);
            watch.Stop();
            Save(scenario.Name, result);
            Console.WriteLine($"{scenario.Name,-22} {watch.ElapsedMilliseconds,6} ms  {result.Length,7} chars  " +
                              $"model={(result.Contains("prosa assistita", StringComparison.Ordinal) ? "yes" : "no ")}  " +
                              $"removed={ExtractRemoved(result)}");
            Check(!result.StartsWith("Error:", StringComparison.Ordinal), $"{scenario.Name} returned an error: {ItalianText.Clip(result, 200)}");
            Check(result.Contains("# Parere legale", StringComparison.Ordinal) ||
                  result.Contains("# Dati insufficienti", StringComparison.Ordinal),
                $"{scenario.Name} returned neither an opinion nor the insufficient-input answer");
        }

        Check(File.ReadAllText(Path.Combine(_outDir, "opinion-insufficiente.md")).Contains("Dati insufficienti", StringComparison.Ordinal),
            "a one-line description was accepted as sufficient input");
        Check(File.ReadAllText(Path.Combine(_outDir, "opinion-vuota.md")).Contains("Dati insufficienti", StringComparison.Ordinal),
            "empty facts were accepted");

        Console.WriteLine();
    }

    private static void VerifyAllOutputs()
    {
        Console.WriteLine("== verification of the produced Markdown ==");
        var corpus = LegalCorpus.Load();
        var articleKeys = corpus.Where(d => d.Kind == LegalDocKind.Article && d.ArticleNumber != null)
            .Select(d => d.ArticleNumber!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var judgmentKeys = corpus.Where(d => d.Kind == LegalDocKind.Judgment)
            .Select(d => LegalCitationGuard.JudgmentKey(d.Citation))
            .Where(k => k != null).Select(k => k!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.GetFiles(_outDir, "*.md").OrderBy(f => f))
        {
            var text = StripExternalSources(File.ReadAllText(file));
            var name = Path.GetFileName(file);
            var badArticles = new List<string>();
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(text, @"Art\.\s*(\d{1,4})\s*(c\.p\.|c\.c\.)"))
            {
                var number = match.Groups[1].Value;
                if (!articleKeys.Contains(number)) badArticles.Add(match.Value);
            }

            var badJudgments = new List<string>();
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(text, @"n\.\s*(\d{1,6})\s*/\s*(\d{2,4})"))
            {
                var key = $"{int.Parse(match.Groups[1].Value)}/{NormalizeYear(match.Groups[2].Value)}";
                if (!judgmentKeys.Contains(key)) badJudgments.Add(match.Value);
            }

            var links = System.Text.RegularExpressions.Regex.Matches(text, @"\]\((https?://[^)]+)\)").Count;
            Console.WriteLine($"{name,-28} articles={CountOccurrences(text, "\n### Art. "),2} " +
                              $"judgments={CountOccurrences(text, "\n### Cass.,") + CountOccurrences(text, "\n### Corte cost.,"),2} " +
                              $"links={links,2} inventedArticles={badArticles.Count} inventedJudgments={badJudgments.Count}");

            Check(badArticles.Count == 0, $"{name} cites articles that are not in the archive: {string.Join(", ", badArticles)}");
            Check(badJudgments.Count == 0, $"{name} cites judgments that are not in the archive: {string.Join(", ", badJudgments)}");
            Check(!text.Contains("Art. 0", StringComparison.Ordinal), $"{name} contains an impossible article number");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Removes the external-sources section: those rows quote third-party page titles, which are not
    /// citations of this tool and are explicitly labelled as unverified.
    /// </summary>
    private static string StripExternalSources(string text)
    {
        const string marker = "## 8. Approfondimenti esterni valutati";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return text;
        var end = text.IndexOf("\n## ", start + marker.Length, StringComparison.Ordinal);
        return end < 0 ? text[..start] : text[..start] + text[(end + 1)..];
    }

    private static string NormalizeYear(string year)
    {
        var value = int.Parse(year);
        if (value < 100) value += value < 50 ? 2000 : 1900;
        return value.ToString();
    }

    private static string ExtractRemoved(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"rimossi (\d+) passaggi");
        return match.Success ? match.Groups[1].Value : "0";
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static void Save(string name, string content)
    {
        var path = Path.Combine(_outDir, name + ".md");
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static void Check(bool condition, string message)
    {
        if (condition) return;
        Failures.Add(message);
        Console.WriteLine("  FAIL: " + message);
    }

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LegalAssistantTool.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }
}