using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DocSharp.Binary.DocFileFormat;
using DocSharp.Binary.StructuredStorage.Reader;

namespace AIOrchestrator.API.Legal;

/// <summary>A downloaded candidate form with the Markdown extracted from it.</summary>
/// <param name="Url">Address the form was downloaded from.</param>
/// <param name="Title">Title shown by the search engine.</param>
/// <param name="Markdown">Content converted to Markdown.</param>
internal sealed record LegalFormCandidate(string Url, string Title, string Markdown);

/// <summary>The model's verdict on one candidate document.</summary>
internal sealed class LegalFormVerdict
{
    /// <summary>True when the document is exactly the act asked for.</summary>
    public bool IsRequested { get; set; }

    /// <summary>Completeness as a blank form, 0-10; only used to rank the documents that are the right act.</summary>
    public int Rating { get; set; }

    /// <summary>Short justification.</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// Retrieves blank legal forms: searches the web for the document under its local name, converts
/// every candidate to Markdown, has the model rate them, normalises the winner into a template with
/// <c>[PLACEHOLDER]</c> fields and caches it, so the same form is served from disk next time.
/// Every search, download and model step is optional: when one of them is unavailable the method
/// says so instead of inventing a form.
/// </summary>
internal static class LegalTemplates
{
    /// <summary>How many search results are kept as candidates.</summary>
    private const int MaxCandidates = 10;

    /// <summary>Text returned when no downloadable form was found.</summary>
    public const string NoTemplateText =
        "No template is available: no downloadable form was found for this document name in this jurisdiction. " +
        "Retry with the name the document has in the country (e.g. \"ricorso in Cassazione\"), or keep the name " +
        "used by the law returned by get_laws; do not draft a document for legal use without a form.";

    /// <summary>Text returned when the internet is unreachable.</summary>
    public const string OfflineText =
        "No template is available: the function is currently unavailable because the internet connection is not reachable. " +
        "Retry when the connection is back; do not draft a document for legal use from memory only.";

    private const int MinTemplateChars = 400;
    private const int MaxDownloadBytes = 8 * 1024 * 1024;
    private const int NormalizeInputChars = 14000;
    private const int DownloadSeconds = 25;

    /// <summary>Seconds that must be left before another candidate is judged: below it the answer would be abandoned mid-way.</summary>
    private const int JudgingSecondsLeft = 45;
    private const double MinCoverage = 0.5;
    private const int MaxGuidePages = 4;
    private const int GuidePageChars = 4000;

    /// <summary>Rating that ends the judgement of the remaining candidates: top of the rubric (8-10 = exactly this act and complete).</summary>
    private const int GoodEnoughRating = 9;

    private static readonly HttpClient Client = CreateClient();

    /// <summary>Serialises the legacy .doc conversion: the binary reader is not re-entrant across threads.</summary>
    private static readonly object DocGate = new();

    /// <summary>
    /// Returns the template of a document for a jurisdiction, from the local cache or from the web.
    /// </summary>
    /// <param name="jurisdiction">Jurisdiction code.</param>
    /// <param name="documentName">Document name as it is used in that country.</param>
    /// <param name="documentScope">Field and court the document is for.</param>
    /// <returns>The fenced template, or the text explaining why no template is available.</returns>
    public static string Get(LegalJurisdiction jurisdiction, string documentName, string documentScope)
    {
        LegalLlm.Reset();
        var name = ItalianText.CollapseWhitespace(documentName);
        var scope = ItalianText.CollapseWhitespace(documentScope);
        if (name.Length < 3)
            return "Error: 'documentName' is required: give the name of the document as it is called in the country " +
                   "where it will be used, e.g. \"ricorso in Cassazione\".";
        if (scope.Length < 4)
            return "Error: 'scope' is required: state the field and the court the document is for, e.g. " +
                   "\"civile, appello Milano\" or \"penale, tribunale di Roma\". A document from another field must not be used.";

        var domain = CountryDomain(jurisdiction);
        var cachePath = CachePath(name, scope, domain);
        var cached = ReadCache(cachePath);
        if (cached != null)
        {
            Log.LogStep($"LegalTemplates: cache hit for '{name}' [{scope}] ({Path.GetFileName(cachePath)}, {cached.Length} chars).");
            return Envelope(cached, null);
        }

        if (!Online())
        {
            Log.LogStep($"LegalTemplates: '{name}' [{scope}] requested offline.");
            return OfflineText;
        }

        // A form of this document cached under another scope is a candidate like any other: it is judged
        // with the same prompt as a form downloaded from the web, and promoted to this scope only when the
        // model says it is the right act here. One file alone skips the ranking, not the judgement.
        var nearby = CachedForms(name, domain, cachePath);
        if (nearby.Count > 0)
        {
            Log.LogStep($"LegalTemplates: {nearby.Count} cached form(s) of '{name}' in another scope; judging them " +
                        $"like web candidates.");
            LegalFormCandidate? accepted = null;
            var acceptedRating = -1;
            foreach (var cachedForm in nearby)
            {
                if (LegalLlm.Exhausted || LegalLlm.RemainingSeconds < JudgingSecondsLeft) break;
                var verdict = JudgeOne(name, scope, cachedForm);
                if (verdict == null) continue;
                Log.LogStep($"LegalTemplates: cache {cachedForm.Url} requested={verdict.IsRequested} " +
                            $"rating={verdict.Rating} ({verdict.Reason}).");
                if (!verdict.IsRequested) continue;
                if (verdict.Rating > acceptedRating) { accepted = cachedForm; acceptedRating = verdict.Rating; }
                if (acceptedRating >= GoodEnoughRating) break;
            }

            if (accepted != null)
            {
                var body = Sanitize(accepted.Markdown);
                SaveCache(cachePath, body);
                Log.LogStep($"LegalTemplates: cached form '{accepted.Url}' accepted for '{scope}' " +
                            $"({acceptedRating}/10) and promoted to {Path.GetFileName(cachePath)}.");
                return Envelope(body, null);
            }

            Log.LogStep("LegalTemplates: no cached form of this document is the right act in this scope.");
        }

        var best = SearchAndJudge(name, scope, withScopeInQuery: false);
        if (best == null)
        {
            // A niche field can lose the right form when the query is generic: the search without the
            // scope returns only general forms, so it is repeated WITH the scope and the new candidates
            // are judged. Costly, therefore only as a fallback.
            Log.LogStep($"LegalTemplates: no usable form for '{name}' without the scope; searching with '{scope}'.");
            best = SearchAndJudge(name, scope, withScopeInQuery: true);
        }

        if (best == null)
        {
            if (!Online()) return OfflineText;
            var guide = Guide(name, scope);
            if (guide == null)
            {
                Log.LogStep($"LegalTemplates: no form and no usable guide for '{name}' [{scope}].");
                return NoTemplateText;
            }

            guide = Sanitize(guide);
            SaveCache(cachePath, guide);
            Log.LogStep($"LegalTemplates: '{name}' [{scope}] built from a web guide ({guide.Length} chars, cached as {Path.GetFileName(cachePath)}).");
            return Envelope(guide, "No published form was usable, so this template was built from web guides on how to write this document in this scope. Check it against the law before using it.");
        }

        var note = (string?)null;
        var template = Normalize(name, scope, best.Markdown);
        if (string.IsNullOrWhiteSpace(template))
        {
            template = best.Markdown;
            note = "The form is returned as found: it could not be normalised, so correct the text and add the " +
                   "[PLACEHOLDER] fields yourself before using it.";
        }
        else if (!template.Contains('[', StringComparison.Ordinal))
        {
            note = "The form was normalised but no field could be marked, so add the [PLACEHOLDER] fields yourself " +
                   "before using it.";
        }

        // The body must not close the envelope early, and the cache must hold exactly what is returned.
        template = Sanitize(template);
        SaveCache(cachePath, template);
        Log.LogStep($"LegalTemplates: '{name}' [{scope}] served from {best.Url} ({template.Length} chars, cached as {Path.GetFileName(cachePath)}).");
        return Envelope(template, note);
    }

    /// <summary>Country-code top-level domain of the jurisdiction, the IANA form of its ISO 3166-1 code.</summary>
    /// <param name="jurisdiction">Jurisdiction code.</param>
    /// <returns>The two-letter domain, without the dot; a country whose ccTLD differs from the ISO code (GB → uk) needs its own entry here.</returns>
    public static string CountryDomain(LegalJurisdiction jurisdiction) => jurisdiction switch
    {
        LegalJurisdiction.IT => "it",
        _ => jurisdiction.ToString().ToLowerInvariant()
    };

    /// <summary>Cache file of a form: <c>&lt;document&gt;.&lt;scope&gt;.&lt;domain&gt;.md</c>, e.g. <c>ricorso_in_cassazione.civile.it.md</c>.</summary>
    /// <param name="documentName">Document name as the caller wrote it.</param>
    /// <param name="documentScope">Field and court the document is for.</param>
    /// <param name="domain">Country-code top-level domain.</param>
    /// <returns>Absolute path of the cache file.</returns>
    public static string CachePath(string documentName, string documentScope, string domain) =>
        Path.Combine(LegalStorage.TemplatesDirectory,
            Slug(documentName, 80) + "." + Slug(documentScope, 40) + "." + domain + ".md");

    /// <summary>Lower-case, accent-folded, underscore-separated form of a text, usable in a file name.</summary>
    /// <param name="text">Text to slug.</param>
    /// <param name="maxLength">Maximum length of the result.</param>
    /// <returns>The slug; "template" when the text has no letter or digit.</returns>
    private static string Slug(string text, int maxLength)
    {
        var builder = new StringBuilder(maxLength);
        foreach (var character in ItalianText.Normalize(text))
        {
            if (char.IsLetterOrDigit(character)) builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != '_') builder.Append('_');
        }

        var clean = builder.ToString().Trim('_');
        if (clean.Length > maxLength)
        {
            clean = clean[..maxLength];
            var cut = clean.LastIndexOf('_');
            if (cut > maxLength / 2) clean = clean[..cut];
            clean = clean.Trim('_');
        }

        return clean.Length == 0 ? "template" : clean;
    }

    /// <summary>The line that introduces every answer: the form is a demonstration and may be completed beyond its placeholders.</summary>
    private const string FormIntro =
        "Demonstration form; fill the data, including any information not covered by the placeholders";

    /// <summary>Wraps a template in the envelope the agent expects.</summary>
    /// <param name="markdown">Template body.</param>
    /// <param name="note">Optional line shown before the envelope.</param>
    /// <returns>The fenced answer.</returns>
    public static string Envelope(string markdown, string? note)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(note))
        {
            builder.AppendLine(note);
            builder.AppendLine();
        }

        // The body must not close the envelope early: an inner fence becomes a tilde fence.
        builder.AppendLine(FormIntro);
        builder.AppendLine("```form");
        builder.AppendLine(Sanitize(markdown));
        builder.AppendLine("```");
        return builder.ToString();
    }

    /// <summary>Makes a template safe to wrap in a Markdown fence and free of converter escapes.</summary>
    /// <param name="markdown">Template body.</param>
    /// <returns>The body with any inner code fence turned into a tilde fence, and the escapes the Word converter adds to parentheses and underscores removed — in a form they would reach the document verbatim.</returns>
    public static string Sanitize(string markdown) =>
        markdown.Replace("```", "~~~", StringComparison.Ordinal)
            .Replace("\\)", ")").Replace("\\(", "(").Replace("\\_", "_")
            .Trim();

    private static string? ReadCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path, Encoding.UTF8).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex)
        {
            Log.LogStep($"LegalTemplates: cache {Path.GetFileName(path)} unreadable ({ex.GetType().Name}).");
            return null;
        }
    }

    private static void SaveCache(string path, string markdown)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, markdown, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.LogStep($"LegalTemplates: cache {Path.GetFileName(path)} not written ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    /// <summary>Searches for the form and converts every candidate it can download.</summary>
    /// <param name="documentName">Document name in the local language.</param>
    /// <param name="scopeInQuery">Scope to add to the query, or null for the plain search. The plain search runs first; the scoped one only when it produced no usable form, because a scope in the query makes it the dominant signal and the act type slips (measured: it picked a ricorso ex art. 492-bis over a ricorso in Cassazione).</param>
    /// <returns>Converted candidates, in search order; empty when nothing usable came back.</returns>
    private static List<LegalFormCandidate> Candidates(string documentName, string? scopeInQuery)
    {
        var found = new List<(string Url, string Title)>();
        // Word is asked for as .doc first, the historical format of published forms; .docx is the
        // fallback, because most public offices moved there without changing the catalogue entry.
        var query = string.IsNullOrWhiteSpace(scopeInQuery)
            ? $"{documentName} modello"
            : $"{documentName} {ItalianText.CollapseWhitespace(Regex.Replace(scopeInQuery, @"[,;:/]", " "))} modello";
        foreach (var filetype in new[] { "doc", "docx" })
        {
            WebSearchEngine.SearchResult[] results;
            try
            {
                results = WebSearchEngine.Search(query, filetype, WebSearchEngine.SearchProvider.DuckDuckGo);
            }
            catch (Exception ex)
            {
                Log.LogStep($"LegalTemplates: search filetype={filetype} failed ({ex.GetType().Name}: {ex.Message}).");
                continue;
            }

            Log.LogStep($"LegalTemplates: search filetype={filetype} '{query}' returned {results.Length} result(s).");
            foreach (var result in results)
            {
                if (string.IsNullOrWhiteSpace(result.Url)) continue;
                if (found.Any(f => string.Equals(f.Url, result.Url, StringComparison.OrdinalIgnoreCase))) continue;
                found.Add((result.Url, FirstLine(result.MdDescription)));
                if (found.Count >= MaxCandidates) break;
            }

            if (found.Count > 0) break;
        }

        if (found.Count == 0) return [];

        var downloads = found.Select(item => Task.Run(() => Fetch(item.Url, item.Title))).ToArray();
        try
        {
            Task.WaitAll(downloads, TimeSpan.FromSeconds(DownloadSeconds * 2));
        }
        catch (AggregateException ex)
        {
            Log.LogStep($"LegalTemplates: a download failed ({ex.InnerException?.GetType().Name}).");
        }

        var candidates = downloads.Where(t => t.IsCompletedSuccessfully && t.Result != null)
            .Select(t => t.Result!).ToList();

        // A search for a plausible name always returns something: unrelated files, invoices, lecture
        // notes. Before spending a model call, a candidate must share the vocabulary of the document
        // asked for, exactly as the web enrichment keeps a page only when it covers the case.
        var anchors = ItalianText.Tokenize(documentName);
        var relevant = new List<LegalFormCandidate>();
        foreach (var candidate in candidates)
        {
            var coverage = anchors.Count == 0 ? 1.0 : ItalianText.Coverage(anchors, ItalianText.TermSet(candidate.Markdown));
            Log.LogStep($"LegalTemplates: {candidate.Url} coverage {ItalianText.Num(coverage)} ({candidate.Markdown.Length} chars).");
            if (coverage >= MinCoverage) relevant.Add(candidate);
        }

        Log.LogStep($"LegalTemplates: {relevant.Count} relevant candidate(s) from {candidates.Count} converted, {found.Count} found.");
        return relevant;
    }

    private static LegalFormCandidate? Fetch(string url, string title)
    {
        try
        {
            using var response = Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                Log.LogStep($"LegalTemplates: {url} answered {(int)response.StatusCode}.");
                return null;
            }

            using var stream = response.Content.ReadAsStream();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaxDownloadBytes)
                {
                    Log.LogStep($"LegalTemplates: {url} exceeds {MaxDownloadBytes} bytes, skipped.");
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            var markdown = Convert(buffer.ToArray());
            if (markdown.Length < MinTemplateChars)
            {
                Log.LogStep($"LegalTemplates: {url} converted to {markdown.Length} chars, skipped.");
                return null;
            }

            return new LegalFormCandidate(url, title, markdown);
        }
        catch (Exception ex)
        {
            Log.LogStep($"LegalTemplates: {url} skipped ({ex.GetType().Name}: {ex.Message}).");
            return null;
        }
    }

    private static string Convert(byte[] data)
    {
        var format = Detect(data);
        if (format == null) return string.Empty;
        try
        {
            var markdown = format == AllToMarkdown.Converter.SupportedFileFormat.doc
                ? ConvertLegacyDoc(data)
                : AllToMarkdown.Converter.ConvertDataToMarkdown(data, format.Value) ?? string.Empty;
            return RepairEncoding(markdown);
        }
        catch (Exception ex)
        {
            Log.LogStep($"LegalTemplates: conversion failed ({ex.GetType().Name}: {ex.Message}).");
            return string.Empty;
        }
    }

    /// <summary>
    /// Undoes the double encoding a binary .doc produces ("notorietÃ " instead of "notorietà", "Â’"
    /// instead of "’"): 8-bit text decoded as Windows-1252 while the bytes are UTF-8. Only the
    /// sequences that decode to a single valid character are rewritten, so text that is already
    /// correct — however it is mixed with the damaged part — is left alone.
    /// </summary>
    /// <param name="text">Converted text.</param>
    /// <returns>The readable text.</returns>
    public static string RepairEncoding(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (!text.Contains('Ã') && !text.Contains('Â')) return text;

        var repaired = Mojibake.Replace(text, match => DecodePair(match.Value) ?? match.Value);
        if (repaired == text) return text;

        // The pair above decoded to a C1 control when the original byte was a Windows-1252
        // punctuation mark: put the real character back.
        var builder = new StringBuilder(repaired.Length);
        foreach (var character in repaired)
        {
            var replacement = character is >= '\u0080' and <= '\u009F' ? Windows1252Low[character - '\u0080'] : character;
            builder.Append(replacement == '\uFFFD' ? character : replacement);
        }

        Log.LogStep("LegalTemplates: repaired the double encoding of the converted text.");
        return builder.ToString();
    }

    /// <summary>A lead byte of a two-byte UTF-8 sequence followed by one of its continuation bytes.</summary>
    private static readonly Regex Mojibake = new(
        "[\u00C2\u00C3][\u0080-\u00BF\u20AC\u201A\u0192\u201E\u2026\u2020\u2021\u02C6\u2030\u0160\u2039\u0152" +
        "\u017D\u2018\u2019\u201C\u201D\u2022\u2013\u2014\u02DC\u2122\u0161\u203A\u0153\u017E\u0178]",
        RegexOptions.Compiled);

    /// <summary>
    /// The Windows-1252 characters of the 0x80-0x9F block, in code order; the slots the code page
    /// leaves undefined keep U+FFFD, which never reaches the output.
    /// </summary>
    private const string Windows1252Low =
        "\u20AC\uFFFD\u201A\u0192\u201E\u2026\u2020\u2021\u02C6\u2030\u0160\u2039\u0152\uFFFD\u017D\uFFFD" +
        "\uFFFD\u2018\u2019\u201C\u201D\u2022\u2013\u2014\u02DC\u2122\u0161\u203A\u0153\uFFFD\u017E\u0178";

    /// <summary>Decodes one two-character mojibake pair, or returns null when it is not one.</summary>
    /// <param name="pair">The matched pair.</param>
    /// <returns>The single character it stands for, or null.</returns>
    private static string? DecodePair(string pair)
    {
        if (pair.Length != 2) return null;
        var lead = ByteOf(pair[0]);
        var trail = ByteOf(pair[1]);
        if (lead is < 0xC2 or > 0xC3 || trail is < 0x80 or > 0xBF) return null;
        var decoded = Encoding.UTF8.GetString([(byte)lead, (byte)trail]);
        return decoded.Length == 1 && decoded[0] != '\uFFFD' ? decoded : null;
    }

    /// <summary>Reads a character as the Windows-1252 byte it was decoded from.</summary>
    /// <param name="character">Character to map.</param>
    /// <returns>The byte value, or -1 when the character has no 8-bit slot.</returns>
    private static int ByteOf(char character)
    {
        if (character <= '\u00FF') return character;
        var index = Windows1252Low.IndexOf(character);
        return index < 0 ? -1 : 0x80 + index;
    }

    /// <summary>
    /// Converts a legacy binary .doc in two steps, .doc → .docx in memory → Markdown, without touching
    /// the disk. The library's single-step path is not used: it returns from inside a <c>finally</c> that
    /// deletes the intermediate .docx while its stream is still open, so every .doc conversion ends in a
    /// sharing violation and the converted text is lost.
    /// </summary>
    private static string ConvertLegacyDoc(byte[] data)
    {
        lock (DocGate)
        {
            using var source = new MemoryStream(data, writable: false);
            using var reader = new StructuredStorageReader(source);
            var document = new WordDocument(reader);
            using var output = new MemoryStream();
            using (var docx = DocSharp.Binary.OpenXmlLib.WordprocessingML.WordprocessingDocument.Create(
                       output, DocSharp.Binary.OpenXmlLib.WordprocessingDocumentType.Document))
                DocSharp.Binary.WordprocessingMLMapping.Converter.Convert(document, docx);
            return AllToMarkdown.Converter.ConvertDataToMarkdown(output.ToArray(),
                AllToMarkdown.Converter.SupportedFileFormat.docx) ?? string.Empty;
        }
    }

    /// <summary>
    /// Decides the format from the first bytes, not from the extension: catalogs label legacy
    /// binaries as .doc, and a page that answers instead of the file would be converted as text.
    /// </summary>
    private static AllToMarkdown.Converter.SupportedFileFormat? Detect(byte[] data)
    {
        if (data.Length < 8) return null;
        if (data[0] == 0x50 && data[1] == 0x4B) return AllToMarkdown.Converter.SupportedFileFormat.docx;
        if (data[0] == 0xD0 && data[1] == 0xCF && data[2] == 0x11 && data[3] == 0xE0)
            return AllToMarkdown.Converter.SupportedFileFormat.doc;

        var head = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 512));
        if (head.Contains("{\\rtf", StringComparison.Ordinal)) return AllToMarkdown.Converter.SupportedFileFormat.rtf;
        if (head.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
            head.Contains("<!doctype", StringComparison.OrdinalIgnoreCase) ||
            head.Contains("<head", StringComparison.OrdinalIgnoreCase)) return null;
        return AllToMarkdown.Converter.SupportedFileFormat.doc;
    }

    /// <summary>Cached forms of the same document under another scope, ready to be judged.</summary>
    /// <param name="documentName">Document name in the local language.</param>
    /// <param name="domain">Country-code top-level domain.</param>
    /// <param name="exactPath">Path of the exact match, excluded from the list.</param>
    /// <returns>One candidate per cached file of that document, best effort.</returns>
    private static List<LegalFormCandidate> CachedForms(string documentName, string domain, string exactPath)
    {
        var result = new List<LegalFormCandidate>();
        var directory = LegalStorage.TemplatesDirectory;
        if (!Directory.Exists(directory)) return result;

        var prefix = Slug(documentName, 80) + ".";
        var suffix = "." + domain + ".md";
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, prefix + "*" + suffix))
            {
                if (string.Equals(file, exactPath, StringComparison.OrdinalIgnoreCase)) continue;
                var text = ReadCache(file);
                if (text == null) continue;
                result.Add(new LegalFormCandidate("cache:" + Path.GetFileName(file),
                    Path.GetFileNameWithoutExtension(file), text));
            }
        }
        catch (Exception ex)
        {
            Log.LogStep($"LegalTemplates: cached forms not readable ({ex.GetType().Name}: {ex.Message}).");
        }

        return result;
    }

    /// <summary>One search pass followed by the judgement of its candidates.</summary>
    /// <param name="documentName">Document name in the local language.</param>
    /// <param name="documentScope">Field and court the document is for.</param>
    /// <param name="withScopeInQuery">True to add the scope to the search query.</param>
    /// <returns>The chosen candidate, or null when this pass found none.</returns>
    private static LegalFormCandidate? SearchAndJudge(string documentName, string documentScope, bool withScopeInQuery)
    {
        var candidates = Candidates(documentName, withScopeInQuery ? documentScope : null);
        Log.LogStep($"LegalTemplates: '{documentName}' [{documentScope}] {(withScopeInQuery ? "scoped" : "plain")} " +
                    $"search produced {candidates.Count} converted candidate(s).");
        return candidates.Count == 0 ? null : Choose(documentName, documentScope, candidates);
    }

    /// <summary>
    /// Last resort when no published form exists: a search for instructions ("come fare …"), the pages
    /// read as sources, and the model asked whether they are reliable and to write the form from them.
    /// A guide can be wrong or describe another country's practice, so the answer is used only when the
    /// model calls it reliable and returns a template.
    /// </summary>
    /// <param name="documentName">Document name in the local language.</param>
    /// <param name="documentScope">Field and court the document is for.</param>
    /// <returns>The Markdown template built from the guides, or null.</returns>
    public static string? Guide(string documentName, string documentScope)
    {
        if (LegalLlm.Exhausted || LegalLlm.RemainingSeconds < 120) return null;

        var sources = new StringBuilder();
        var read = 0;
        // Two queries: the question "how to" and the request for a fac-simile reach different pages, and a
        // guide is only as good as the material it is written from.
        foreach (var search in new[] { $"{documentName} {HowToPhrase}", $"{documentName} fac-simile redigere" })
        {
            WebSearchEngine.SearchResult[] results;
            try
            {
                results = WebSearchEngine.Search(search, filetype: null, WebSearchEngine.SearchProvider.DuckDuckGo);
            }
            catch (Exception ex)
            {
                Log.LogStep($"LegalTemplates: guide search failed ({ex.GetType().Name}: {ex.Message}).");
                continue;
            }

            Log.LogStep($"LegalTemplates: guide search '{search}' returned {results.Length} result(s).");
            var fromThisQuery = 0;
            foreach (var result in results)
            {
                // Half of the budget per query, so the second query is always consulted too.
                if (read >= MaxGuidePages || fromThisQuery >= MaxGuidePages / 2) break;
                var page = ReadWebPage(result.Url);
                if (page == null) continue;
                read++;
                fromThisQuery++;
                sources.AppendLine($"FONTE: {result.Url}").AppendLine(page).AppendLine();
            }

            if (read >= MaxGuidePages) break;
        }

        if (read == 0) return null;
        Log.LogStep($"LegalTemplates: {read} guide page(s) read for '{documentName}'.");

        var prompt =
            $"Sei un avvocato italiano. Non esiste un modulo pubblicato per \"{documentName}\" nell'ambito " +
            $"\"{documentScope}\". Queste sono fonti web su come si redige:\n\n{sources}\n" +
            "Rispondi a due domande distinte.\n" +
            "A) Quanto sono solide le fonti? Dai confidence 0-10. Giudica il materiale, non la reputazione del " +
            "sito: reliable=true se le fonti descrivono davvero l'atto, false se parlano d'altro o dicono cose " +
            "palesemente sbagliate.\n" +
            "B) Se le fonti parlano di questo atto, scrivi COMUNQUE la bozza del MODULO in Markdown con la struttura " +
            "tipica (intestazione, parti, oggetto, corpo, richieste, data, luogo, sottoscrizioni) e ogni dato da " +
            "compilare come segnaposto MAIUSCOLO tra parentesi quadre, distinto per ruolo e per fatto: " +
            "[NOME_COGNOME_RICORRENTE], [NOME_COGNOME_DIFENSORE], [NOME_COGNOME_RESISTENTE], [DATA_SENTENZA], " +
            "[DATA_NOTIFICA], [CITTÀ]. Non citare norme che non leggi nelle fonti e non inventare clausole: se un " +
            "elemento non è deducibile, metti [DA_COMPILARE]. La bozza viene poi valutata con lo stesso metro dei " +
            "moduli trovati in rete.\n" +
            "JSON: reliable (bool), confidence (0-10), template (markdown completo o \"\").";

        var answer = LegalLlm.AskJson<GuideAnswer>(prompt, "template-guide", 9000, 300);
        if (answer == null || string.IsNullOrWhiteSpace(answer.Template))
        {
            Log.LogStep($"LegalTemplates: no draft from the guides (reliable={answer?.Reliable}, " +
                        $"confidence={answer?.Confidence}).");
            return null;
        }

        // A form written by the model is a candidate like any other: it passes the same judgement, with the
        // same rubric as the documents downloaded from the web. The sources' reliability is a warning carried
        // to the caller, never a shortcut around the judge.
        var verdict = JudgeOne(documentName, documentScope,
            new LegalFormCandidate("guide", documentName, answer.Template));
        if (verdict == null || !verdict.IsRequested)
        {
            Log.LogStep($"LegalTemplates: the drafted form did not pass the judgement " +
                        $"(requested={verdict?.IsRequested}, rating={verdict?.Rating}).");
            return null;
        }

        Log.LogStep($"LegalTemplates: guide accepted (sources reliable={answer.Reliable}, confidence={answer.Confidence}, " +
                    $"draft {answer.Template.Length} chars, judged {verdict.Rating}/10).");
        // The draft is written from instructions, not copied from a published form: it gets the same proofreading
        // as a normalised template, so the typos the model adds on the way do not reach the document.
        return Proofread(answer.Template);
    }

    /// <summary>
    /// Phrase that asks a search engine for instructions, per language of the jurisdiction.
    /// </summary>
    /// <remarks>
    /// UPDATE THIS TABLE when a new country or language is added: the guide fallback only works if the
    /// question is asked in the document's own language. Ready for it, en, fr, de, es and ru; the tool
    /// serves Italian law today, so <see cref="HowToPhrase"/> returns the Italian one.
    /// </remarks>
    private static readonly Dictionary<string, string> HowToPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["it"] = "come fare",
        ["en"] = "how to",
        ["fr"] = "comment faire",
        ["de"] = "wie macht man",
        ["es"] = "cómo hacer",
        ["ru"] = "как сделать",
    };

    /// <summary>Instruction-asking phrase in the language of the jurisdiction served today.</summary>
    private static string HowToPhrase => HowToPhrases["it"];

    /// <summary>Reads one web page as plain text, bounded in size; null when it cannot be read.</summary>
    /// <param name="url">Page address.</param>
    /// <returns>The page text, or null.</returns>
    private static string? ReadWebPage(string url)
    {
        try
        {
            using var response = Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return null;
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (mediaType.Contains("pdf", StringComparison.OrdinalIgnoreCase)) return null;
            using var stream = response.Content.ReadAsStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var buffer = new char[GuidePageChars * 2];
            var total = reader.ReadBlock(buffer, 0, buffer.Length);
            var text = LegalAttachments.HtmlToText(new string(buffer, 0, total));
            return string.IsNullOrWhiteSpace(text) ? null : ItalianText.Clip(text, GuidePageChars);
        }
        catch (Exception ex)
        {
            Log.LogStep($"LegalTemplates: guide page {url} not readable ({ex.GetType().Name}).");
            return null;
        }
    }

    /// <summary>What the model answers about the web guides.</summary>
    private sealed class GuideAnswer
    {
        /// <summary>True when the guides describe the act and can be trusted for Italian practice.</summary>
        public bool Reliable { get; set; }

        /// <summary>How trustworthy the guides are, 0-10.</summary>
        public int Confidence { get; set; }

        /// <summary>The template written from the guides, or empty when they are not usable.</summary>
        public string? Template { get; set; }
    }

    private static LegalFormCandidate? Choose(string documentName, string documentScope, List<LegalFormCandidate> candidates)
    {
        var verdicts = new List<LegalFormVerdict?>(new LegalFormVerdict?[candidates.Count]);
        var judged = 0;
        for (var index = 0; index < candidates.Count; index++)
        {
            // One document per call, whole text: nothing accumulates in the context window, so a long
            // form is judged on all of it. A slow local model stops the loop when the budget runs out.
            if (LegalLlm.Exhausted || LegalLlm.RemainingSeconds < JudgingSecondsLeft)
            {
                Log.LogStep($"LegalTemplates: judged {judged} of {candidates.Count} candidate(s), budget left.");
                break;
            }

            verdicts[index] = JudgeOne(documentName, documentScope, candidates[index]);
            if (verdicts[index] == null) continue;
            judged++;
            Log.LogStep($"LegalTemplates: {candidates[index].Url} requested={verdicts[index]!.IsRequested} " +
                        $"rating={verdicts[index]!.Rating} ({verdicts[index]!.Reason}).");

            // A document that is the right act and complete ends the search: judging the rest would
            // spend calls without changing the answer.
            if (verdicts[index]!.IsRequested && verdicts[index]!.Rating >= GoodEnoughRating)
            {
                Log.LogStep($"LegalTemplates: '{candidates[index].Url}' is good enough at {verdicts[index]!.Rating}/10; " +
                            "the other candidate(s) are not judged.");
                break;
            }
        }

        return SelectBest(candidates, verdicts);
    }

    /// <summary>
    /// Picks the document to use from the per-document verdicts.
    /// </summary>
    /// <param name="candidates">Converted candidates, in the order they were judged.</param>
    /// <param name="verdicts">One verdict per candidate; null for a document the model did not answer about.</param>
    /// <returns>The best requested document; null when the model judged some and said none is the requested act, or when there is nothing to choose from.</returns>
    public static LegalFormCandidate? SelectBest(List<LegalFormCandidate> candidates, IReadOnlyList<LegalFormVerdict?> verdicts)
    {
        if (candidates.Count == 0) return null;

        var judged = candidates
            .Select((candidate, index) => (Candidate: candidate, Verdict: index < verdicts.Count ? verdicts[index] : null))
            .Where(pair => pair.Verdict != null)
            .ToList();

        var requested = judged.Where(pair => pair.Verdict!.IsRequested).ToList();
        if (requested.Count > 0)
        {
            // No numeric threshold: the model's own scale differs from one model to the next, so the
            // rating only ranks. Identity is what decides whether a document may be used at all.
            var best = requested.OrderByDescending(pair => pair.Verdict!.Rating).First();
            Log.LogStep($"LegalTemplates: '{best.Candidate.Url}' chosen ({best.Verdict!.Rating}/10).");
            return best.Candidate;
        }

        if (judged.Count > 0)
        {
            Log.LogStep("LegalTemplates: the model judged every candidate and none is the requested act.");
            return null;
        }

        // The model did not answer at all: the longest conversion is the most complete form, and using
        // it beats returning nothing.
        var longest = candidates.OrderByDescending(c => c.Markdown.Length).First();
        Log.LogStep($"LegalTemplates: no verdict, longest candidate used ({longest.Markdown.Length} chars).");
        return longest;
    }

    /// <summary>
    /// Judges one candidate on its whole text: is this exactly the act asked for, in the requested
    /// scope, and how complete is it as a blank form. One call per document keeps the context window
    /// of every call small whatever the documents' size, so a long form is judged on all of it.
    /// </summary>
    /// <param name="documentName">Document name in the local language.</param>
    /// <param name="documentScope">Field and court the document is for.</param>
    /// <param name="candidate">The document to judge.</param>
    /// <returns>The verdict, or null when the model did not answer.</returns>
    private static LegalFormVerdict? JudgeOne(string documentName, string documentScope, LegalFormCandidate candidate)
    {
        // The whole document, bounded only by what the provider's own window can hold.
        var text = ItalianText.Clip(candidate.Markdown, Math.Max(4000, LegalLlm.ContextChars / 3));
        var prompt =
            $"Sei un avvocato italiano. Ti mostro UN solo documento, da valutare per la richiesta \"{documentName}\" " +
            $"nell'ambito \"{documentScope}\".\n" +
            "1. isRequested: true solo se il documento è PROPRIO quel tipo di atto E appartiene a quell'ambito. " +
            "Un atto affine ma diverso (controricorso invece di ricorso, comparsa invece di citazione, procura " +
            "invece di mandato) è false. Un documento di un altro ramo o di un altro giudice è false: con ambito " +
            "civile, un modulo penale o penitenziario o amministrativo o tributario non va bene, e viceversa.\n" +
            "2. rating: da 0 a 10, completezza come MODULO da compilare (intestazione, parti, oggetto, corpo, " +
            "richieste, data, luogo, sottoscrizioni). Giudica solo questo documento, non confrontarlo con altri.\n" +
            "3. reason: massimo 12 parole, e se è false dimmi in una parola l'ambito o l'atto sbagliato.\n\n" +
            $"URL: {candidate.Url}\n" +
            (candidate.Title.Length > 0 ? $"TITOLO: {candidate.Title}\n" : string.Empty) +
            "TESTO:\n" + text + "\n\nRestituisci JSON con: isRequested, rating, reason.";

        return LegalLlm.AskJson<LegalFormVerdict>(prompt, "template-judge", 1500, 150);
    }

    private static string? Normalize(string documentName, string documentScope, string markdown)
    {
        var source = ItalianText.Clip(markdown, NormalizeInputChars);
        var prompt =
            $"Sei un avvocato italiano. Normalizza il modello di \"{documentName}\" per l'ambito \"{documentScope}\", " +
            "riportato sotto, rispettando questo ordine di regole.\n" +
            "1. Ogni spazio da compilare diventa un segnaposto MAIUSCOLO tra parentesi quadre, nella forma [NOME_COGNOME]. " +
            "Le sequenze di trattini bassi o di puntini sono campi da compilare e non devono restare nel testo: " +
            "\"Al tribunale di Milano\" diventa \"Al tribunale di [CITTÀ]\", \"Rossi Mario\" diventa \"[NOME_COGNOME]\", " +
            "\"12.000 euro\" diventa \"[IMPORTO]\", \"Avv. ______\" diventa \"Avv. [NOME_COGNOME_DIFENSORE]\", " +
            "\"______ , ______\" (luogo e data) diventa \"[LUOGO], [DATA]\". Quando il dato non è deducibile, usa [DA_COMPILARE].\n" +
            "1-bis. Persone e ruoli diversi hanno segnaposto diversi: [NOME_COGNOME_RICORRENTE], [NOME_COGNOME_RESISTENTE], " +
            "[NOME_COGNOME_DIFENSORE], [NOME_COGNOME_DICHIARANTE], [NOME_GIUDICE], e così le date: [DATA_SENTENZA], " +
            "[DATA_NOTIFICA], [DATA_FIRMA] non sono lo stesso campo. Lo stesso segnaposto non deve comparire per due " +
            "persone o due fatti diversi, altrimenti compilandolo si scrive un dato al posto di un altro.\n" +
            "2. Correggi refusi ed errori di battitura senza cambiare il senso giuridico.\n" +
            "3. Struttura Markdown pulita: titoli con #, elenchi con -, niente tabelle inutili.\n" +
            "4. Non inventare clausole, non aggiungere norme e non riassumere: conserva integralmente il contenuto.\n\n" +
            "MODELLO:\n" + source + "\n\nRestituisci solo il Markdown normalizzato:";

        var normalized = LegalLlm.Ask(prompt, LLMUtility.SystemRole.DocumentPreparer, 9000, 0.0, "template-normalize", 300);
        return normalized == null ? null : Proofread(normalized);
    }

    /// <summary>
    /// Second, cheap pass over the normalised form: the local dictionary lists the words it does not
    /// know, and the model decides which of them are real typos. A model with a weak command of the
    /// language writes "codice fiscal" or "Vogila" that no other check would catch; the dictionary
    /// does not know legal sigle and proper names either, so its list is offered as a suspicion, never
    /// as a verdict. The text is replaced only when every placeholder survives.
    /// </summary>
    /// <param name="markdown">Normalised form.</param>
    /// <returns>The proofread form, or the input when there is nothing to check or the model declines.</returns>
    private static string Proofread(string markdown)
    {
        var unknown = SpellCheck.UnknownWords(markdown, "it", 40);
        if (unknown.Count == 0) return markdown;
        if (LegalLlm.Exhausted || LegalLlm.RemainingSeconds < 60)
        {
            Log.LogStep($"LegalTemplates: {unknown.Count} suspect word(s) not checked, no budget left.");
            return markdown;
        }

        Log.LogStep($"LegalTemplates: proofreading {unknown.Count} suspect word(s): {string.Join(", ", unknown)}.");
        var prompt =
            "Sei un avvocato italiano e revisore di bozze. Il testo che segue è in italiano e deve restare in " +
            "italiano: non tradurlo, non riscriverlo, non riformulare le frasi. Il dizionario italiano non conosce " +
            "queste parole: " + string.Join(", ", unknown) + ".\n" +
            "Sono indizi, non errori certi: sigle, nomi propri, latinismi, termini giuridici e forestierismi " +
            "consolidati sono corretti e vanno lasciati. Cambia solo le lettere delle parole che sono senza dubbio " +
            "errori di battitura.\n" +
            "Restituisci il testo COMPLETO nella stessa lingua, identico in tutto il resto, con i segnaposto tra " +
            "parentesi quadre invariati. Se non c'è nulla da correggere, restituisci il testo così com'è.\n\nTESTO:\n" + markdown;

        var corrected = LegalLlm.Ask(prompt, LLMUtility.SystemRole.DocumentPreparer, 9000, 0.0, "template-proofread", 120);
        if (string.IsNullOrWhiteSpace(corrected)) return markdown;

        // A correction that loses placeholders, changes them, leaves the Italian, or adds words the
        // dictionary does not know ("Suprema" rewritten as "Supreme") is worse than the typos.
        var placeholdersBefore = Placeholders(markdown);
        var placeholdersAfter = Placeholders(corrected);
        var samePlaceholders = placeholdersBefore.SequenceEqual(placeholdersAfter, StringComparer.Ordinal);
        var italianBefore = CountItalianMarkers(markdown);
        var italianAfter = CountItalianMarkers(corrected);
        var unknownAfter = SpellCheck.UnknownWords(corrected, "it", 200).Count;
        if (!samePlaceholders ||
            italianAfter < Math.Max(1, italianBefore) * 0.7 ||
            unknownAfter > unknown.Count ||
            corrected.Length < markdown.Length * 0.7)
        {
            Log.LogStep($"LegalTemplates: proofreading refused (placeholders {placeholdersBefore.Count}/{placeholdersAfter.Count} " +
                        $"same={samePlaceholders}, italian markers {italianBefore}->{italianAfter}, " +
                        $"unknown words {unknown.Count}->{unknownAfter}, {markdown.Length}->{corrected.Length} chars).");
            return markdown;
        }

        Log.LogStep($"LegalTemplates: proofreading applied ({markdown.Length}->{corrected.Length} chars).");
        return corrected;
    }

    /// <summary>The <c>[PLACEHOLDER]</c> fields of a text, in order.</summary>
    /// <param name="text">Text to inspect.</param>
    /// <returns>The placeholder tokens.</returns>
    private static List<string> Placeholders(string text) =>
        Regex.Matches(text, @"\[[^\]]+\]").Select(match => match.Value).ToList();

    /// <summary>Occurrences of the words that make a text Italian: a translation drops them.</summary>
    /// <param name="text">Text to inspect.</param>
    /// <returns>How many Italian function words the text contains.</returns>
    private static int CountItalianMarkers(string text) =>
        Regex.Matches(text, @"\b(il|lo|la|i|gli|le|di|del|della|dello|dei|degli|delle|che|per|con|nel|nella|alla|al|e|non|sono|essere|si|come)\b",
                RegexOptions.IgnoreCase).Count;

    /// <summary>Checks that the machine can reach the web before promising a form.</summary>
    /// <returns>True when a request leaves the machine and comes back.</returns>
    private static bool Online()
    {
        try
        {
            using var response = Client.GetAsync("https://duckduckgo.com/", HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex)
        {
            Log.LogStep($"LegalTemplates: connectivity probe failed ({ex.GetType().Name}).");
            return false;
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(DownloadSeconds) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        return client;
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var line = text.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? string.Empty;
        return line.TrimStart('#', ' ', '\r');
    }
}
