using System.Text.RegularExpressions;
using System.Text;

namespace AIOrchestrator.API.Legal;

/// <summary>Everything the opinion renderer needs, already verified against the archive.</summary>
internal sealed class LegalOpinionReport
{
    /// <summary>Sources retrieved for the case.</summary>
    public required LegalResearch Research { get; init; }

    /// <summary>Attachments that were read.</summary>
    public List<LegalAttachment> Attachments { get; init; } = [];

    /// <summary>Web pages that were rated.</summary>
    public List<WebSource> WebSources { get; init; } = [];

    /// <summary>Narrative sections drafted by the model and validated, keyed by section name.</summary>
    public Dictionary<string, string> Sections { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sentences removed because they cited sources outside the archive.</summary>
    public List<string> RemovedSentences { get; init; } = [];

    /// <summary>Information the tool needs to strengthen the opinion.</summary>
    public List<string> MissingInformation { get; init; } = [];

    /// <summary>Concrete next steps for the lawyer.</summary>
    public List<string> NextSteps { get; init; } = [];

    /// <summary>True when the model contributed prose; false when the deterministic draft was used.</summary>
    public bool ModelDrafted { get; init; }

    /// <summary>Suggestions supplied by the caller that were taken into account.</summary>
    public List<string> Suggestions { get; init; } = [];
}

/// <summary>
/// Renders the two tool answers as Markdown. Citations, article texts and links are always emitted
/// by this code from the archive records, never by the model, so every reference in the output can
/// be checked against the source repository.
/// </summary>
internal static class LegalMarkdown
{
    /// <summary>Renders the answer of the "laws" method.</summary>
    /// <param name="research">Retrieved sources.</param>
    /// <param name="topic">Topic requested by the caller.</param>
    /// <param name="caseType">Branch of law.</param>
    /// <param name="jurisdiction">Jurisdiction code.</param>
    /// <returns>Markdown document.</returns>
    public static string RenderLaws(LegalResearch research, string topic, CaseType caseType, string jurisdiction)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Normativa e giurisprudenza — {Clean(topic)}");
        builder.AppendLine();
        builder.AppendLine($"> **Giurisdizione:** {jurisdiction} · **Ramo:** {BranchLabel(caseType)} · " +
                           $"**Consultazione:** {DateTime.Now:dd/MM/yyyy HH:mm}");
        builder.AppendLine($"> **Archivio:** {ArchiveSummary()}");
        if (research.Topics.Count > 0)
            builder.AppendLine($"> **Materie riconosciute:** {string.Join(", ", research.Topics.Select(t => t.Name))}");
        builder.AppendLine();

        if (research.IsEmpty)
        {
            builder.AppendLine("## Nessun risultato");
            builder.AppendLine();
            builder.AppendLine("L'archivio non contiene norme o sentenze pertinenti ai termini indicati. " +
                               "Riformulare il tema con termini giuridici più specifici (ad esempio " +
                               "\"furto aggravato\", \"inadempimento contrattuale\", \"danno da sinistro stradale\").");
            return builder.ToString();
        }

        AppendArticles(builder, research.Articles, "## Articoli di legge in vigore");
        AppendJudgments(builder, research.Judgments, "## Sentenze e provvedimenti");
        AppendSources(builder, research);
        return builder.ToString();
    }

    /// <summary>Renders the answer of the "opinion" method.</summary>
    /// <param name="report">Verified report.</param>
    /// <param name="caseType">Branch of law.</param>
    /// <param name="jurisdiction">Jurisdiction code.</param>
    /// <returns>Markdown document.</returns>
    public static string RenderOpinion(LegalOpinionReport report, CaseType caseType, string jurisdiction)
    {
        var research = report.Research;
        var builder = new StringBuilder();
        var title = research.Topics.Count > 0 ? research.Topics[0].Name : "Caso sottoposto";
        builder.AppendLine($"# Parere legale — {Clean(title)}");
        builder.AppendLine();
        builder.AppendLine($"> **Giurisdizione:** {jurisdiction} · **Ramo:** {BranchLabel(caseType)} · " +
                           $"**Redatto il:** {DateTime.Now:dd/MM/yyyy HH:mm}");
        builder.AppendLine($"> **Archivio:** {ArchiveSummary()}");
        builder.AppendLine($"> **Fonti applicate:** {research.Articles.Count} articoli, " +
                           $"{research.Judgments.Count} provvedimenti" +
                           (report.WebSources.Any(s => s.Used)
                               ? $", {report.WebSources.Count(s => s.Used)} approfondimenti esterni"
                               : string.Empty));
        builder.AppendLine($"> **Modalità di redazione:** {(report.ModelDrafted
            ? "prosa assistita dal modello, citazioni selezionate e verificate dall'archivio"
            : "redazione deterministica dai testi di legge e dalle sentenze in archivio")}");
        builder.AppendLine();

        AppendSection(builder, "1. Sintesi del parere", report.Sections, "Sintesi");
        AppendSection(builder, "2. Qualificazione giuridica dei fatti", report.Sections, "Qualificazione");

        if (report.Suggestions.Count > 0)
        {
            builder.AppendLine("## 3. Piste indicate dal richiedente");
            builder.AppendLine();
            foreach (var suggestion in report.Suggestions) builder.AppendLine($"- {Clean(suggestion)}");
            builder.AppendLine();
        }

        AppendArticles(builder, research.Articles, "## 4. Norme applicabili (testo vigente)");
        AppendJudgments(builder, research.Judgments, "## 5. Giurisprudenza di riferimento");
        AppendSection(builder, "6. Strategia difensiva", report.Sections, "Strategia");
        AppendSection(builder, "7. Punti di debolezza e rischi", report.Sections, "Rischi");

        if (report.WebSources.Count > 0)
        {
            builder.AppendLine("## 8. Approfondimenti esterni valutati");
            builder.AppendLine();
            builder.AppendLine("| Fonte | Utilità | Attendibilità | Stato | Esito |");
            builder.AppendLine("|---|---|---|---|---|");
            foreach (var source in report.WebSources)
            {
                var status = source.Obsolete ? "superata" : "attuale";
                var outcome = source.Used ? "utilizzata" : "scartata";
                builder.AppendLine($"| [{Clean(source.Title)}]({source.Url}) | {source.Usefulness}/10 | " +
                                   $"{ReliabilityLabel(source.Reliability)} | {status} | {outcome} |");
            }

            builder.AppendLine();
            foreach (var source in report.WebSources.Where(s => s.Used))
            {
                builder.AppendLine($"- **{Clean(source.Title)}** — {Clean(source.Assessment)} " +
                                   $"([fonte]({source.Url}))");
            }

            builder.AppendLine();
            builder.AppendLine("> Gli approfondimenti esterni servono solo a orientare la strategia: le norme e le " +
                               "sentenze citate nel parere provengono esclusivamente dall'archivio verificabile.");
            builder.AppendLine();
        }

        if (report.MissingInformation.Count > 0)
        {
            builder.AppendLine("## 9. Dati mancanti");
            builder.AppendLine();
            foreach (var item in report.MissingInformation) builder.AppendLine($"- {Clean(item)}");
            builder.AppendLine();
        }

        if (report.NextSteps.Count > 0)
        {
            builder.AppendLine("## 10. Azioni consigliate");
            builder.AppendLine();
            foreach (var item in report.NextSteps) builder.AppendLine($"- {Clean(item)}");
            builder.AppendLine();
        }

        if (report.Attachments.Count > 0)
        {
            builder.AppendLine("## 11. Allegati esaminati");
            builder.AppendLine();
            foreach (var attachment in report.Attachments)
                builder.AppendLine($"- **{Clean(attachment.Name)}** — {Clean(attachment.Note)}" +
                                   (attachment.Text.Length > 0 ? $" ({attachment.Text.Length} caratteri letti)" : string.Empty));
            builder.AppendLine();
        }

        if (report.RemovedSentences.Count > 0)
        {
            builder.AppendLine("## 12. Controllo delle citazioni");
            builder.AppendLine();
            builder.AppendLine($"Sono stati rimossi {report.RemovedSentences.Count} passaggi che citavano " +
                               "riferimenti non presenti nell'archivio, per evitare citazioni inventate.");
            builder.AppendLine();
        }

        AppendSources(builder, research);
        return builder.ToString();
    }

    /// <summary>Renders the deterministic answer used when the input is not sufficient.</summary>
    /// <param name="reason">Why the input is not sufficient.</param>
    /// <param name="missing">What is missing.</param>
    /// <param name="hints">How to fix the request.</param>
    /// <param name="caseType">Branch of law, or null when unknown.</param>
    /// <param name="jurisdiction">Jurisdiction code.</param>
    /// <returns>Markdown document.</returns>
    public static string RenderInsufficientInput(string reason, IReadOnlyList<string> missing,
        IReadOnlyList<string> hints, CaseType? caseType, string jurisdiction)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Dati insufficienti per il parere legale");
        builder.AppendLine();
        builder.AppendLine($"> **Giurisdizione:** {jurisdiction} · **Ramo:** " +
                           $"{(caseType == null ? "non determinato" : BranchLabel(caseType.Value))}");
        builder.AppendLine();
        builder.AppendLine("## Esito deterministico");
        builder.AppendLine();
        builder.AppendLine(reason);
        builder.AppendLine();
        if (missing.Count > 0)
        {
            builder.AppendLine("## Informazioni mancanti");
            builder.AppendLine();
            foreach (var item in missing) builder.AppendLine($"- {item}");
            builder.AppendLine();
        }

        if (hints.Count > 0)
        {
            builder.AppendLine("## Come completare la richiesta");
            builder.AppendLine();
            foreach (var item in hints) builder.AppendLine($"- {item}");
            builder.AppendLine();
        }

        builder.AppendLine("Nessun parere è stato prodotto: senza questi elementi qualsiasi conclusione sarebbe " +
                           "non verificabile e potrebbe indurre in errore.");
        return builder.ToString();
    }

    private static void AppendSection(StringBuilder builder, string heading, Dictionary<string, string> sections,
        string key)
    {
        if (!sections.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text)) return;
        builder.AppendLine($"## {heading}");
        builder.AppendLine();
        builder.AppendLine(text.Trim());
        builder.AppendLine();
    }

    private static void AppendArticles(StringBuilder builder, IReadOnlyList<LegalHit> articles, string heading)
    {
        if (articles.Count == 0) return;
        builder.AppendLine(heading);
        builder.AppendLine();
        foreach (var hit in articles)
        {
            var article = hit.Document;
            builder.AppendLine($"### {Clean(article.Heading)}");
            builder.AppendLine();
            if (!string.IsNullOrWhiteSpace(article.Section))
            {
                builder.AppendLine($"*{Clean(article.Section)}*");
                builder.AppendLine();
            }

            builder.AppendLine("> " + Blockquote(article.Body));
            builder.AppendLine();
            builder.AppendLine($"**Stato:** {Clean(article.StateLabel)}" +
                               (string.IsNullOrWhiteSpace(article.StateNote) ? string.Empty : $" — {Clean(article.StateNote)}"));
            if (!string.IsNullOrWhiteSpace(article.Url))
                builder.AppendLine($"**Fonte:** [testo vigente su Normattiva]({article.Url})");
            builder.AppendLine();
        }
    }

    private static void AppendJudgments(StringBuilder builder, IReadOnlyList<LegalHit> judgments, string heading)
    {
        if (judgments.Count == 0) return;
        builder.AppendLine(heading);
        builder.AppendLine();
        foreach (var hit in judgments)
        {
            var judgment = hit.Document;
            builder.AppendLine($"### {Clean(judgment.Citation)}");
            builder.AppendLine();
            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(judgment.Court)) meta.Add($"**Autorità:** {Clean(judgment.Court)}");
            if (!string.IsNullOrWhiteSpace(judgment.DecisionDate)) meta.Add($"**Data:** {Clean(judgment.DecisionDate)}");
            if (!string.IsNullOrWhiteSpace(judgment.Matter)) meta.Add($"**Materia:** {Clean(judgment.Matter)}");
            if (!string.IsNullOrWhiteSpace(judgment.JudgmentType)) meta.Add($"**Tipo:** {Clean(HumanizeType(judgment.JudgmentType))}");
            if (meta.Count > 0)
            {
                builder.AppendLine(string.Join(" · ", meta));
                builder.AppendLine();
            }

            var body = !string.IsNullOrWhiteSpace(judgment.Extra) ? judgment.Extra : judgment.Body;
            builder.AppendLine("> " + Blockquote(ItalianText.Clip(DemoteHeadings(body), 2200)));
            builder.AppendLine();
            var links = new List<string>();
            if (!string.IsNullOrWhiteSpace(judgment.Url)) links.Add($"[scheda ufficiale]({judgment.Url})");
            if (!string.IsNullOrWhiteSpace(judgment.PdfUrl)) links.Add($"[testo integrale PDF]({judgment.PdfUrl})");
            if (links.Count > 0) builder.AppendLine($"**Fonte:** {string.Join(" · ", links)}");
            builder.AppendLine();
        }
    }

    private static void AppendSources(StringBuilder builder, LegalResearch research)
    {
        builder.AppendLine("## Fonti e verificabilità");
        builder.AppendLine();
        builder.AppendLine("- Articoli di legge: [leggi-italiane](https://github.com/Andrea-Bruno/leggi-italiane), " +
                           "testi allineati a Normattiva.");
        builder.AppendLine("- Sentenze e massime: [giurisprudenza-db](https://github.com/Synthos-Logic/giurisprudenza-db), " +
                           "schede CC BY 4.0 — Synthos Logic; i testi dei provvedimenti sono atti ufficiali dello Stato.");
        builder.AppendLine($"- Archivio locale: {ArchiveSummary()}.");
        builder.AppendLine();
        builder.AppendLine("Ogni articolo e ogni sentenza sopra riportati sono copiati dall'archivio: il testo non è " +
                           "stato riscritto né integrato. Le sentenze sono citate con numero e anno e collegate alla " +
                           "scheda originale, così da poter essere verificate una per una.");
        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine("*Il presente documento è uno strumento di supporto all'analisi giuridica e non sostituisce " +
                           "il parere di un avvocato abilitato.*");
    }

    private static string ArchiveSummary()
    {
        var state = LegalStorage.ReadState();
        var updated = state.LastSyncUtc.HasValue
            ? state.LastSyncUtc.Value.ToLocalTime().ToString("dd/MM/yyyy")
            : "mai";
        return $"{state.ArticleCount} articoli, {state.JudgmentCount} provvedimenti · aggiornato al {updated}";
    }

    /// <summary>
    /// Massime are stored with their own Markdown headings ("### Massima 1"); inside a blockquote a
    /// heading would break the layout, so heading markers become bold labels.
    /// </summary>
    /// <param name="text">Body text.</param>
    /// <returns>Text without Markdown headings.</returns>
    private static string DemoteHeadings(string text) =>
        Regex.Replace(text, @"(?m)^[ \t]*#{1,6}[ \t]*(.+)$", "**$1**");

    /// <summary>Humanises the machine type of a scheda ("questione-su" becomes "questione SU").</summary>
    /// <param name="type">Raw type.</param>
    /// <returns>Readable label.</returns>
    private static string HumanizeType(string type) => type.Replace("-", " ", StringComparison.Ordinal)
        .Replace("questione su", "questione SU", StringComparison.OrdinalIgnoreCase);
    private static string Blockquote(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join("\n> ", lines.Select(l => l.Trim()));
    }

    private static string Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : ItalianText.CollapseWhitespace(text);

    private static string BranchLabel(CaseType caseType) =>
        caseType == CaseType.Criminal ? "diritto penale" : "diritto civile";

    private static string ReliabilityLabel(SourceReliability reliability) => reliability switch
    {
        SourceReliability.High => "alta",
        SourceReliability.Medium => "media",
        SourceReliability.Low => "bassa",
        _ => "non nota"
    };
}
