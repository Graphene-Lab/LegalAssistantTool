using System.Text.Json.Serialization;

namespace AIOrchestrator.API.Legal;

/// <summary>Legal system the archive belongs to. Only <see cref="IT"/> is served: the archive
/// contains Italian sources only, and a different jurisdiction would return out-of-scope law.</summary>
public enum LegalJurisdiction
{
    /// <summary>Italy (two-letter ISO 3166-1 alpha-2 code). The only supported jurisdiction.</summary>
    IT
}

/// <summary>Branch of law a request belongs to; it selects which code (civil or criminal) is searched.</summary>
public enum CaseType
{
    /// <summary>Civil matter: Codice civile, civil Cassazione schede, Corte costituzionale.</summary>
    Civil,

    /// <summary>Criminal matter: Codice penale, criminal Cassazione schede, Corte costituzionale.</summary>
    Criminal
}

/// <summary>Kind of source document held by the archive.</summary>
public enum LegalDocKind
{
    /// <summary>An article of a code (Codice civile / Codice penale).</summary>
    Article,

    /// <summary>A judgment scheda (Cassazione or Corte costituzionale).</summary>
    Judgment
}

/// <summary>Reliability band assigned to a web page used as complementary context.</summary>
public enum SourceReliability
{
    /// <summary>Unknown or unverifiable publisher: never used as a legal authority.</summary>
    Unknown = 0,

    /// <summary>Generic outlet or blog: usable only as a pointer to the primary source.</summary>
    Low = 1,

    /// <summary>Specialised legal publisher: usable with an explicit caveat.</summary>
    Medium = 2,

    /// <summary>Institutional source (Normattiva, Gazzetta Ufficiale, Corti, Ministero): highest band.</summary>
    High = 3
}

/// <summary>A single retrievable legal document: one code article or one judgment scheda.</summary>
public sealed class LegalDocument
{
    /// <summary>Stable identifier: "art:penale:624" or "jud:Cass_10387_2025".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Article or judgment.</summary>
    public LegalDocKind Kind { get; set; }

    /// <summary>Civil or criminal scope. Null for sources serving both branches (Corte costituzionale).</summary>
    public CaseType? CaseType { get; set; }

    /// <summary>Short title: the article rubric, or the judgment header ("Cass. pen., Sez. VI, n. 10387/2025").</summary>
    public string Heading { get; set; } = string.Empty;

    /// <summary>Full text: the in-force article text, or the scheda body (massima + esito).</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Exact human citation, e.g. "Art. 624 c.p." or "Cass. pen., Sez. VI, n. 10387/2025".</summary>
    public string Citation { get; set; } = string.Empty;

    /// <summary>Archive state code: vigente, abrogato, parzialmente_inapplicabile, dichiarato_incostituzionale.</summary>
    public string StateCode { get; set; } = string.Empty;

    /// <summary>Human explanation of the state, when the archive provides one.</summary>
    public string StateNote { get; set; } = string.Empty;

    /// <summary>True when the archive reports the article as wholly or partly inapplicable.</summary>
    public bool NotApplicable { get; set; }

    /// <summary>Verifiable primary link (Normattiva for articles, official scheda for judgments).</summary>
    public string? Url { get; set; }

    /// <summary>Link to the official PDF of the judgment, when available.</summary>
    public string? PdfUrl { get; set; }

    /// <summary>Court that issued the judgment, e.g. "Corte di cassazione, Sez. VI penale".</summary>
    public string Court { get; set; } = string.Empty;

    /// <summary>Matter (judgments) or rubric (articles).</summary>
    public string Matter { get; set; } = string.Empty;

    /// <summary>Deposit/decision date in ISO form (yyyy-MM-dd), when known.</summary>
    public string? DecisionDate { get; set; }

    /// <summary>Code the article belongs to: "civile" or "penale". Empty for judgments.</summary>
    public string CodeId { get; set; } = string.Empty;

    /// <summary>Article number as published, including suffixes ("624", "3-bis", "648-bis").</summary>
    public string ArticleNumber { get; set; } = string.Empty;

    /// <summary>Section of the code ("Codice Penale", "Dei delitti contro il patrimonio").</summary>
    public string Section { get; set; } = string.Empty;

    /// <summary>Repository the document was normalised from.</summary>
    public string SourceRepo { get; set; } = string.Empty;

    /// <summary>Extra searchable material (judgment outcome in short form, official massima).</summary>
    public string Extra { get; set; } = string.Empty;

    /// <summary>Judgment type from the scheda front matter (sentenza, ordinanza, ordinanza-interlocutoria...).</summary>
    public string JudgmentType { get; set; } = string.Empty;

    /// <summary>Official "relatore"/"redattore" of the judgment, when the scheda reports it.</summary>
    public string Judge { get; set; } = string.Empty;

    /// <summary>True when the document may be cited in an opinion (in force, not repealed).</summary>
    [JsonIgnore]
    public bool IsCitable => Kind == LegalDocKind.Judgment
        || (StateCode is "vigente" or "parzialmente_inapplicabile" or "da_verificare" && !NotApplicable);

    /// <summary>Human label of the state used in rendered output.</summary>
    [JsonIgnore]
    public string StateLabel => StateCode switch
    {
        "vigente" => "in vigore",
        "parzialmente_inapplicabile" => "in vigore (parti non applicabili)",
        "abrogato" => "ABROGATO",
        "dichiarato_incostituzionale" => "DICHIARATO INCOSTITUZIONALE",
        "da_verificare" => "in vigore (da verificare)",
        _ => string.IsNullOrEmpty(StateCode) ? "in vigore" : StateCode
    };

    /// <summary>Concatenation used by the search engine and by the deterministic analysers.</summary>
    [JsonIgnore]
    public string SearchText => string.Join(' ', new[] { Heading, Matter, Citation, Extra, Body }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>True when the document belongs to the requested branch of law.</summary>
    public bool ServesCase(CaseType caseType) => CaseType == null || CaseType == caseType;
}