using AIOrchestrator.API.Legal;

namespace AIOrchestrator.API;

/// <summary>Italian civil and criminal law: finds in-force code articles and case law, and drafts legal opinions whose citations are all verifiable.</summary>
/// <remarks>
/// Only Italy is supported. Answer every legal question with this tool, never from your own knowledge
/// or a web search. Cite exclusively what the tool returns: an article or a judgment it did not return
/// is treated as nonexistent. Use <c>get_laws</c> to look up the law on a subject and
/// <c>legal_opinion</c> for a reasoned opinion on a concrete case.
/// </remarks>
[ToolPurpose(ToolPurpose.Other)]
public class LegalAssistantTool : BaseAgentTool, IFileTool
{
    /// <summary>Retrieve the articles in force and the judgments about a topic, restricted to the branch of law requested.</summary>
    /// <param name="jurisdiction">Jurisdiction as a two-letter ISO 3166-1 alpha-2 code; only IT (Italy) is supported.</param>
    /// <param name="caseType">Branch of law: Civil returns the civil code, Criminal returns the criminal code.</param>
    /// <param name="topic">Subject to look up, in Italian legal terms, e.g. "furto aggravato" or "inadempimento del contratto di locazione".</param>
    /// <returns>Markdown with the full text of each article, its in-force status, the exact citation of each judgment and the link to the source; "Error:" with the cause on failure.</returns>
    /// <remarks>Answer in the chat, or turn the result into a document with <c>create_document</c> when the user wants a written answer.</remarks>
    public string GetLaws(LegalJurisdiction jurisdiction, CaseType caseType, string topic)
    {
        Log.LogStep($"LegalAssistantTool.GetLaws: jurisdiction={jurisdiction}, branch={caseType}, topic='{topic}'");
        try
        {
            var result = LegalService.Laws(jurisdiction, caseType, topic ?? string.Empty);
            Log.LogStep($"LegalAssistantTool.GetLaws: returned {result.Length} chars.");
            return result;
        }
        catch (Exception ex)
        {
            Log.LogStep($"LegalAssistantTool.GetLaws failed: {ex.GetType().Name}: {ex.Message}");
            return $"Error: the legal lookup failed ({ex.GetType().Name}: {ex.Message}). " +
                   "Retry; if the failure persists the local archive may need to be rebuilt.";
        }
    }

    /// <summary>Produce a detailed legal opinion on a concrete case, based only on articles and judgments that exist in the archive.</summary>
    /// <param name="jurisdiction">Jurisdiction as a two-letter ISO 3166-1 alpha-2 code; only IT (Italy) is supported.</param>
    /// <param name="caseType">Branch of law: Civil or Criminal.</param>
    /// <param name="facts">Description of the facts: what happened, who did what, when, where and with which consequences, plus the procedural position of the client.</param>
    /// <param name="attachments">Optional workspace-relative paths of case documents to read (PDF, DOCX, HTML or text).</param>
    /// <param name="suggestions">Optional leads to evaluate, e.g. "the car keys were in the dashboard, this may change everything".</param>
    /// <returns>Markdown opinion with the answer, the applicable articles in their current text, the exact citations of the judgments relied on, the defence strategy, the missing information and the links that make every source verifiable; a deterministic statement of what is missing when the input is insufficient; "Error:" with the cause on failure.</returns>
    /// <remarks>An opinion is meant to be delivered to the client as a formal document: pass the result to <c>create_document</c> with document type "Legal Opinion".</remarks>
    public string LegalOpinion(LegalJurisdiction jurisdiction, CaseType caseType, string facts,
        string[]? attachments = null, string[]? suggestions = null)
    {
        Log.LogStep($"LegalAssistantTool.LegalOpinion: jurisdiction={jurisdiction}, branch={caseType}, " +
                    $"facts={facts?.Length ?? 0} chars, attachments={attachments?.Length ?? 0}, suggestions={suggestions?.Length ?? 0}");
        try
        {
            var result = LegalService.Opinion(jurisdiction, caseType, facts ?? string.Empty, attachments, suggestions);
            Log.LogStep($"LegalAssistantTool.LegalOpinion: returned {result.Length} chars.");
            return result;
        }
        catch (Exception ex)
        {
            Log.LogStep($"LegalAssistantTool.LegalOpinion failed: {ex.GetType().Name}: {ex.Message}");
            return $"Error: the legal opinion could not be produced ({ex.GetType().Name}: {ex.Message}). " +
                   "Retry; if the failure persists the local archive may need to be rebuilt.";
        }
    }
}