using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AIOrchestrator.API.Legal;

/// <summary>Number of documents produced by a normalisation run.</summary>
/// <param name="Articles">Normalised code articles.</param>
/// <param name="Judgments">Normalised judgment schede.</param>
internal readonly record struct NormalizeCounts(int Articles, int Judgments);

/// <summary>
/// Turns the two raw repositories into one flat, indexable corpus: the article JSON files of
/// <c>leggi-italiane</c> and the Markdown schede of <c>giurisprudenza-db</c> are read once,
/// flattened to <see cref="LegalDocument"/> records and written as JSON Lines, which is the
/// normalised format every retrieval engine consumes.
/// </summary>
internal static class LegalNormalizer
{
    private static readonly string[] SkippedFolders = ["RADAR", "RASSEGNE", "RPC", "ANNUARIO", "_QUARANTENA", "scripts", ".github"];
    private static readonly string[] SkippedFiles = ["INDICE.md", "LOG_ERRORI.md", "README.md", "manifest.json"];

    private static readonly string[] CriminalMarkers =
        ["cod. pen", "c.p.", "imputato", "reato", "penale", "condannato", "assolto", "querela", "dolo", "pena"];

    private static readonly string[] CivilMarkers =
        ["cod. civ", "c.c.", "attore", "convenuto", "civile", "contratto", "obbligazione", "risarcimento", "creditore"];

    /// <summary>Normalises both repositories and writes the JSONL corpus.</summary>
    /// <param name="leggiRoot">Extracted <c>leggi-italiane</c> folder.</param>
    /// <param name="giuriRoot">Extracted <c>giurisprudenza-db</c> folder.</param>
    /// <returns>Counts of the produced documents.</returns>
    public static NormalizeCounts NormalizeAll(string leggiRoot, string giuriRoot)
    {
        LegalStorage.EnsureDirectories();

        var articles = NormalizeArticles(leggiRoot);
        WriteJsonLines(LegalStorage.ArticlesJsonl, articles);
        Log.LogStep($"Normalised {articles.Count} articles from {leggiRoot}");

        var judgments = NormalizeJudgments(giuriRoot);
        WriteJsonLines(LegalStorage.JudgmentsJsonl, judgments);
        Log.LogStep($"Normalised {judgments.Count} judgments from {giuriRoot}");

        return new NormalizeCounts(articles.Count, judgments.Count);
    }

    /// <summary>Normalises the article JSON files of a code repository.</summary>
    /// <param name="leggiRoot">Extracted repository root.</param>
    /// <returns>The article documents.</returns>
    public static List<LegalDocument> NormalizeArticles(string leggiRoot)
    {
        var result = new List<LegalDocument>();
        var dataRoot = Path.Combine(leggiRoot, "data");
        if (!Directory.Exists(dataRoot))
        {
            Log.LogStep($"Article normalisation skipped: {dataRoot} not found.");
            return result;
        }

        foreach (var codeDir in Directory.GetDirectories(dataRoot))
        {
            var codeId = Path.GetFileName(codeDir);
            var codiceDir = Path.Combine(codeDir, "codice");
            if (!Directory.Exists(codiceDir)) continue;

            foreach (var file in Directory.EnumerateFiles(codiceDir, "*.json"))
            {
                try
                {
                    var document = ParseArticle(file, codeId);
                    if (document != null) result.Add(document);
                }
                catch (Exception ex)
                {
                    Log.LogStep($"Article {Path.GetFileName(file)} skipped: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        return result;
    }

    private static LegalDocument? ParseArticle(string file, string codeId)
    {
        using var stream = File.OpenRead(file);
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;

        if (!root.TryGetProperty("articolo", out var articolo)) return null;
        var number = GetString(articolo, "numero");
        if (string.IsNullOrWhiteSpace(number)) return null;

        var rubric = GetString(articolo, "rubrica");
        var section = GetString(articolo, "sezione");
        var url = GetString(articolo, "url");
        var codeName = "Codice";
        if (root.TryGetProperty("codice", out var codice)) codeName = GetString(codice, "denominazione");

        var stateCode = "vigente";
        var stateNote = string.Empty;
        var notApplicable = false;
        if (root.TryGetProperty("stato", out var stato))
        {
            stateCode = GetString(stato, "codice");
            if (string.IsNullOrEmpty(stateCode)) stateCode = "vigente";
            stateNote = GetString(stato, "motivazione");
            if (stato.TryGetProperty("inapplicabile", out var inapplicabile) && inapplicabile.ValueKind == JsonValueKind.True)
                notApplicable = true;
        }

        var body = string.Empty;
        if (root.TryGetProperty("testo", out var testo))
        {
            body = GetString(testo, "vigente");
            if (string.IsNullOrWhiteSpace(body)) body = GetString(testo, "con_marcatori");
        }

        var abbreviation = codeId == "penale" ? "c.p." : codeId == "civile" ? "c.c." : codeName;
        var citation = $"Art. {number} {abbreviation}";

        return new LegalDocument
        {
            Id = $"art:{codeId}:{number}",
            Kind = LegalDocKind.Article,
            CaseType = codeId == "penale" ? CaseType.Criminal : codeId == "civile" ? CaseType.Civil : null,
            Heading = string.IsNullOrWhiteSpace(rubric) ? citation : $"{citation} — {rubric}",
            Body = CleanText(body),
            Citation = citation,
            StateCode = stateCode,
            StateNote = CleanText(stateNote),
            NotApplicable = notApplicable,
            Url = string.IsNullOrWhiteSpace(url) ? null : url,
            Matter = rubric,
            CodeId = codeId,
            ArticleNumber = number,
            Section = section,
            SourceRepo = "leggi-italiane",
            Extra = CleanText(section),
        };
    }

    /// <summary>Normalises the Markdown schede of the case-law repository.</summary>
    /// <param name="giuriRoot">Extracted repository root.</param>
    /// <returns>The judgment documents.</returns>
    public static List<LegalDocument> NormalizeJudgments(string giuriRoot)
    {
        var result = new List<LegalDocument>();
        if (!Directory.Exists(giuriRoot))
        {
            Log.LogStep($"Judgment normalisation skipped: {giuriRoot} not found.");
            return result;
        }

        var skipped = 0;
        foreach (var file in Directory.EnumerateFiles(giuriRoot, "*.md", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(giuriRoot, file).Replace('\\', '/');
            if (ShouldSkip(relative))
            {
                skipped++;
                continue;
            }

            try
            {
                var document = ParseScheda(file, relative);
                if (document != null) result.Add(document);
            }
            catch (Exception ex)
            {
                Log.LogStep($"Scheda {relative} skipped: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Log.LogStep($"Judgment normalisation: kept {result.Count}, skipped {skipped} non-citable files.");
        return result;
    }

    private static bool ShouldSkip(string relative)
    {
        foreach (var folder in SkippedFolders)
            if (relative.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)
                || relative.Contains("/" + folder + "/", StringComparison.OrdinalIgnoreCase))
                return true;

        var name = Path.GetFileName(relative);
        foreach (var skippedFile in SkippedFiles)
            if (string.Equals(name, skippedFile, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private static LegalDocument? ParseScheda(string file, string relative)
    {
        var raw = File.ReadAllText(file, Encoding.UTF8);
        if (!raw.StartsWith("---", StringComparison.Ordinal)) return null;

        var endOfFrontMatter = raw.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endOfFrontMatter < 0) return null;

        var frontMatter = raw.Substring(3, endOfFrontMatter - 3);
        var body = raw[(endOfFrontMatter + 4)..];
        var meta = ParseFrontMatter(frontMatter);

        var title = string.Empty;
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var buffer = new StringBuilder();
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                if (current != null) sections[current] = buffer.ToString().Trim();
                current = trimmed[3..].Trim();
                buffer.Clear();
                continue;
            }

            if (trimmed.StartsWith("# ", StringComparison.Ordinal) && current == null && title.Length == 0)
            {
                title = trimmed[2..].Trim();
                continue;
            }

            buffer.AppendLine(trimmed);
        }

        if (current != null) sections[current] = buffer.ToString().Trim();

        var isConsulta = meta.TryGetValue("corte", out var corte)
            && corte.Contains("costituzionale", StringComparison.OrdinalIgnoreCase);

        var massima = FirstSection(sections, "Massima ufficiale (Oggetto)", "Massime ufficiali", "Massima");
        var outcome = FirstSection(sections, "L'esito in sintesi", "Dispositivo", "Quesito");
        var citation = string.IsNullOrWhiteSpace(title) ? BuildCitation(meta, isConsulta) : title;
        if (Get(meta, "tipo").StartsWith("questione", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(Get(meta, "rg")))
            citation = BuildQuestionCitation(meta);

        var matter = Get(meta, "materia");
        if (string.IsNullOrWhiteSpace(matter) && isConsulta) matter = Get(meta, "tipologia_giudizio");

        var bodyText = CleanText(string.Join("\n\n", new[] { massima, outcome }.Where(s => !string.IsNullOrWhiteSpace(s))));
        if (bodyText.Length == 0) bodyText = CleanText(sections.GetValueOrDefault("Fonte autentica") ?? string.Empty);

        var caseType = isConsulta ? InferCaseType(citation + " " + bodyText) : InferCaseTypeFromPath(relative);
        caseType ??= InferCaseType(citation + " " + bodyText + " " + Get(meta, "url_ordinanza_pdf") + " " + Get(meta, "url_scheda"));
        var name = Path.GetFileNameWithoutExtension(file);

        return new LegalDocument
        {
            Id = "jud:" + name,
            Kind = LegalDocKind.Judgment,
            CaseType = caseType,
            Heading = citation,
            Body = bodyText,
            Citation = citation,
            Url = NullIfEmpty(Get(meta, "url_scheda")),
            PdfUrl = NullIfEmpty(Get(meta, "url_pdf")),
            Court = isConsulta
                ? "Corte costituzionale"
                : "Corte di cassazione" + FormatSection(Get(meta, "sezione"), caseType),
            Matter = matter,
            DecisionDate = NullIfEmpty(First(Get(meta, "data_deposito"), Get(meta, "data_decisione"), Get(meta, "data_ordinanza"))),
            SourceRepo = "giurisprudenza-db",
            Extra = CleanText(massima),
            JudgmentType = Get(meta, "tipo"),
            Judge = First(Get(meta, "relatore"), Get(meta, "redattore")),
            StateCode = "vigente",
            StateNote = "fonte: " + (isConsulta ? "Corte costituzionale (CC BY-SA 3.0)" : "Corte di cassazione — massimario"),
        };
    }

    private static CaseType? InferCaseTypeFromPath(string relative)
    {
        if (relative.StartsWith("CIVILE/", StringComparison.OrdinalIgnoreCase)) return CaseType.Civil;
        if (relative.StartsWith("CONSULTA/", StringComparison.OrdinalIgnoreCase)) return null;
        return CaseType.Criminal;
    }

    private static CaseType? InferCaseType(string text)
    {
        var normalized = ItalianText.Normalize(text);
        var criminal = CriminalMarkers.Sum(marker => CountOccurrences(normalized, ItalianText.Normalize(marker)));
        var civil = CivilMarkers.Sum(marker => CountOccurrences(normalized, ItalianText.Normalize(marker)));
        if (criminal == civil) return null;
        return criminal > civil ? CaseType.Criminal : CaseType.Civil;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (needle.Length == 0) return 0;
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>
    /// Citation of a Sezioni Unite question. These schede carry no decision number - the question is
    /// identified by the registration number and the hearing date - so the citation is built from
    /// those, keeping it unique and verifiable on the Court's own site.
    /// </summary>
    /// <param name="meta">Front matter of the scheda.</param>
    /// <returns>The citation.</returns>
    private static string BuildQuestionCitation(Dictionary<string, string> meta)
    {
        var rg = Get(meta, "rg").Trim();
        var hearing = First(Get(meta, "data_udienza"), Get(meta, "data_inserimento"));
        var date = DateTime.TryParse(hearing, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
            : hearing;
        return string.IsNullOrWhiteSpace(date)
            ? $"Cass., Sez. U — questione n. R.G. {rg}"
            : $"Cass., Sez. U — questione n. R.G. {rg} (ud. {date})";
    }
    private static string BuildCitation(Dictionary<string, string> meta, bool isConsulta)
    {
        var number = Get(meta, "numero");
        var year = Get(meta, "anno");
        if (isConsulta) return $"Corte cost., n. {number}/{year}";
        var section = Get(meta, "sezione");
        return $"Cass., Sez. {section}, n. {number}/{year}";
    }

    private static string FormatSection(string section, CaseType? caseType)
    {
        if (string.IsNullOrWhiteSpace(section)) return string.Empty;
        var suffix = caseType == CaseType.Civil ? " civile" : caseType == CaseType.Criminal ? " penale" : string.Empty;
        return $", Sez. {section}{suffix}";
    }

    private static string FirstSection(Dictionary<string, string> sections, params string[] candidates)
    {
        foreach (var candidate in candidates)
            foreach (var pair in sections)
                if (pair.Key.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
                    return CleanText(pair.Value);
        return string.Empty;
    }

    private static Dictionary<string, string> ParseFrontMatter(string block)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in block.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r').Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var separator = trimmed.IndexOf(':');
            if (separator <= 0) continue;
            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim().Trim('"', '\'');
            if (value is "null" or "~" || value.Length == 0) continue;
            result[key] = value;
        }

        return result;
    }

    private static string Get(Dictionary<string, string> meta, string key) =>
        meta.TryGetValue(key, out var value) ? value : string.Empty;

    private static string First(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Collapses whitespace and strips Markdown noise so the text is indexable as-is.</summary>
    /// <param name="text">Raw text.</param>
    /// <returns>Cleaned text.</returns>
    internal static string CleanText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var builder = new StringBuilder(text.Length);
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
                continue;
            }

            while (trimmed.StartsWith('>')) trimmed = trimmed[1..].TrimStart();
            trimmed = trimmed.Replace("**", string.Empty).Replace("__", string.Empty);
            builder.Append(trimmed).Append(' ');
        }

        var collapsed = builder.ToString();
        while (collapsed.Contains("  ", StringComparison.Ordinal)) collapsed = collapsed.Replace("  ", " ");
        return collapsed.Trim();
    }

    private static void WriteJsonLines(string path, List<LegalDocument> documents)
    {
        var options = new JsonSerializerOptions { WriteIndented = false };
        using var stream = File.Create(path);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        foreach (var document in documents)
            writer.WriteLine(JsonSerializer.Serialize(document, options));
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}