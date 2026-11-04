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
        UseProviderFromEnvironment();
        Console.WriteLine();

        var scenario = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "all";

        if (scenario is "sync" or "all") RunSync();
        if (scenario is "laws" or "all") RunLaws();
        if (scenario is "opinion" or "all") RunOpinions();
        if (scenario is "forms" or "all") RunForms();
        if (scenario is "describe" or "all") RunDescribe();
        if (scenario is "guide" or "all") RunGuide();
        if (scenario is "cache" or "all") RunCache();
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

    /// <summary>
    /// Runs the harness against the provider named by <c>LEGALASSISTANTTOOL_TEST_LLM</c> instead of the
    /// first entry of <c>providers.json</c>, so a model can be exercised without editing the host's file.
    /// <c>LLMUtility</c> resolves its provider from <see cref="ProviderConfigs.Default"/>, so the named
    /// one is marked default in memory only.
    /// </summary>
    private static void UseProviderFromEnvironment()
    {
        var name = Environment.GetEnvironmentVariable("LEGALASSISTANTTOOL_TEST_LLM");
        if (string.IsNullOrWhiteSpace(name)) return;
        // The key of a provider that needs one comes from the environment, never from the committed file.
        var key = Environment.GetEnvironmentVariable("LEGALASSISTANTTOOL_TEST_KEY");
        if (!string.IsNullOrWhiteSpace(key))
            ProviderConfigs.SetApiKey(name.Trim(), key.Trim(), persist: false);
        var config = ProviderConfigs.Get(name.Trim());
        ProviderConfigs.SetDefault(config.ProviderName, persist: false);
        Setup.ProviderConfig = config;
        Console.WriteLine($"Model:  {config.ProviderName} / {config.ModelName} @ {config.BaseAddress}");
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

    private static void RunForms()
    {
        Console.WriteLine("== GetLegalForms ==");
        var tool = new LegalAssistantTool();

        // Deterministic parts first: no network, no model.
        Check(LegalTemplates.CountryDomain(LegalJurisdiction.IT) == "it", "IT does not map to the .it domain");
        var cacheName = Path.GetFileName(LegalTemplates.CachePath("Atto notarile", "civile, notaio", "it"));
        Check(cacheName == "atto_notarile.civile_notaio.it.md", $"cache file name is '{cacheName}', expected 'atto_notarile.civile_notaio.it.md'");
        Check(LegalTemplates.OfflineText.Contains("internet connection", StringComparison.Ordinal),
            "the offline answer does not mention the missing internet connection");
        Check(LegalTemplates.NoTemplateText.Contains("No template is available", StringComparison.Ordinal),
            "the no-template answer is not self-explanatory");
        var empty = tool.GetLegalForms(LegalJurisdiction.IT, "  ", "civile");
        Check(empty.StartsWith("Error:", StringComparison.Ordinal), $"an empty document name was accepted: {ItalianText.Clip(empty, 120)}");
        var noScope = tool.GetLegalForms(LegalJurisdiction.IT, "ricorso in Cassazione", "  ");
        Check(noScope.StartsWith("Error:", StringComparison.Ordinal), $"an empty scope was accepted: {ItalianText.Clip(noScope, 120)}");

        // The local dictionary is a suspicion list for the model, never a verdict.
        if (SpellCheck.Available("it"))
        {
            var suspect = SpellCheck.UnknownWords("il codice fiscal e la sentenza Vogila constituted", "it");
            Console.WriteLine($"  dizionario: {Path.GetFileName(SpellCheck.DictionaryPath("it"))} · sospette: {string.Join(", ", suspect)}");
            Check(suspect.Contains("fiscal", StringComparer.OrdinalIgnoreCase), "the dictionary did not flag 'fiscal'");
            Check(!suspect.Contains("sentenza", StringComparer.OrdinalIgnoreCase), "the dictionary flagged a correct word");
        }
        else
        {
            Console.WriteLine("  dizionario: nessuno installato, controllo ortografico saltato");
        }

        // How the model's verdicts become a choice, without depending on a model being reachable.
        var offered = new List<LegalFormCandidate>
        {
            new("https://a/short.doc", "breve", new string('x', 500)),
            new("https://b/lungo.doc", "lungo", new string('x', 9000)),
            new("https://c/medio.doc", "medio", new string('x', 1500)),
        };
        Check(LegalTemplates.SelectBest(offered, [new() { IsRequested = true, Rating = 3 }, null, null])?.Url == "https://a/short.doc",
            "a requested document with a low score was not used: the rating must only rank");
        Check(LegalTemplates.SelectBest(offered, [new() { IsRequested = true, Rating = 2 }, null, new() { IsRequested = true, Rating = 9 }])?.Url == "https://c/medio.doc",
            "the highest rated requested document was not chosen");
        Check(LegalTemplates.SelectBest(offered, [new() { IsRequested = false, Rating = 10 }, new() { IsRequested = false, Rating = 9 }, null]) == null,
            "a document the model said is a different act was used");
        Check(LegalTemplates.SelectBest(offered, [new() { IsRequested = false, Rating = 10 }, new() { IsRequested = true, Rating = 1 }, null])?.Url == "https://b/lungo.doc",
            "identity did not win over a higher completeness score");
        Check(LegalTemplates.SelectBest(offered, [null, null, null])?.Url == "https://b/lungo.doc",
            "without any verdict the longest candidate was not used");
        Check(LegalTemplates.SelectBest([], [new() { IsRequested = true, Rating = 9 }]) == null, "an empty candidate list produced a choice");
        Check(LegalTemplates.RepairEncoding("dichiarazione di atto di notoriet\u00C3\u00A0 e nonch\u00C3\u00A9") ==
              "dichiarazione di atto di notorietà e nonché", "the mojibake repair did not restore the accented text");
        Check(LegalTemplates.RepairEncoding("nell\u00C2\u2019ambito e ci\u00C2\u2013 testuale") ==
              "nell’ambito e ci– testuale", "the mojibake repair did not restore the quotation marks");
        Check(LegalTemplates.RepairEncoding("Atto notarile redatto in lingua italiana") ==
              "Atto notarile redatto in lingua italiana", "the mojibake repair altered clean text");
        Check(LegalTemplates.Sanitize("D.P.R. \\(n. 445\\) e \\_\\_\\_ , \\_\\_\\_") ==
              "D.P.R. (n. 445) e ___ , ___", "the converter escapes were not removed from the template");

        // Real lookup: search, convert, choose, normalise, cache.
        var watch = Stopwatch.StartNew();
        var result = tool.GetLegalForms(LegalJurisdiction.IT, "ricorso in Cassazione", "civile, appello di Milano");
        watch.Stop();
        Save("forms-ricorso", result);
        var body = TemplateBody(result);
        Console.WriteLine($"forms-ricorso          {watch.ElapsedMilliseconds,6} ms  {result.Length,7} chars  " +
                          $"body={body.Length} placeholders={System.Text.RegularExpressions.Regex.Matches(body, @"\[[A-ZÀ-Ù][A-ZÀ-Ù_ ]+\]").Count}");
        Console.WriteLine("  head: " + ItalianText.Clip(ItalianText.CollapseWhitespace(body), 220));
        Check(!result.StartsWith("Error:", StringComparison.Ordinal), $"GetLegalForms returned an error: {ItalianText.Clip(result, 200)}");
        var offline = result.StartsWith(LegalTemplates.OfflineText, StringComparison.Ordinal);
        var missing = result.StartsWith(LegalTemplates.NoTemplateText, StringComparison.Ordinal);
        Check(offline || missing || result.Contains("```form", StringComparison.Ordinal),
            "GetLegalForms returned neither a template nor one of the two documented answers");
        if (!offline && !missing)
        {
            Check(body.Length > 400, $"the template body is only {body.Length} chars");
            Check(!body.Contains("```", StringComparison.Ordinal), "the template body would close its own fence");
            Check(!body.Contains('\u00C3'), "the template body contains double-encoded text");
            Check(body.Contains('[', StringComparison.Ordinal) ||
                  result.Contains("The form is returned as found", StringComparison.Ordinal),
                "the template has no placeholder and the caller was not told to add them");
            if (!result.Contains("The form is returned as found", StringComparison.Ordinal))
                Check(System.Text.RegularExpressions.Regex.Matches(body, @"\[[A-ZÀ-Ù][A-ZÀ-Ù_ ]+\]").Count >= 3,
                    "the normalised template marks fewer than three fields with a placeholder");

            var cacheFile = LegalTemplates.CachePath("ricorso in Cassazione", "civile, appello di Milano", "it");
            Check(File.Exists(cacheFile), $"the template was not cached at {Path.GetFileName(cacheFile)}");
            if (File.Exists(cacheFile))
            {
                var cached = File.ReadAllText(cacheFile).Trim();
                Check(cached == LegalTemplates.Sanitize(body), "the cached template differs from the one returned");
                var again = Stopwatch.StartNew();
                var second = tool.GetLegalForms(LegalJurisdiction.IT, "ricorso in Cassazione", "civile, appello di Milano");
                again.Stop();
                Console.WriteLine($"forms-ricorso-cache    {again.ElapsedMilliseconds,6} ms  {second.Length,7} chars");
                Check(again.ElapsedMilliseconds < 1500, $"the cache hit took {again.ElapsedMilliseconds} ms");
                Check(TemplateBody(second).Trim() == body.Trim(), "the cached answer differs from the first one");
            }
        }

        // A second document type, to check the search and the cache name on a different form.
        watch.Restart();
        var notarial = tool.GetLegalForms(LegalJurisdiction.IT, "atto notarile", "civile, notaio");
        watch.Stop();
        Save("forms-atto-notarile", notarial);
        var notarialBody = TemplateBody(notarial);
        Console.WriteLine($"forms-atto-notarile    {watch.ElapsedMilliseconds,6} ms  {notarial.Length,7} chars  body={notarialBody.Length}");
        Check(!notarial.StartsWith("Error:", StringComparison.Ordinal), $"atto notarile returned an error: {ItalianText.Clip(notarial, 200)}");
        Check(notarial.Contains("```form", StringComparison.Ordinal) ||
              notarial.StartsWith(LegalTemplates.NoTemplateText, StringComparison.Ordinal) ||
              notarial.StartsWith(LegalTemplates.OfflineText, StringComparison.Ordinal),
            "atto notarile returned neither a template nor a documented answer");
        if (notarial.Contains("```form", StringComparison.Ordinal))
        {
            var notarialCache = LegalTemplates.CachePath("atto notarile", "civile, notaio", "it");
            Check(File.Exists(notarialCache), "atto notarile was not cached as atto_notarile.it.md");
            if (File.Exists(notarialCache))
                Check(!File.ReadAllText(notarialCache).Contains('\u00C3'), "the atto notarile template contains double-encoded text");
        }

        // Nothing exists for this name: the answer must say so instead of inventing a form.
        watch.Restart();
        var nothing = tool.GetLegalForms(LegalJurisdiction.IT, "zibildo quazzico vrenfolo", "civile");
        watch.Stop();
        Save("forms-inesistente", nothing);
        Console.WriteLine($"forms-inesistente      {watch.ElapsedMilliseconds,6} ms  {nothing.Length,7} chars");
        Check(nothing.StartsWith(LegalTemplates.NoTemplateText, StringComparison.Ordinal) ||
              nothing.StartsWith(LegalTemplates.OfflineText, StringComparison.Ordinal),
            $"a form was returned for an impossible document name: {ItalianText.Clip(nothing, 200)}");

        Console.WriteLine();
    }

    /// <summary>Extracts the body of a fenced template answer, or the whole text when there is no fence.</summary>
    private static string TemplateBody(string answer)
    {
        const string open = "```form";
        var start = answer.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return answer;
        start = answer.IndexOf('\n', start);
        if (start < 0) return answer;
        start++;
        var end = answer.IndexOf("\n```", start, StringComparison.Ordinal);
        return end < 0 ? answer[start..] : answer[start..end];
    }

    /// <summary>
    /// Prints what the agent actually reads: the catalogue the host builds from this type. Only
    /// <c>&lt;summary&gt;</c>, <c>&lt;param&gt;</c> and <c>&lt;returns&gt;</c> reach it — a
    /// <c>&lt;remarks&gt;</c> block is never rendered, so a rule written there is invisible.
    /// </summary>
    private static void RunDescribe()
    {
        Console.WriteLine("== agent-facing description ==");
        var catalogue = UISupportGeneric.Analyzer.GetToolDefinitions(typeof(LegalAssistantTool));
        Save("describe", catalogue);
        Console.WriteLine(catalogue);
        foreach (var method in new[] { "get_laws", "legal_opinion", "get_legal_forms" })
            Check(catalogue.Contains($"### {method}", StringComparison.Ordinal), $"the catalogue does not advertise {method}");
        Check(catalogue.Contains("mandatory", StringComparison.OrdinalIgnoreCase),
            "the mandatory-form rule does not reach the model");
        Console.WriteLine($"catalogue: {catalogue.Length} chars");
        Console.WriteLine();
    }

    /// <summary>
    /// Drives the web-guide fallback alone, with no agent in the loop: the search for instructions,
    /// the pages read and the model's verdict. A nonsense name must never produce a form.
    /// </summary>
    private static void RunGuide()
    {
        Console.WriteLine("== guida come fare (senza agente) ==");
        LegalLlm.Reset();
        foreach (var (name, scope) in new[] { ("procura speciale", "civile, notaio"), ("zibildo quazzico vrenfolo", "civile") })
        {
            var watch = Stopwatch.StartNew();
            var guide = LegalTemplates.Guide(name, scope);
            watch.Stop();
            Save("guide-" + name.Split(' ')[0], guide ?? "(nessuna guida)");
            var placeholders = guide == null
                ? 0
                : System.Text.RegularExpressions.Regex.Matches(guide, @"\[[A-ZÀ-Ù][A-ZÀ-Ù_ ]+\]").Count;
            Console.WriteLine($"{name,-28} {watch.ElapsedMilliseconds,6} ms  " +
                              (guide == null ? "nessuna guida" : $"{guide.Length} chars, segnaposto={placeholders}"));
            if (guide != null)
            {
                Check(placeholders >= 5, $"{name}: la guida accettata ha solo {placeholders} segnaposto");
                Check(guide.Contains('#', StringComparison.Ordinal), $"{name}: la guida accettata non ha struttura markdown");
            }

            if (name.StartsWith("zibildo", StringComparison.Ordinal))
                Check(guide == null, "una guida è stata accettata per un documento inesistente");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Provokes the reuse of a cached form under another scope: the exact file does not exist, the same
    /// document does. The model must judge it like a web candidate and the file must be promoted to the
    /// new scope, so the next identical request is a plain cache hit.
    /// </summary>
    private static void RunCache()
    {
        Console.WriteLine("== cache: stesso documento, ambito diverso ==");
        var tool = new LegalAssistantTool();
        const string scopeA = "civile, appello di Milano";
        const string scopeB = "civile, appello di Milano, Cassazione";
        var pathA = LegalTemplates.CachePath("ricorso in Cassazione", scopeA, "it");
        var pathB = LegalTemplates.CachePath("ricorso in Cassazione", scopeB, "it");
        if (File.Exists(pathB)) File.Delete(pathB);
        if (!File.Exists(pathA))
        {
            Console.WriteLine("  creo il file di partenza…");
            tool.GetLegalForms(LegalJurisdiction.IT, "ricorso in Cassazione", scopeA);
        }

        Check(File.Exists(pathA), $"the starting form was not cached at {Path.GetFileName(pathA)}");
        if (!File.Exists(pathA)) return;

        var watch = Stopwatch.StartNew();
        var answer = tool.GetLegalForms(LegalJurisdiction.IT, "ricorso in Cassazione", scopeB);
        watch.Stop();
        Console.WriteLine($"  ambito nuovo: {watch.ElapsedMilliseconds,6} ms  {answer.Length} chars");
        Check(answer.Contains("```form", StringComparison.Ordinal), "the reused form is not in the form envelope");
        Check(File.Exists(pathB), $"the form was not promoted to {Path.GetFileName(pathB)}");
        Check(watch.ElapsedMilliseconds < 60000, $"the reuse took {watch.ElapsedMilliseconds} ms: not a cache reuse");

        var again = Stopwatch.StartNew();
        var second = tool.GetLegalForms(LegalJurisdiction.IT, "ricorso in Cassazione", scopeB);
        again.Stop();
        Console.WriteLine($"  stesso ambito: {again.ElapsedMilliseconds,6} ms  {second.Length} chars");
        Check(again.ElapsedMilliseconds < 1500, $"the second request of the same scope took {again.ElapsedMilliseconds} ms");

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

        // Templates are third-party forms: their citations are the form's own, not the tool's, so
        // they are not audited against the archive.
        foreach (var file in Directory.GetFiles(_outDir, "*.md")
                     .Where(f => !Path.GetFileName(f).StartsWith("forms-", StringComparison.Ordinal)).OrderBy(f => f))
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