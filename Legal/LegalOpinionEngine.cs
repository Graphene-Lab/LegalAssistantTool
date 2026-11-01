using System.Text;
using System.Text.RegularExpressions;

namespace AIOrchestrator.API.Legal;

/// <summary>Result of the deterministic input check.</summary>
/// <param name="Sufficient">True when the input allows a verifiable opinion.</param>
/// <param name="Reason">Deterministic explanation when the input is not sufficient.</param>
/// <param name="Missing">Information that is missing.</param>
/// <param name="Hints">How to complete the request.</param>
internal sealed record LegalInputCheck(bool Sufficient, string Reason, List<string> Missing, List<string> Hints);

/// <summary>
/// Builds a legal opinion. The engine never asks the model to find the law: it retrieves the
/// sources deterministically, gives the model a closed list of citations it is allowed to use, and
/// then deletes any sentence that cites something outside that list. When no model is available the
/// opinion is still produced, written directly from the retrieved texts.
/// </summary>
internal static partial class LegalOpinionEngine
{
    /// <summary>Minimum number of content words required to qualify a case.</summary>
    public const int MinContentTerms = 12;

    /// <summary>Facts shorter than this are flagged as too thin for a reliable opinion.</summary>
    public const int ThinFactsTerms = 28;

    /// <summary>Maximum articles given to the model as citable law.</summary>
    public const int CitableArticles = 10;

    /// <summary>Maximum judgments given to the model as citable case law.</summary>
    public const int CitableJudgments = 6;

    [GeneratedRegex(@"\b\d{1,2}[/.-]\d{1,2}[/.-]\d{2,4}\b|\b(?:19|20)\d{2}\b", RegexOptions.IgnoreCase)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"\b\d+[\d.,]*\s*(?:€|euro|eur)\b|\beuro\s*\d", RegexOptions.IgnoreCase)]
    private static partial Regex AmountPattern();

    [GeneratedRegex(@"\b(attore|convenuto|imputato|indagato|parte offesa|persona offesa|ricorrente|resistente|creditore|debitore|locatore|conduttore|datore di lavoro|dipendente|minore|erede)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex RolePattern();

    [GeneratedRegex(@"\b(indagini|udienza|giudizio|processo|appello|cassazione|tribunale|corte|citazione|ricorso|atto di|sentenza|ordinanza|decreto|mediazione|negoziazione assistita|precetto|pignoramento)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex PhasePattern();

    [GeneratedRegex(@"\b(dolo|colpa|negligenza|imprudenza|imperizia|volont[aà]|errore|mala fede|buona fede|coscienza e volont[aà])\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex MentalElementPattern();

    /// <summary>Checks, without any model, whether the request can support an opinion.</summary>
    /// <param name="caseType">Branch of law.</param>
    /// <param name="facts">Description of the facts.</param>
    /// <param name="attachments">Attachments already read.</param>
    /// <returns>The check result.</returns>
    public static LegalInputCheck CheckInput(CaseType caseType, string facts, IReadOnlyList<LegalAttachment> attachments)
    {
        var missing = new List<string>();
        var hints = new List<string>();
        var terms = ItalianText.Tokenize(facts);
        var readable = attachments.Where(a => a.Text.Length > 0).ToList();

        if (terms.Count < MinContentTerms)
        {
            missing.Add($"descrizione dei fatti troppo breve ({terms.Count} termini utili, minimo {MinContentTerms})");
            hints.Add("Descrivere la vicenda in modo cronologico: chi ha fatto cosa, quando, dove, con quali conseguenze.");
            hints.Add("Indicare il ruolo del cliente (attore, convenuto, imputato, persona offesa).");
            if (readable.Count == 0) hints.Add("Allegare i documenti disponibili (contratti, verbali, referti, atti giudiziari).");
            return new LegalInputCheck(false,
                "La descrizione dei fatti è troppo scarna per individuare la norma applicabile e le pronunce pertinenti. " +
                "Senza questi elementi il parere non sarebbe verificabile.", missing, hints);
        }

        if (caseType == CaseType.Criminal && !RolePattern().IsMatch(facts) && readable.Count == 0)
        {
            missing.Add("posizione processuale del soggetto (indagato, imputato, persona offesa)");
            hints.Add("Precisare se il cliente è indagato, imputato o persona offesa e la fase del procedimento.");
        }

        if (caseType == CaseType.Civil && !RolePattern().IsMatch(facts) && readable.Count == 0)
        {
            missing.Add("ruolo contrattuale o processuale delle parti");
            hints.Add("Precisare se il cliente agisce come attore o convenuto e quale rapporto è controverso.");
        }

        if (!DatePattern().IsMatch(facts) && readable.Count == 0)
            missing.Add("riferimento temporale dei fatti (data o periodo)");

        if (!PhasePattern().IsMatch(facts) && readable.Count == 0)
            missing.Add("fase processuale o stragiudiziale in corso");

        return new LegalInputCheck(true, string.Empty, missing, hints);
    }

    /// <summary>
    /// Produces the Markdown opinion for a case.
    /// </summary>
    /// <param name="caseType">Branch of law.</param>
    /// <param name="facts">Description of the facts.</param>
    /// <param name="attachments">Attachment paths supplied by the caller.</param>
    /// <param name="suggestions">Optional leads to evaluate.</param>
    /// <param name="jurisdiction">Jurisdiction code.</param>
    /// <returns>Markdown document.</returns>
    public static string Opinion(CaseType caseType, string? facts, IEnumerable<string>? attachments,
        IEnumerable<string>? suggestions, string jurisdiction)
    {
        LegalLlm.Reset();
        var factsText = facts ?? string.Empty;
        Log.LogStep($"LegalOpinion: start (jurisdiction={jurisdiction}, branch={caseType}, " +
                    $"facts={factsText.Length} chars, suggestions={(suggestions?.Count() ?? 0)})");

        var suggestionList = (suggestions ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        var attachmentList = LegalAttachments.Read(attachments ?? []);
        var check = CheckInput(caseType, factsText, attachmentList);
        if (!check.Sufficient)
        {
            Log.LogStep($"LegalOpinion: input insufficient — {check.Reason}");
            return LegalMarkdown.RenderInsufficientInput(check.Reason, check.Missing, check.Hints, caseType, jurisdiction);
        }

        var prepared = PrepareAttachments(attachmentList);
        var attachmentText = string.Join("\n\n", prepared.Where(a => a.Text.Length > 0)
            .Select(a => $"ALLEGATO {a.Name}:\n{a.Text}"));
        var suggestionText = string.Join(" ", suggestionList);

        var research = LegalRetriever.Research(caseType, factsText + " " + ItalianText.Clip(attachmentText, 4000),
            [suggestionText], CitableArticles, CitableJudgments, rounds: 3);

        if (research.IsEmpty)
        {
            Log.LogStep("LegalOpinion: no anchor found in the archive.");
            return LegalMarkdown.RenderInsufficientInput(
                "Nessuna norma o sentenza dell'archivio italiano è risultata pertinente ai fatti descritti: " +
                "il tema non è coperto dalle fonti disponibili oppure i termini usati non individuano un istituto giuridico.",
                ["tema giuridico riconoscibile (reato, contratto, istituto, tipo di danno)"],
                ["Riformulare indicando il reato o l'istituto (es. \"furto aggravato\", \"inadempimento del contratto di locazione\").",
                 "Indicare il ramo del diritto: civile o penale."],
                caseType, jurisdiction);
        }

        var webSources = LegalWebEnrichment.Enrich(caseType, factsText, research.Topics,
            research.Articles.Select(a => a.Document).ToList());
        var sections = Draft(caseType, factsText, prepared, research, webSources, suggestionList, out var modelDrafted);

        var missing = new List<string>(check.Missing);
        missing.AddRange(DetectGaps(caseType, factsText, prepared));
        var next = NextSteps(caseType, research, missing);

        var report = new LegalOpinionReport
        {
            Research = research,
            Attachments = attachmentList,
            WebSources = webSources,
            Sections = sections.Sections,
            RemovedSentences = sections.Removed,
            MissingInformation = missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            NextSteps = next,
            ModelDrafted = modelDrafted,
            Suggestions = suggestionList,
        };

        var markdown = LegalMarkdown.RenderOpinion(report, caseType, jurisdiction);
        Log.LogStep($"LegalOpinion: completed ({markdown.Length} chars, model={modelDrafted}, " +
                    $"articles={research.Articles.Count}, judgments={research.Judgments.Count}, " +
                    $"webUsed={webSources.Count(s => s.Used)}, removed={sections.Removed.Count}).");
        return markdown;
    }

    /// <summary>Shrinks the attachments that would not fit in the model context window.</summary>
    /// <param name="attachments">Attachments read from disk.</param>
    /// <returns>Attachments whose text fits the budget.</returns>
    private static List<LegalAttachment> PrepareAttachments(IReadOnlyList<LegalAttachment> attachments)
    {
        var result = new List<LegalAttachment>();
        if (attachments.Count == 0) return result;
        var budget = LegalLlm.AttachmentBudget(attachments.Count(a => a.Text.Length > 0));

        foreach (var attachment in attachments)
        {
            if (attachment.Text.Length == 0)
            {
                result.Add(attachment);
                continue;
            }

            if (attachment.Text.Length <= budget)
            {
                result.Add(attachment);
                continue;
            }

            Log.LogStep($"Attachment {attachment.Name}: {attachment.Text.Length} chars exceeds budget {budget}, summarizing.");
            var summary = LegalLlm.Summarize(attachment.Text, budget, $"attach-{attachment.Name}");
            if (!string.IsNullOrWhiteSpace(summary))
            {
                result.Add(attachment with
                {
                    Text = summary,
                    Note = $"{attachment.Note} — riassunto per rientrare nella finestra di contesto"
                });
                continue;
            }

            // No model available: keep the most informative parts, head and tail.
            var clipped = ItalianText.Clip(attachment.Text, budget);
            result.Add(attachment with
            {
                Text = clipped,
                Note = $"{attachment.Note} — estratto ridotto (riassunto automatico non disponibile)"
            });
            Log.LogStep($"Attachment {attachment.Name}: trimmed deterministically to {clipped.Length} chars.");
        }

        return result;
    }

    /// <summary>Lists the elements that would materially strengthen the opinion.</summary>
    /// <param name="caseType">Branch of law.</param>
    /// <param name="facts">Description of the facts.</param>
    /// <param name="attachments">Attachments already read.</param>
    /// <returns>The missing elements.</returns>
    private static List<string> DetectGaps(CaseType caseType, string facts, IReadOnlyList<LegalAttachment> attachments)
    {
        var gaps = new List<string>();
        var readable = attachments.Any(a => a.Text.Length > 0);
        if (ItalianText.Tokenize(facts).Count < ThinFactsTerms)
            gaps.Add("la descrizione dei fatti è sintetica: ulteriori dettagli cronologici possono cambiare la qualificazione");

        if (!AmountPattern().IsMatch(facts) && !readable)
            gaps.Add("valore economico della controversia o del danno (necessario per competenza e quantificazione)");

        if (caseType == CaseType.Criminal && !MentalElementPattern().IsMatch(facts) && !readable)
            gaps.Add("elemento soggettivo (dolo o colpa) e condotta contestata con precisione");

        if (!readable)
            gaps.Add("documenti probatori: la valutazione si basa solo sul racconto dei fatti");

        return gaps;
    }

    /// <summary>Builds the operational next steps.</summary>
    /// <param name="caseType">Branch of law.</param>
    /// <param name="research">Retrieved sources.</param>
    /// <param name="missing">Missing information.</param>
    /// <returns>The next steps.</returns>
    private static List<string> NextSteps(CaseType caseType, LegalResearch research, IReadOnlyList<string> missing)
    {
        var steps = new List<string>();
        foreach (var point in LegalThesaurus.DefencePoints(research.Topics).Take(6)) steps.Add(point);

        if (missing.Count > 0)
            steps.Add("Raccogliere le informazioni mancanti elencate sopra prima di depositare l'atto.");

        if (research.Judgments.Count > 0)
        {
            var firstJudgment = research.Judgments[0];
            steps.Add($"Leggere il testo integrale di {firstJudgment.Document.Citation} per verificarne la pertinenza al caso concreto.");
        }

        steps.Add("Verificare l'attualità delle norme citate su Normattiva prima dell'udienza: l'archivio è aggiornato " +
                  "all'ultima sincronizzazione e non copre eventuali modifiche successive.");
        steps.Add(caseType == CaseType.Criminal
            ? "Verificare i termini di prescrizione e l'eventuale procedibilità a querela."
            : "Verificare la competenza, il valore della causa e i termini di decadenza o prescrizione.");
        return steps;
    }
    /// <summary>Drafts the narrative sections, falling back to a deterministic text.</summary>
    /// <param name="caseType">Branch of law.</param>
    /// <param name="facts">Description of the facts.</param>
    /// <param name="attachments">Attachments ready for the prompt.</param>
    /// <param name="research">Retrieved sources.</param>
    /// <param name="webSources">Rated web pages.</param>
    /// <param name="suggestions">Leads supplied by the caller.</param>
    /// <param name="modelDrafted">True when the model produced usable prose.</param>
    /// <returns>The sections and the sentences removed by the citation guard.</returns>
    private static (Dictionary<string, string> Sections, List<string> Removed) Draft(CaseType caseType, string facts,
        IReadOnlyList<LegalAttachment> attachments, LegalResearch research, IReadOnlyList<WebSource> webSources,
        IReadOnlyList<string> suggestions, out bool modelDrafted)
    {
        modelDrafted = false;
        var removed = new List<string>();
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var prompt = BuildDraftPrompt(caseType, facts, attachments, research, webSources, suggestions);
        // The draft is the one call that must be given real time, but never the whole budget: the
        // citation audit and the deterministic sections still have to run afterwards.
        var draftTimeout = Math.Min(150, Math.Max(0, LegalLlm.RemainingSeconds - 30));
        var parsed = draftTimeout >= 30
            ? LegalLlm.AskJson<OpinionSections>(prompt, "opinion-draft", 1400, draftTimeout)
            : null;
        if (parsed == null) Log.LogStep("Opinion draft skipped or failed: deterministic sections will be used.");

        if (parsed != null)
        {
            var candidates = new (string Key, string? Text)[]
            {
                ("Sintesi", parsed.Sintesi),
                ("Qualificazione", parsed.Qualificazione),
                ("Strategia", parsed.Strategia),
                ("Rischi", parsed.Rischi),
            };

            var usable = 0;
            foreach (var (key, text) in candidates)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                var audit = LegalCitationGuard.Audit(text, research);
                removed.AddRange(audit.RemovedSentences);
                if (audit.Text.Length < 80)
                {
                    Log.LogStep($"Draft section '{key}' discarded: {audit.Text.Length} chars left after citation guard.");
                    continue;
                }

                sections[key] = audit.Text;
                usable++;
            }

            modelDrafted = usable >= 2;
            Log.LogStep($"Draft: model produced {usable}/4 usable sections, {removed.Count} sentences removed.");
        }
        else
        {
            Log.LogStep("Draft: model unavailable, using the deterministic draft.");
        }

        if (!modelDrafted)
        {
            foreach (var (key, text) in DeterministicSections(caseType, facts, attachments, research, webSources, suggestions))
                sections[key] = text;
        }

        return (sections, removed);
    }

    /// <summary>Builds the prompt that restricts the model to the retrieved citations.</summary>
    /// <param name="caseType">Branch of law.</param>
    /// <param name="facts">Description of the facts.</param>
    /// <param name="attachments">Attachments ready for the prompt.</param>
    /// <param name="research">Retrieved sources.</param>
    /// <param name="webSources">Rated web pages.</param>
    /// <param name="suggestions">Leads supplied by the caller.</param>
    /// <returns>The prompt text.</returns>
    private static string BuildDraftPrompt(CaseType caseType, string facts, IReadOnlyList<LegalAttachment> attachments,
        LegalResearch research, IReadOnlyList<WebSource> webSources, IReadOnlyList<string> suggestions)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Sei un avvocato italiano esperto in " +
            (caseType == CaseType.Criminal ? "diritto penale" : "diritto civile") +
            " e redigi un parere legale scritto per un collega che deve decidere come impostare la difesa.");
        builder.AppendLine();
        builder.AppendLine("VINCOLI ASSOLUTI (violarli rende il parere inutilizzabile):");
        builder.AppendLine("1. Puoi citare esclusivamente le norme e i provvedimenti elencati nelle sezioni NORMATIVA e GIURISPRUDENZA qui sotto.");
        builder.AppendLine("2. Non citare altri articoli, altre sentenze, dottrina, circolari, prassi o riferimenti normativi, nemmeno se li conosci.");
        builder.AppendLine("3. Ogni riferimento va scritto esattamente come indicato (es. \"Art. 624 c.p.\", \"Cass., Sez. V, n. 12345/2023\"). Non aggiungere numeri di sentenza diversi.");
        builder.AppendLine("4. Non inventare fatti, date, importi o documenti: se un elemento manca, dichiaralo esplicitamente.");
        builder.AppendLine("5. Gli approfondimenti esterni servono solo a orientare la strategia e non possono essere citati come norme o sentenze.");
        builder.AppendLine("6. Scrivi in italiano giuridico, in modo concreto e operativo, senza frasi di circostanza.");
        builder.AppendLine();

        builder.AppendLine("NORMATIVA DISPONIBILE:");
        foreach (var article in research.Articles.Take(CitableArticles))
            builder.AppendLine($"- {article.Document.Citation} — {article.Document.Matter}: {ItalianText.Clip(article.Document.Body, 700)}");
        if (research.Articles.Count == 0) builder.AppendLine("- (nessun articolo pertinente individuato)");
        builder.AppendLine();

        builder.AppendLine("GIURISPRUDENZA DISPONIBILE:");
        foreach (var judgment in research.Judgments.Take(CitableJudgments))
        {
            var text = !string.IsNullOrWhiteSpace(judgment.Document.Extra) ? judgment.Document.Extra : judgment.Document.Body;
            builder.AppendLine($"- {judgment.Document.Citation}" +
                (string.IsNullOrWhiteSpace(judgment.Document.Matter) ? string.Empty : $" ({judgment.Document.Matter})") +
                $": {ItalianText.Clip(text, 600)}");
        }

        if (research.Judgments.Count == 0) builder.AppendLine("- (nessuna sentenza pertinente individuata)");
        builder.AppendLine();

        var usedWeb = webSources.Where(s => s.Used).ToList();
        if (usedWeb.Count > 0)
        {
            builder.AppendLine("APPROFONDIMENTI ESTERNI VALUTATI (solo orientamento, non citabili come norme):");
            foreach (var source in usedWeb)
                builder.AppendLine($"- {source.Title} [{source.Url}]: {ItalianText.Clip(source.Excerpt, 500)}");
            builder.AppendLine();
        }

        var defencePoints = LegalThesaurus.DefencePoints(research.Topics).ToList();
        if (defencePoints.Count > 0)
        {
            builder.AppendLine("PISTE DIFENSIVE DA VALUTARE (verifica se sono sostenibili sui fatti, scarta quelle non pertinenti):");
            foreach (var point in defencePoints.Take(10)) builder.AppendLine($"- {point}");
            builder.AppendLine();
        }

        if (suggestions.Count > 0)
        {
            builder.AppendLine("INDICAZIONI DEL RICHIEDENTE DA VALUTARE CON ATTENZIONE:");
            foreach (var suggestion in suggestions) builder.AppendLine($"- {suggestion}");
            builder.AppendLine();
        }

        builder.AppendLine("FATTI:");
        builder.AppendLine(ItalianText.Clip(facts, LegalLlm.ContextChars / 3));
        builder.AppendLine();

        foreach (var attachment in attachments.Where(a => a.Text.Length > 0))
        {
            builder.AppendLine($"ALLEGATO {attachment.Name}:");
            builder.AppendLine(ItalianText.Clip(attachment.Text, Math.Max(2000, LegalLlm.ContextChars / 4)));
            builder.AppendLine();
        }

        builder.AppendLine("Redigi il parere in quattro campi JSON:");
        builder.AppendLine("- sintesi: 120-200 parole con l'esito atteso e le due o tre mosse decisive.");
        builder.AppendLine("- qualificazione: 150-300 parole con la qualificazione giuridica dei fatti, gli elementi costitutivi " +
                         "e la norma che li prevede, indicando quali elementi risultano provati e quali no.");
        builder.AppendLine("- strategia: 200-400 parole con gli argomenti difensivi da presentare al giudice, ciascuno ancorato a una " +
                         "norma o a una sentenza dell'elenco, con l'indicazione della prova da produrre.");
        builder.AppendLine("- rischi: 100-200 parole con le tesi avversarie prevedibili, i punti deboli e come neutralizzarli.");
        return builder.ToString();
    }

    /// <summary>Writes the opinion directly from the archive when no model is available.</summary>
    /// <param name="caseType">Branch of law.</param>
    /// <param name="facts">Description of the facts.</param>
    /// <param name="attachments">Attachments ready for the prompt.</param>
    /// <param name="research">Retrieved sources.</param>
    /// <param name="webSources">Rated web pages.</param>
    /// <param name="suggestions">Leads supplied by the caller.</param>
    /// <returns>The sections.</returns>
    private static List<(string Key, string Text)> DeterministicSections(CaseType caseType, string facts,
        IReadOnlyList<LegalAttachment> attachments, LegalResearch research, IReadOnlyList<WebSource> webSources,
        IReadOnlyList<string> suggestions)
    {
        var result = new List<(string, string)>();
        var topics = research.Topics.Select(t => t.Name).ToList();
        var articleList = research.Articles.Take(5).Select(a => a.Document.Citation).ToList();
        var judgmentList = research.Judgments.Take(4).Select(j => j.Document.Citation).ToList();

        var sintesi = new StringBuilder();
        sintesi.Append(topics.Count > 0
            ? $"Il caso rientra nelle materie di {string.Join(", ", topics)} ({BranchLabel(caseType)}). "
            : $"Il caso è stato inquadrato nel {BranchLabel(caseType)}. ");
        if (articleList.Count > 0)
            sintesi.Append($"Le norme di riferimento sono {string.Join(", ", articleList)}. ");
        if (judgmentList.Count > 0)
            sintesi.Append($"L'archivio contiene {research.Judgments.Count} provvedimenti pertinenti, tra cui {string.Join(", ", judgmentList)}. ");
        if (webSources.Any(s => s.Used))
            sintesi.Append($"Sono stati valutati {webSources.Count(s => s.Used)} approfondimenti esterni attendibili e attuali. ");
        sintesi.Append("La redazione è avvenuta direttamente sui testi dell'archivio: la prosa argomentativa richiede " +
                       "l'intervento del modello linguistico, non disponibile in questa esecuzione.");
        result.Add(("Sintesi", sintesi.ToString()));

        var qualificazione = new StringBuilder();
        qualificazione.AppendLine($"La fattispecie va esaminata alla luce del {BranchLabel(caseType)} italiano. ");
        foreach (var hit in research.Articles.Take(CitableArticles))
        {
            var article = hit.Document;
            qualificazione.AppendLine($"- **{article.Citation}**" +
                (string.IsNullOrWhiteSpace(article.Matter) ? string.Empty : $" ({article.Matter})") +
                $": {ItalianText.Clip(article.Body, 400)}");
        }

        foreach (var topic in research.Topics.Take(3))
            qualificazione.AppendLine($"- Piste indicate dalla materia *{topic.Name}*: {string.Join("; ", topic.DefencePoints.Take(3))}");
        result.Add(("Qualificazione", qualificazione.ToString()));

        var strategia = new StringBuilder();
        var points = LegalThesaurus.DefencePoints(research.Topics).ToList();
        if (points.Count > 0)
        {
            strategia.AppendLine("Argomenti difensivi da sviluppare e verificare sui fatti:");
            foreach (var point in points.Take(8)) strategia.AppendLine($"- {point}");
        }
        else
        {
            strategia.AppendLine("Non sono state individuate piste difensive specifiche: la descrizione dei fatti non " +
                                 "consente di isolare un istituto prevalente.");
        }

        foreach (var suggestion in suggestions)
            strategia.AppendLine($"- Pista suggerita dal richiedente da verificare: {suggestion}");
        foreach (var judgment in research.Judgments.Take(3))
            strategia.AppendLine($"- Confronto con {judgment.Document.Citation}: verificare l'analogia dei fatti prima di citarla in udienza.");
        result.Add(("Strategia", strategia.ToString()));

        var rischi = new StringBuilder();
        rischi.AppendLine("- Le tesi avversarie vanno costruite sugli elementi che il racconto non chiarisce: ogni lacuna è un argomento per la controparte.");
        if (research.Judgments.Count == 0)
            rischi.AppendLine("- Nessuna sentenza pertinente è stata reperita: la difesa dovrà fondarsi sul testo di legge e sulla prova documentale.");
        if (!attachments.Any(a => a.Text.Length > 0))
            rischi.AppendLine("- Nessun documento è stato allegato: ogni affermazione di fatto resta non provata.");
        rischi.AppendLine("- Le norme vanno riverificate su Normattiva prima dell'udienza per escludere modifiche successive all'aggiornamento dell'archivio.");
        result.Add(("Rischi", rischi.ToString()));

        return result;
    }

    private static string BranchLabel(CaseType caseType) =>
        caseType == CaseType.Criminal ? "diritto penale" : "diritto civile";

    /// <summary>Shape of the JSON the model returns.</summary>
    private sealed class OpinionSections
    {
        /// <summary>Executive summary.</summary>
        public string? Sintesi { get; set; }

        /// <summary>Legal qualification of the facts.</summary>
        public string? Qualificazione { get; set; }

        /// <summary>Defence strategy.</summary>
        public string? Strategia { get; set; }

        /// <summary>Weak points and risks.</summary>
        public string? Rischi { get; set; }
    }
}
