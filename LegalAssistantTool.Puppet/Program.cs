using System.Text;
using AIOrchestrator;
using AIOrchestrator.API;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace LegalAssistantToolPuppet;

/// <summary>
/// Puppet test: a lawyer asks for a ricorso in Cassazione, and the agent has to walk the whole
/// expected path with the real tool set — <c>get_legal_forms</c> for the form, questions for the
/// missing data, the placeholders filled, <c>create_from_markdown</c> for the DOCX, the file handed
/// back. Every tool call is recorded, so the run shows what the agent actually did, not what it says.
/// </summary>
internal static class Program
{
    private const string Request =
        "Devo depositare un ricorso per cassazione contro la sentenza n. 1234/2025 della Corte d'Appello di " +
        "Milano, notificata il 3 marzo 2026. Preparami il ricorso in un file Word.";

    private const string Answers =
        "Ecco i dati che mancavano. Ricorrente: Mario Rossi, nato a Bergamo il 12/05/1980, residente in Via " +
        "Verdi 7, Milano, codice fiscale RSSMRA80E12A794X. Resistente: Alfa S.r.l., con sede in Via Roma 1, " +
        "Milano, difesa dall'Avv. Laura Bianchi. Motivo dedotto: violazione dell'art. 360 n. 3 c.p.c. per " +
        "errata applicazione dell'art. 1218 c.c., avendo la Corte d'Appello respinto la domanda di " +
        "risarcimento di euro 50.000 senza valutare la prova documentale prodotta. Il ricorso si deposita a Milano.";

    private const string Nudge =
        "Procedi pure: recupera il modello del ricorso con get_legal_forms, compila i segnaposto con i dati " +
        "che ti ho dato e crea il file Word con create_from_markdown, poi dimmi il percorso del documento.";

    private static string _provider = "Ornith";
    private static string _workspace = "";
    private static readonly List<string> Trace = [];
    private static int _failures;

    private static int Main(string[] args)
    {
        var index = Array.IndexOf(args, "--provider");
        if (index >= 0 && index + 1 < args.Length) _provider = args[index + 1];
        var allTools = args.Contains("--all-tools");

        if (!ProviderConfigs.TryGet(_provider, out _))
        {
            Console.WriteLine($"Error: provider '{_provider}' is not in providers.json.");
            return 2;
        }

        PrepareWorkspace();
        McpToolRegistry.Register(typeof(LegalAssistantTool));
        McpToolRegistry.Register(typeof(DocumentTool));

        Console.WriteLine($"Provider:  {_provider} ({ProviderConfigs.Get(_provider).ModelName})");
        Console.WriteLine($"Workspace: {_workspace}");
        Console.WriteLine();

        using var harness = new AgentHarness(_provider);
        harness.AgentProgress += (_, e) =>
        {
            if (e.State != AgentHarness.AgentState.Iteration || string.IsNullOrWhiteSpace(e.MethodName)) return;
            Trace.Add(e.MethodName);
            Console.WriteLine($"  call #{e.Iteration}: {e.MethodName}");
        };

        string[] tools = ["LegalAssistantTool", "DocumentTool"];
        if (allTools) return RunAllTools(tools);

        var first = harness.ExecuteAction(Request, tools, maxIterations: 25);
        Console.WriteLine($"\n-- turn 1 --\n{Clip(first.Message)}\n");

        // A lawyer answers the questions: the same conversation continues, as in a real chat.
        var second = harness.ExecuteAction(Answers, tools, maxIterations: 25);
        Console.WriteLine($"-- turn 2 --\n{Clip(second.Message)}\n");

        if (ProducedDocuments().Count == 0)
        {
            var third = harness.ExecuteAction(Nudge, tools, maxIterations: 20);
            Console.WriteLine($"-- turn 3 (nudge) --\n{Clip(third.Message)}\n");
        }

        Check("Ha chiesto il modello con get_legal_forms",
            Trace.Any(t => t.Contains("get_legal_forms", StringComparison.OrdinalIgnoreCase)));
        Check("Ha chiesto i dati mancanti prima di scrivere il documento",
            !(first.Message ?? "").Contains("create_from_markdown", StringComparison.OrdinalIgnoreCase) &&
            ((first.Message ?? "").Contains("manc", StringComparison.OrdinalIgnoreCase) ||
             (first.Message ?? "").Contains('?')));
        Check("Ha creato il documento da markdown (create_from_markdown)",
            Trace.Any(t => t.Contains("create_from_markdown", StringComparison.OrdinalIgnoreCase)));

        var documents = ProducedDocuments();
        Check("Ha prodotto un file .docx", documents.Count > 0);
        // The agent may leave a failed empty .docx beside the real one: judge the richest of them.
        var texts = documents.Select(path => (Path: path, Text: ReadDocx(path)))
            .OrderByDescending(pair => pair.Text.Length).ToList();
        if (texts.Count > 0)
        {
            Console.WriteLine($"  documenti: {string.Join(", ", texts.Select(t => Path.GetFileName(t.Path) + "=" + t.Text.Length))}");
            var text = texts[0].Text;
            Check("Il documento contiene il testo del ricorso", text.Contains("Cassazione", StringComparison.OrdinalIgnoreCase));
            Check("Il documento contiene il ricorrente", text.Contains("Rossi", StringComparison.OrdinalIgnoreCase));
            Check("Il documento cita la sentenza impugnata", text.Contains("1234/2025", StringComparison.Ordinal));
            // The form decides which fields exist: the tax code is checked only when the chosen form has it.
            if (text.Contains("codice fiscal", StringComparison.OrdinalIgnoreCase))
                Check("Il documento contiene il codice fiscale fornito", text.Contains("RSSMRA80E12A794X", StringComparison.OrdinalIgnoreCase));
            else
                Console.WriteLine("  [--] codice fiscale: il modulo scelto non ha quel campo");
            // What the lawyer did not provide must stay a placeholder, and the agent has to say so —
            // in either turn, because it often lists them while asking.
            var said = (first.Message ?? "") + " " + (second.Message ?? "");
            Check("Ha indicato all'utente i segnaposto ancora da compilare",
                said.Contains("manc", StringComparison.OrdinalIgnoreCase) ||
                said.Contains("residu", StringComparison.OrdinalIgnoreCase) ||
                said.Contains("complet", StringComparison.OrdinalIgnoreCase) ||
                said.Contains("forniscimi", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"  documento: {texts[0].Path} ({text.Length} caratteri)");
        }

        Console.WriteLine();
        Console.WriteLine($"Chiamate: {string.Join(" → ", Trace)}");
        Console.WriteLine(_failures == 0 ? "RESULT: all checks passed." : $"RESULT: {_failures} check(s) failed.");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// A real case that needs the whole tool: the law on the charge, a reasoned opinion, the form and the
    /// Word file. The run passes only when the agent actually used all three methods and produced the .docx.
    /// </summary>
    private static int RunAllTools(string[] tools)
    {
        var turns = new[]
        {
            "Il mio cliente è imputato di furto aggravato (art. 625 c.p.) con l'aggravante dell'esposizione alla " +
            "pubblica fede: dimmi cosa prevedono le norme in vigore.",
            "Preparami un parere legale sull'ipotesi di ricorso in Cassazione avverso una sentenza di condanna per " +
            "furto aggravato: il veicolo era aperto e le chiavi erano nel cruscotto.",
            "Ora prepara il ricorso per cassazione in Word. Ricorrente: Mario Rossi, nato a Bergamo il 12/05/1980, " +
            "residente in Via Verdi 7, Milano, CF RSSMRA80E12A794X. Resistente: Alfa S.r.l., difesa dall'Avv. Laura " +
            "Bianchi. Sentenza n. 1234/2025 della Corte d'Appello di Milano, notificata il 3 marzo 2026. Motivo: " +
            "violazione dell'art. 360 n. 3 c.p.c. per errata applicazione dell'art. 1218 c.c.",
        };

        // One chat per request, like three separate questions from the same lawyer: an opinion of 45 000 chars
        // carried into the next turn makes a local model dictate for minutes without adding anything.
        for (var index = 0; index < turns.Length; index++)
        {
            using var chat = new AgentHarness(_provider);
            chat.AgentProgress += (_, e) =>
            {
                if (e.State != AgentHarness.AgentState.Iteration || string.IsNullOrWhiteSpace(e.MethodName)) return;
                Trace.Add(e.MethodName);
                Console.WriteLine($"  ask {index + 1}, call #{e.Iteration}: {e.MethodName}");
            };
            var result = chat.ExecuteAction(turns[index], tools, maxIterations: 25);
            var message = result.Message ?? "(nessun messaggio)";
            Console.WriteLine($"-- ask {index + 1} --\n{message[..Math.Min(300, message.Length)]}\n");
        }

        Console.WriteLine("Metodi usati: " + string.Join(", ", Trace.Distinct(StringComparer.OrdinalIgnoreCase)));
        Check("Ha usato get_laws", Trace.Any(t => t.Contains("get_laws", StringComparison.OrdinalIgnoreCase)));
        Check("Ha usato legal_opinion", Trace.Any(t => t.Contains("legal_opinion", StringComparison.OrdinalIgnoreCase)));
        Check("Ha usato get_legal_forms", Trace.Any(t => t.Contains("get_legal_forms", StringComparison.OrdinalIgnoreCase)));
        var byMarkdown = Trace.Any(t => t.Contains("create_from_markdown", StringComparison.OrdinalIgnoreCase));
        var byEditor = Trace.Any(t => t.Contains("add_paragraph", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"  percorso del documento: {(byMarkdown ? "create_from_markdown" : byEditor ? "editor (open_or_create + add_paragraph)" : "nessuno")}");
        Check("Ha creato il documento Word", byMarkdown || byEditor);

        var documents = ProducedDocuments();
        Check("Ha prodotto un file .docx", documents.Count > 0);
        if (documents.Count > 0)
        {
            var best = documents.Select(path => (Path: path, Text: ReadDocx(path)))
                .OrderByDescending(pair => pair.Text.Length).First();
            Console.WriteLine($"  documento: {Path.GetFileName(best.Path)} ({best.Text.Length} caratteri)");
            Check("Il documento parla di Cassazione", best.Text.Contains("Cassazione", StringComparison.OrdinalIgnoreCase));
        }

        Console.WriteLine(_failures == 0 ? "RESULT: all checks passed." : $"RESULT: {_failures} check(s) failed.");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>Prepares an isolated workspace: the sandbox root for every path the tools resolve.</summary>
    private static void PrepareWorkspace()
    {
        Log.IsEnabled = true;
        _workspace = Path.Combine(Path.GetTempPath(), "LegalAssistantTool.Puppet-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspace);
        Setup.SkipIndexingOnStartup = true;
        Setup.DocumentsPath = _workspace;
        // A LAN or cloud provider that needs a key takes it from the environment, never from providers.json.
        var key = Environment.GetEnvironmentVariable("LEGALASSISTANTTOOL_TEST_KEY");
        if (!string.IsNullOrWhiteSpace(key))
            ProviderConfigs.SetApiKey(_provider, key.Trim(), persist: false);
        Setup.ProviderConfig = ProviderConfigs.Get(_provider);
        ProviderConfigs.SetDefault(_provider, persist: false);
    }

    /// <summary>Every DOCX the agent produced in the workspace.</summary>
    private static List<string> ProducedDocuments() =>
        Directory.Exists(_workspace)
            ? Directory.GetFiles(_workspace, "*.docx", SearchOption.AllDirectories).ToList()
            : [];

    /// <summary>Reads the paragraphs and table cells of a DOCX.</summary>
    private static string ReadDocx(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var builder = new StringBuilder();
        var body = document.MainDocumentPart?.Document?.Body;
        if (body == null) return string.Empty;
        foreach (var paragraph in body.Descendants<Paragraph>()) builder.AppendLine(paragraph.InnerText);
        return builder.ToString();
    }

    private static void Check(string label, bool condition)
    {
        Console.WriteLine($"  [{(condition ? "OK" : "NO")}] {label}");
        if (!condition) _failures++;
    }

    private static string Clip(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "(nessun messaggio)" : text.Trim();
}
