namespace LegalAssistantToolBenchmark;

using AIOrchestrator.API.Legal;

/// <summary>One evaluation query with the norms a competent Italian lawyer would open first.</summary>
internal sealed class GoldQuery
{
    /// <summary>Short identifier used in the report.</summary>
    public required string Id { get; init; }

    /// <summary>Branch of law.</summary>
    public required CaseType CaseType { get; init; }

    /// <summary>Query text, written the way an agent would write it.</summary>
    public required string Query { get; init; }

    /// <summary>Relevant article numbers of the code of the branch.</summary>
    public required string[] Articles { get; init; }
}

/// <summary>
/// The evaluation set. The relevant articles are hand-picked from the Italian civil and criminal
/// codes (they are the norms a lawyer opens for that fact pattern), never taken from the thesaurus:
/// using the curated topic list as ground truth would reward the hybrid engine for its own prior.
/// The runner drops the numbers the archive does not contain and reports them, so the set stays
/// honest against the actual corpus.
/// </summary>
internal static class GoldSet
{
    /// <summary>All evaluation queries.</summary>
    public static readonly GoldQuery[] Queries =
    [
        new()
        {
            Id = "pen-furto-auto", CaseType = CaseType.Criminal,
            Query = "furto di autovettura parcheggiata in strada durante la notte",
            Articles = ["624", "625"],
        },
        new()
        {
            Id = "pen-rapina", CaseType = CaseType.Criminal,
            Query = "rapina in banca con minaccia armata e sottrazione del denaro",
            Articles = ["628", "629"],
        },
        new()
        {
            Id = "pen-truffa-online", CaseType = CaseType.Criminal,
            Query = "truffa su acquisto online con pagamento eseguito e merce mai consegnata",
            Articles = ["640", "641"],
        },
        new()
        {
            Id = "pen-appropriazione", CaseType = CaseType.Criminal,
            Query = "appropriazione indebita di somme di denaro affidate per un incarico",
            Articles = ["646"],
        },
        new()
        {
            Id = "pen-lesioni-rissa", CaseType = CaseType.Criminal,
            Query = "lesioni personali gravi provocate durante una rissa",
            Articles = ["582", "583", "588"],
        },
        new()
        {
            Id = "pen-omicidio-stradale", CaseType = CaseType.Criminal,
            Query = "omicidio colposo causato da un incidente stradale con violazione del codice della strada",
            Articles = ["589"],
        },
        new()
        {
            Id = "pen-stalking", CaseType = CaseType.Criminal,
            Query = "atti persecutori e stalking nei confronti dell'ex coniuge",
            Articles = ["612-bis", "612"],
        },
        new()
        {
            Id = "pen-diffamazione", CaseType = CaseType.Criminal,
            Query = "diffamazione a mezzo stampa e su social network",
            Articles = ["595"],
        },
        new()
        {
            Id = "pen-ricettazione", CaseType = CaseType.Criminal,
            Query = "ricettazione di merce di provenienza furtiva acquistata a prezzo vile",
            Articles = ["648"],
        },
        new()
        {
            Id = "pen-associazione", CaseType = CaseType.Criminal,
            Query = "associazione per delinquere finalizzata a reati contro il patrimonio",
            Articles = ["416", "416-bis"],
        },
        new()
        {
            Id = "pen-danneggiamento", CaseType = CaseType.Criminal,
            Query = "danneggiamento di autovettura parcheggiata con rigature alla carrozzeria",
            Articles = ["635"],
        },
        new()
        {
            Id = "pen-minaccia", CaseType = CaseType.Criminal,
            Query = "minaccia grave con arma per ottenere la consegna di denaro",
            Articles = ["612", "629"],
        },
        new()
        {
            Id = "pen-violenza-privata", CaseType = CaseType.Criminal,
            Query = "violenza privata e costrizione della persona a subire un atto contro la volontà",
            Articles = ["610", "611"],
        },
        new()
        {
            Id = "pen-usura", CaseType = CaseType.Criminal,
            Query = "usura con interessi oltre la soglia legale e approfittamento dello stato di bisogno",
            Articles = ["644"],
        },
        new()
        {
            Id = "pen-furto-abitazione", CaseType = CaseType.Criminal,
            Query = "furto in abitazione mediante effrazione della porta d'ingresso",
            Articles = ["624-bis", "625"],
        },
        new()
        {
            Id = "pen-calunnia", CaseType = CaseType.Criminal,
            Query = "calunnia con denuncia di un reato mai avvenuto a carico di un innocente",
            Articles = ["368"],
        },
        new()
        {
            Id = "pen-falso-atto", CaseType = CaseType.Criminal,
            Query = "falso in atto pubblico materialmente formato dal pubblico ufficiale",
            Articles = ["476", "479"],
        },
        new()
        {
            Id = "pen-corruzione", CaseType = CaseType.Criminal,
            Query = "corruzione del pubblico ufficiale per ottenere l'aggiudicazione di un appalto",
            Articles = ["318", "319", "321"],
        },
        new()
        {
            Id = "pen-peculato", CaseType = CaseType.Criminal,
            Query = "peculato mediante appropriazione di denaro pubblico posseduto per ragioni d'ufficio",
            Articles = ["314", "316"],
        },
        new()
        {
            Id = "pen-maltrattamenti", CaseType = CaseType.Criminal,
            Query = "maltrattamenti in famiglia e violenza domestica abituale",
            Articles = ["572", "582"],
        },
        new()
        {
            Id = "civ-locazione", CaseType = CaseType.Civil,
            Query = "mancato pagamento del canone di locazione e risoluzione del contratto",
            Articles = ["1587", "1453", "1455"],
        },
        new()
        {
            Id = "civ-custodia", CaseType = CaseType.Civil,
            Query = "danno causato da una cosa in custodia caduta dal balcone",
            Articles = ["2051", "2052", "2043"],
        },
        new()
        {
            Id = "civ-sinistro", CaseType = CaseType.Civil,
            Query = "risarcimento del danno da sinistro stradale con colpa del conducente",
            Articles = ["2054", "2043", "2056"],
        },
        new()
        {
            Id = "civ-inadempimento", CaseType = CaseType.Civil,
            Query = "inadempimento del debitore e risarcimento del danno per perdita subita",
            Articles = ["1218", "1219", "1223"],
        },
        new()
        {
            Id = "civ-vizi", CaseType = CaseType.Civil,
            Query = "vizi della cosa venduta e garanzia nella compravendita",
            Articles = ["1490", "1492", "1495"],
        },
        new()
        {
            Id = "civ-separazione", CaseType = CaseType.Civil,
            Query = "separazione personale dei coniugi e addebito della separazione",
            Articles = ["150", "151"],
        },
        new()
        {
            Id = "civ-successione", CaseType = CaseType.Civil,
            Query = "successione legittima e quota spettante ai figli",
            Articles = ["565", "566", "581"],
        },
        new()
        {
            Id = "civ-usucapione", CaseType = CaseType.Civil,
            Query = "usucapione ventennale di un immobile posseduto in modo continuato",
            Articles = ["1158", "1159", "1161"],
        },
        new()
        {
            Id = "civ-servitu", CaseType = CaseType.Civil,
            Query = "servitù di passaggio sul fondo del vicino costituita per destinazione del padre di famiglia",
            Articles = ["1051", "1053", "1054"],
        },
        new()
        {
            Id = "civ-licenziamento", CaseType = CaseType.Civil,
            Query = "licenziamento per giustificato motivo oggettivo e diritti del lavoratore",
            Articles = ["2118", "2119", "2103"],
        },
        new()
        {
            Id = "civ-mediazione", CaseType = CaseType.Civil,
            Query = "mediazione immobiliare e diritto del mediatore alla provvigione",
            Articles = ["1754", "1755"],
        },
        new()
        {
            Id = "civ-prescrizione", CaseType = CaseType.Civil,
            Query = "prescrizione del diritto al risarcimento del danno da fatto illecito",
            Articles = ["2934", "2946", "2947"],
        },
        new()
        {
            Id = "civ-danno-biologico", CaseType = CaseType.Civil,
            Query = "danno biologico e danno morale da lesione della salute",
            Articles = ["2043", "2059", "1226"],
        },
        new()
        {
            Id = "civ-clausole", CaseType = CaseType.Civil,
            Query = "clausole vessatorie e condizioni generali di contratto predisposte dal contraente forte",
            Articles = ["1341", "1342", "1370"],
        },
        new()
        {
            Id = "civ-ipoteca", CaseType = CaseType.Civil,
            Query = "ipoteca iscritta a garanzia del mutuo e suo effetto verso i terzi acquirenti",
            Articles = ["2808", "2810", "2852"],
        },
    ];
}
