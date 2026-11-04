namespace AIOrchestrator.API.Legal;

/// <summary>
/// Entry point shared by the two tool methods: makes sure the archive is ready, validates the
/// jurisdiction, and routes to the retrieval or to the opinion engine.
/// </summary>
internal static class LegalService
{
    /// <summary>
    /// Environment variable that sets how long a call waits for the first synchronisation.
    /// The default covers a cold install on a normal connection (download, normalisation, index):
    /// a shorter wait leaves the archive half-built, and a process that exits mid-sync restarts it.
    /// </summary>
    public const string ReadySecondsVariable = "LEGALASSISTANTTOOL_READY_SECONDS";

    /// <summary>Default wait for the first synchronisation, in seconds.</summary>
    public const int DefaultReadySeconds = 360;

    /// <summary>How long a call waits for the first synchronisation to finish.</summary>
    public static readonly TimeSpan ReadyWait = ResolveReadyWait();

    /// <summary>Waits for the archive and reports whether it can answer.</summary>
    /// <returns>Null when ready, otherwise the error text for the agent.</returns>
    public static string? Prepare()
    {
        var readiness = LegalArchive.EnsureReady(ReadyWait);
        Log.LogStep($"LegalService.Prepare: {readiness.Status} ({readiness.Message})");
        return readiness.Status switch
        {
            ArchiveStatus.Ready => null,
            ArchiveStatus.Preparing =>
                $"Error: the legal archive is still being prepared ({readiness.Message}). " +
                "Retry in a couple of minutes; the first run downloads and indexes the Italian sources.",
            _ =>
                $"Error: the legal archive is unavailable ({readiness.Message}). " +
                "Check the internet connection and retry: the tool needs one successful download of the Italian sources before it can answer."
        };
    }

    private static TimeSpan ResolveReadyWait()
    {
        var raw = Environment.GetEnvironmentVariable(ReadySecondsVariable);
        if (int.TryParse(raw, out var seconds)) return TimeSpan.FromSeconds(Math.Clamp(seconds, 30, 900));
        return TimeSpan.FromSeconds(DefaultReadySeconds);
    }

    /// <summary>Validates the jurisdiction and returns the error text when it is not supported.</summary>
    /// <param name="jurisdiction">Jurisdiction requested by the caller.</param>
    /// <returns>Null when supported, otherwise the error text.</returns>
    public static string? CheckJurisdiction(LegalJurisdiction jurisdiction) =>
        jurisdiction == LegalJurisdiction.IT
            ? null
            : $"Error: jurisdiction '{jurisdiction}' is not supported. Use IT: the archive only contains Italian law.";

    /// <summary>Answers the "laws" method.</summary>
    /// <param name="jurisdiction">Jurisdiction code.</param>
    /// <param name="caseType">Branch of law.</param>
    /// <param name="topic">Topic to search for.</param>
    /// <returns>Markdown document.</returns>
    public static string Laws(LegalJurisdiction jurisdiction, CaseType caseType, string topic)
    {
        Log.LogStep($"LegalService.Laws: jurisdiction={jurisdiction}, branch={caseType}, topic='{ItalianText.Clip(topic, 120)}'");

        var jurisdictionError = CheckJurisdiction(jurisdiction);
        if (jurisdictionError != null) return jurisdictionError;

        if (string.IsNullOrWhiteSpace(topic) || ItalianText.Tokenize(topic).Count == 0)
            return "Error: 'topic' is required. Describe the subject in Italian legal terms, e.g. \"furto aggravato\", " +
                   "\"inadempimento del contratto di locazione\", \"danno da sinistro stradale\".";

        var prepareError = Prepare();
        if (prepareError != null) return prepareError;

        var research = LegalRetriever.Research(caseType, topic, null, maxArticles: 10, maxJudgments: 6, rounds: 2);
        if (research.IsEmpty)
        {
            Log.LogStep("LegalService.Laws: nothing found.");
            return LegalMarkdown.RenderLaws(research, topic, caseType, jurisdiction.ToString());
        }

        var markdown = LegalMarkdown.RenderLaws(research, topic, caseType, jurisdiction.ToString());
        Log.LogStep($"LegalService.Laws: {research.Articles.Count} articles, {research.Judgments.Count} judgments, {markdown.Length} chars.");
        return markdown;
    }

    /// <summary>Answers the "forms" method.</summary>
    /// <param name="jurisdiction">Jurisdiction code.</param>
    /// <param name="documentName">Document name in the language of the country.</param>
    /// <param name="scope">Field and court the document is for.</param>
    /// <returns>The fenced template, or the text explaining why none is available.</returns>
    public static string Forms(LegalJurisdiction jurisdiction, string documentName, string scope)
    {
        Log.LogStep($"LegalService.Forms: jurisdiction={jurisdiction}, document='{ItalianText.Clip(documentName, 80)}', " +
                    $"scope='{ItalianText.Clip(scope, 80)}'");

        var jurisdictionError = CheckJurisdiction(jurisdiction);
        if (jurisdictionError != null) return jurisdictionError;

        return LegalTemplates.Get(jurisdiction, documentName ?? string.Empty, scope ?? string.Empty);
    }

    /// <summary>Answers the "opinion" method.</summary>
    /// <param name="jurisdiction">Jurisdiction code.</param>
    /// <param name="caseType">Branch of law.</param>
    /// <param name="facts">Description of the facts.</param>
    /// <param name="attachments">Attachment paths, if any.</param>
    /// <param name="suggestions">Optional leads to evaluate.</param>
    /// <returns>Markdown document.</returns>
    public static string Opinion(LegalJurisdiction jurisdiction, CaseType caseType, string facts,
        string[]? attachments, string[]? suggestions)
    {
        Log.LogStep($"LegalService.Opinion: jurisdiction={jurisdiction}, branch={caseType}, " +
                    $"facts={facts?.Length ?? 0} chars, attachments={attachments?.Length ?? 0}, suggestions={suggestions?.Length ?? 0}");

        var jurisdictionError = CheckJurisdiction(jurisdiction);
        if (jurisdictionError != null) return jurisdictionError;

        if (string.IsNullOrWhiteSpace(facts))
            return LegalMarkdown.RenderInsufficientInput(
                "La descrizione dei fatti è vuota: non è possibile individuare la norma applicabile.",
                ["descrizione dei fatti"],
                ["Descrivere la vicenda in ordine cronologico indicando soggetti, condotte, date e conseguenze.",
                 "Allegare i documenti disponibili con il parametro 'attachments'."],
                caseType, jurisdiction.ToString());

        var prepareError = Prepare();
        if (prepareError != null) return prepareError;

        return LegalOpinionEngine.Opinion(caseType, facts, attachments, suggestions, jurisdiction.ToString());
    }
}