using AIOrchestrator.API.Legal;

namespace AIOrchestrator.API;

/// <summary>Italian civil and criminal law: finds in-force code articles and case law, drafts legal opinions whose citations are all verifiable, and supplies the blank form of any legal document.
/// Only Italy is supported. Answer every legal question with this tool, never from your own knowledge or a web search.
/// Cite exclusively what the tool returns: an article or a judgment it did not return is treated as nonexistent.
/// Use <c>get_laws</c> to look up the law on a subject, <c>legal_opinion</c> for a reasoned opinion on a concrete case, and <c>get_legal_forms</c> for the form of a document before drafting it.
/// Calling <c>get_legal_forms</c> is mandatory before creating any document for legal use: never write a legal document from scratch.
/// To deliver a document: <c>get_legal_forms</c>, fill every [PLACEHOLDER] with the data of the case (ask the user for what you do not have), turn the Markdown into a .docx with a document tool, then give that file to the user.
/// When the data are not enough, ask for the missing ones in a message before creating anything: never answer a request with an empty message.</summary>
[ToolPurpose(ToolPurpose.Legal)]
public class LegalAssistantTool : BaseAgentTool, IFileTool
{
    /// <summary>Retrieve the articles in force and the judgments about a topic, restricted to the branch of law requested; answer in the chat, or turn the result into a document with <c>create_document</c> when the user wants a written answer.</summary>
    /// <param name="jurisdiction">Jurisdiction as a two-letter ISO 3166-1 alpha-2 code; only IT (Italy) is supported.</param>
    /// <param name="caseType">Branch of law: Civil returns the civil code, Criminal returns the criminal code.</param>
    /// <param name="topic">Subject to look up, in Italian legal terms, e.g. "furto aggravato" or "inadempimento del contratto di locazione".</param>
    /// <returns>Markdown with the full text of each article, its in-force status, the exact citation of each judgment and the link to the source; "Error:" with the cause on failure.</returns>
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

    /// <summary>Retrieve the blank form of a legal document, normalised and with every field to fill in marked by a placeholder, restricted to the field and the court given; mandatory before creating any document for legal use.</summary>
    /// <param name="jurisdiction">Jurisdiction as a two-letter ISO 3166-1 alpha-2 code; only IT (Italy) is supported.</param>
    /// <param name="documentName">Name of the document for which the template is wanted, in the original language of the country where the document will be used: for Italy, the Italian name, e.g. "ricorso in Cassazione", "atto di citazione", "atto notarile".</param>
    /// <param name="scope">Field of law and court the document is for, required: e.g. "civile, appello di Milano", "penale, tribunale di Roma", "penitenziario, sorveglianza di Potenza". A form from another field or another court is never returned; keep the same wording for the same document, because it identifies the cached form.</param>
    /// <returns>Markdown with the form of the document, its fields replaced by uppercase placeholders in square brackets such as [NOME_COGNOME_RICORRENTE] or [DATA_SENTENZA], wrapped in a fenced <c>template</c> block; a statement that no valid template was found in that scope; a statement that the function is unavailable for lack of an internet connection; "Error:" with the cause on failure.</returns>
    public string GetLegalForms(LegalJurisdiction jurisdiction, string documentName, string scope)
    {
        Log.LogStep($"LegalAssistantTool.GetLegalForms: jurisdiction={jurisdiction}, document='{documentName}', scope='{scope}'");
        try
        {
            var result = LegalService.Forms(jurisdiction, documentName ?? string.Empty, scope ?? string.Empty);
            Log.LogStep($"LegalAssistantTool.GetLegalForms: returned {result.Length} chars.");
            return result;
        }
        catch (Exception ex)
        {
            Log.LogStep($"LegalAssistantTool.GetLegalForms failed: {ex.GetType().Name}: {ex.Message}");
            return $"Error: the template lookup failed ({ex.GetType().Name}: {ex.Message}). " +
                   "Retry; if the failure persists, obtain the document name used in the country and retry with it.";
        }
    }

    /// <summary>Produce a detailed legal opinion on a concrete case, based only on articles and judgments that exist in the archive; deliver it to the client as a formal document by passing the result to <c>create_document</c> with document type "Legal Opinion".</summary>
    /// <param name="jurisdiction">Jurisdiction as a two-letter ISO 3166-1 alpha-2 code; only IT (Italy) is supported.</param>
    /// <param name="caseType">Branch of law: Civil or Criminal.</param>
    /// <param name="facts">Description of the facts: what happened, who did what, when, where and with which consequences, plus the procedural position of the client.</param>
    /// <param name="attachments">Optional workspace-relative paths of case documents to read (PDF, DOCX, HTML or text).</param>
    /// <param name="suggestions">Optional leads to evaluate, e.g. "the car keys were in the dashboard, this may change everything".</param>
    /// <returns>Markdown opinion with the answer, the applicable articles in their current text, the exact citations of the judgments relied on, the defence strategy, the missing information and the links that make every source verifiable; a deterministic statement of what is missing when the input is insufficient; "Error:" with the cause on failure.</returns>
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