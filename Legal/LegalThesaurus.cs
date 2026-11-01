namespace AIOrchestrator.API.Legal;

/// <summary>
/// A legal topic: the vocabulary bridge between a colloquial fact description and the formal
/// language of codes and judgments, plus the curated article set and the defence checklist a
/// lawyer would work through for that kind of case.
/// Every article number below was verified against the archived codes (it exists and is in force).
/// </summary>
public sealed class LegalTopic
{
    /// <summary>Stable topic id (kebab-case).</summary>
    public required string Id { get; init; }

    /// <summary>Human topic name (Italian), used in rendered output.</summary>
    public required string Name { get; init; }

    /// <summary>Branch of law the topic belongs to; null when it applies to both.</summary>
    public CaseType? Branch { get; init; }

    /// <summary>Words or phrases (normalised) that activate the topic.</summary>
    public required string[] Triggers { get; init; }

    /// <summary>Formal terms added to the query to reach the code language.</summary>
    public required string[] Expand { get; init; }

    /// <summary>Curated criminal-code articles for this topic.</summary>
    public string[] CriminalArticles { get; init; } = Array.Empty<string>();

    /// <summary>Curated civil-code articles for this topic.</summary>
    public string[] CivilArticles { get; init; } = Array.Empty<string>();

    /// <summary>Elements that must be proven, and the angles that can win the case.</summary>
    public required string[] DefencePoints { get; init; }

    /// <summary>Article numbers for the requested branch, if any.</summary>
    public string[] ArticlesFor(CaseType caseType) => caseType == Legal.CaseType.Criminal ? CriminalArticles : CivilArticles;
}

/// <summary>
/// Deterministic Italian legal thesaurus: topic detection, query expansion and the curated
/// article/defence knowledge that turns a colloquial fact pattern into code vocabulary.
/// No LLM is involved, so the same input always produces the same expansion.
/// </summary>
public static class LegalThesaurus
{
    /// <summary>All known topics.</summary>
    public static readonly LegalTopic[] Topics = Build();

    /// <summary>Topics matching the text, best first (empty when nothing matches).</summary>
    public static List<LegalTopic> Match(CaseType caseType, string text)
    {
        var tokens = ItalianText.TermSet(text);
        var normalized = ItalianText.Normalize(text);
        var scored = new List<(LegalTopic Topic, double Score)>();
        foreach (var topic in Topics)
        {
            if (topic.Branch is { } scoped && scoped != caseType) continue;
            var hits = 0;
            foreach (var trigger in topic.Triggers)
            {
                var t = ItalianText.Normalize(trigger);
                if (t.Contains(' '))
                {
                    if (normalized.Contains(t, StringComparison.Ordinal)) hits += 2;
                    continue;
                }
                if (tokens.Contains(ItalianText.Stem(t))) hits += 2;
            }
            if (hits > 0) scored.Add((topic, hits));
        }
        return scored.OrderByDescending(s => s.Score).Select(s => s.Topic).ToList();
    }

    /// <summary>Formal terms added to the query for the given topics (deduplicated).</summary>
    public static List<string> ExpansionTerms(IEnumerable<LegalTopic> topics)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var topic in topics)
            foreach (var term in topic.Expand)
                if (seen.Add(term)) result.Add(term);
        return result;
    }

    /// <summary>Curated article numbers for the branch, topic by topic (deduplicated, order kept).</summary>
    public static List<(string CodeId, string Number)> ArticleCandidates(CaseType caseType, IEnumerable<LegalTopic> topics)
    {
        var result = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var codeId = caseType == CaseType.Criminal ? "penale" : "civile";
        foreach (var topic in topics)
            foreach (var number in topic.ArticlesFor(caseType))
                if (seen.Add(number)) result.Add((codeId, number));
        return result;
    }

    /// <summary>Defence checklist of the given topics, deduplicated.</summary>
    public static List<string> DefencePoints(IEnumerable<LegalTopic> topics)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var topic in topics)
            foreach (var point in topic.DefencePoints)
                if (seen.Add(point)) result.Add(point);
        return result;
    }

    private static LegalTopic[] Build() =>
    [
        new LegalTopic
        {
            Id = "furto",
            Name = "Furto e reati contro il patrimonio mediante sottrazione",
            Branch = Legal.CaseType.Criminal,
            Triggers = ["furto", "rubato", "rubare", "ruba", "sottratto", "sottrazione", "impossessamento", "taccheggio", "borsa", "portafoglio", "bicicletta", "auto rubata", "cellulare rubato"],
            Expand = ["furto", "impossessamento", "cosa mobile altrui", "sottrazione", "detenzione", "fine di profitto", "furto aggravato", "circostanze aggravanti", "ricettazione", "restituzione", "querela", "particolare tenuita"],
            CriminalArticles = ["624", "625", "626", "627", "647", "648", "62", "62-bis", "131-bis", "163"],
            DefencePoints =
            [
                "Verificare che il bene fosse cosa mobile altrui: se era del reo, di nessuno o privo di valore economico, il fatto non integra il furto (art. 624 c.p.).",
                "Verificare l'effettiva sottrazione alla detenzione: se il bene era gia nella disponibilita del reo o non vi era detenzione altrui, manca l'elemento materiale.",
                "Il fine di profitto e elemento del dolo specifico: provare che l'agente agiva per restituire, per scherzo o per uso momentaneo esclude il reato.",
                "Restituzione o risarcimento prima del giudizio: attenuante comune e possibile esclusione della punibilita per particolare tenuita (artt. 62 n. 6, 62-bis, 131-bis c.p.).",
                "Verificare la tempestivita della querela (art. 124 c.p.): se il furto non e aggravato ed e procedibile a querela, una querela tardiva rende il reato improcedibile.",
                "Contestare le aggravanti dell'art. 625 c.p. (violenza sulle cose, destrezza, esposizione alla pubblica fede): sono loro a determinare competenza e pena.",
                "Se il bene e stato acquistato o ricevuto dopo il fatto, valutare la diversa e meno grave ipotesi di ricettazione (art. 648 c.p.) o di incauto acquisto.",
            ]
        },
        new LegalTopic
        {
            Id = "rapina-estorsione",
            Name = "Rapina, estorsione e sequestro a scopo di estorsione",
            Branch = Legal.CaseType.Criminal,
            Triggers = ["rapina", "rapinato", "estorsione", "estorto", "minaccia", "pistola", "coltello", "scippo", "strappo", "sequestro", "riscatto"],
            Expand = ["rapina", "violenza", "minaccia", "impossessamento", "cosa mobile altrui", "profitto", "estorsione", "costrizione", "ingiusto profitto", "danno", "sequestro di persona a scopo di estorsione", "attenuante"],
            CriminalArticles = ["628", "628-bis", "629", "630", "605", "624", "625", "62", "62-bis", "131-bis"],
            DefencePoints =
            [
                "Distinguere rapina da furto aggravato: senza violenza o minaccia alla persona resta il furto (artt. 624-625 c.p.), con pene e competenza molto piu favorevoli.",
                "Verificare l'intensita della violenza o minaccia: la minaccia deve essere idonea a coartare la volonta e diretta a impedire la difesa della cosa.",
                "Per l'estorsione accertare l'ingiustizia del profitto e il danno: un credito anche solo parzialmente fondato esclude l'ingiustizia (art. 629 c.p.).",
                "Verificare la qualificazione del sequestro: se la privazione della liberta e durata minima e strumentale alla rapina, non si configura il sequestro (art. 605 c.p.).",
                "Contestare le aggravanti dell'art. 628, comma 3 (armi, travisamento, numero di persone): cambiano pena e rito.",
                "Valutare la desistenza volontaria o il recesso attivo (art. 56, comma 4 c.p.) quando la refurtiva e stata restituita prima della consumazione.",
                "Restituzione e risarcimento: attenuanti e possibile particolare tenuita nei casi di minore gravita (artt. 62 n. 6, 62-bis, 131-bis c.p.).",
            ]
        },
        new LegalTopic
        {
            Id = "truffa-frodi",
            Name = "Truffa, frode informatica e altre frodi",
            Branch = Legal.CaseType.Criminal,
            Triggers = ["truffa", "truffato", "raggiro", "raggirato", "frode", "frodato", "phishing", "truffa online", "vendita online", "insolvenza", "assegno", "bonifico", "carta di credito", "clonata", "home banking"],
            Expand = ["truffa", "artifizi", "raggiri", "induzione in errore", "profitto ingiusto", "danno", "frode informatica", "alterazione sistema informatico", "insolvenza fraudolenta", "erogazioni pubbliche", "nesso causale"],
            CriminalArticles = ["640", "640-bis", "640-ter", "641", "643", "648", "316-ter", "62", "62-bis", "131-bis"],
            DefencePoints =
            [
                "Verificare gli artifizi o raggiri: una semplice inadempienza contrattuale, anche colposa, non integra la truffa (art. 640 c.p.).",
                "Accertare l'induzione in errore della vittima e il nesso causale con la disposizione patrimoniale: senza errore determinato dal raggiro il reato non sussiste.",
                "Verificare il danno e il profitto ingiusto: se il rapporto e ancora in esecuzione o il corrispettivo e stato in parte fornito, il danno va provato in concreto.",
                "Per la frode informatica accertare l'alterazione del funzionamento del sistema o l'intervento senza titolo: la sola violazione contrattuale non basta (art. 640-ter c.p.).",
                "Valutare la qualificazione piu lieve di insolvenza fraudolenta (art. 641 c.p.) o l'assenza di dolo iniziale (inadempimento sopravvenuto).",
                "Risarcimento del danno e restituzione: attenuante e possibile definizione con messa alla prova o particolare tenuita (artt. 62 n. 6, 131-bis c.p.).",
                "Contestare le aggravanti (abuso di prestazioni d'opera, danno di rilevante gravita, mezzo informatico) che spostano la competenza al tribunale collegiale.",
            ]
        },        new LegalTopic
        {
            Id = "appropriazione-ricettazione",
            Name = "Appropriazione indebita, ricettazione e riciclaggio",
            Branch = Legal.CaseType.Criminal,
            Triggers = ["appropriazione", "appropriato", "ricettazione", "riciclato", "riciclaggio", "denaro sporco", "non ho restituito", "affidato", "comodato", "incasso", "cassa", "socio", "societa"],
            Expand = ["appropriazione indebita", "possesso per ragioni di lavoro", "denaro altrui", "ricettazione", "provenienza delittuosa", "riciclaggio", "sostituzione", "impiego", "autoriciclaggio", "buona fede", "provenienza"],
            CriminalArticles = ["646", "647", "648", "648-bis", "648-ter.1", "649", "518-ter", "518-quater"],
            DefencePoints =
            [
                "Verificare il titolo del possesso: l'appropriazione indebita richiede il possesso per ragioni di lavoro, deposito o fiducia, non la mera detenzione materiale (art. 646 c.p.).",
                "Accertare la volontà di invertire il titolo e di tenere per sé la cosa: il ritardo nella restituzione, senza animus, è inadempimento civile.",
                "Per la ricettazione provare la provenienza da delitto e la conoscenza (o il sospetto) della stessa: mancando la prova della provenienza il fatto non sussiste (art. 648 c.p.).",
                "Per il riciclaggio accertare le operazioni di sostituzione o trasferimento e l'idoneità a ostacolare l'identificazione: la sola detenzione non basta (art. 648-bis c.p.).",
                "Valutare il rapporto di non punibilità dell'art. 649 c.p. quando il fatto è commesso in danno di un familiare convivente.",
                "Verificare l'elemento soggettivo: la buona fede sull'origine del bene, se supportata da documentazione, esclude il dolo.",
                "Restituzione integrale prima del giudizio e risarcimento: attenuanti e possibile particolare tenuità.",
            ]
        },
        new LegalTopic
        {
            Id = "persona-violenta",
            Name = "Omicidio, lesioni e percosse",
            Branch = Legal.CaseType.Criminal,
            Triggers = ["omicidio", "ucciso", "morto", "morte", "lesioni", "lesionato", "ferito", "percosse", "picchiato", "aggressione", "rissa", "incidente mortale", "investito", "omissione di soccorso"],
            Expand = ["omicidio", "lesione personale", "percosse", "nesso causale", "colpa", "negligenza", "imprudenza", "imperizia", "legittima difesa", "eccesso colposo", "omissione di soccorso", "omicidio stradale", "lesioni stradali", "attenuanti"],
            CriminalArticles = ["575", "579", "584", "589", "589-bis", "582", "590", "590-bis", "581", "593", "52", "53", "54", "55", "62", "62-bis", "131-bis"],
            DefencePoints =
            [
                "Verificare il nesso causale tra condotta ed evento: senza prova del nesso, il reato non sussiste (artt. 575, 589 c.p.).",
                "Per le lesioni accertare la durata della malattia e la qualificazione (lievissime, lievi, gravi, gravissime): cambia procedibilità, competenza e pena (artt. 582-583 c.p.).",
                "Verificare la legittima difesa (art. 52 c.p.) e l'eccesso colposo (art. 55 c.p.): proporzionalità tra offesa e difesa, anche alla luce della difesa domiciliare.",
                "Valutare il concorso di colpa della vittima e le condotte alternative lecite: rilevano sul nesso causale e sul grado della colpa.",
                "Per l'omicidio e le lesioni stradali accertare il tasso alcolemico, la velocità, l'uso del telefono e la condotta della vittima (artt. 589-bis, 590-bis c.p.).",
                "Omissione di soccorso (art. 593 c.p.): verificare l'effettiva impossibilità o il pericolo nel prestare soccorso.",
                "Risarcimento e riparazione: attenuante, sospensione condizionale, messa alla prova e, nei casi lievi, particolare tenuità (art. 131-bis c.p.).",
            ]
        },
        new LegalTopic
        {
            Id = "violenza-genere",
            Name = "Violenza sessuale, maltrattamenti e atti persecutori",
            Branch = Legal.CaseType.Criminal,
            Triggers = ["violenza sessuale", "stupro", "molestia", "molestie", "maltrattamenti", "stalker", "stalking", "atti persecutori", "minacce", "ex partner", "ex marito", "ex moglie", "violenza domestica", "percosse in famiglia", "revenge porn"],
            Expand = ["violenza sessuale", "atti sessuali", "consenso", "maltrattamenti contro familiari e conviventi", "atti persecutori", "minaccia", "violenza privata", "relazione affettiva", "vittima", "querela", "codice rosso"],
            CriminalArticles = ["609-bis", "609-ter", "609-octies", "572", "612-bis", "612", "610", "571", "61", "62", "62-bis"],
            DefencePoints =
            [
                "Accertare il dissenso effettivo e la sua manifestazione: la prova deve essere specifica e non fondata su mere dichiarazioni della persona offesa prive di riscontri.",
                "Verificare la sussistenza della relazione qualificata per i maltrattamenti (art. 572 c.p.): senza rapporto familiare o di convivenza il fatto va riqualificato.",
                "Per gli atti persecutori provare gli atti reiterati e l'evento (perdurante stato d'ansia, timore fondato, mutamento delle abitudini): mancando l'evento il reato non sussiste (art. 612-bis c.p.).",
                "Verificare la tempestività della querela e l'eventuale remissione: nei reati a querela la procedibilità è condizione dell'azione.",
                "Valutare la riqualificazione in reati meno gravi (minaccia art. 612, violenza privata art. 610, percosse art. 581 c.p.).",
                "Contestare le aggravanti (danno grave, minori, armi, rapporto di fiducia) e verificare le circostanze attenuanti.",
                "Valutare i percorsi di giustizia riparativa e la messa alla prova, quando ammessi per il titolo di reato.",
            ]
        },
        new LegalTopic
        {
            Id = "onore-diffamazione",
            Name = "Diffamazione, ingiuria e reati contro l'onore",
            Branch = Legal.CaseType.Criminal,
            Triggers = ["diffamazione", "diffamato", "insulto", "insultato", "offesa", "social", "facebook", "instagram", "recensione", "giornalista", "calunnia", "denuncia falsa", "querela"],
            Expand = ["diffamazione", "offesa reputazione", "mezzo di pubblicità", "comunicazione a più persone", "diritto di cronaca", "continenza", "verità dei fatti", "interesse pubblico", "calunnia", "ingiuria", "rettifica"],
            CriminalArticles = ["595", "596", "596-bis", "368", "369", "594", "57", "598", "599"],
            DefencePoints =
            [
                "Verificare la comunicazione a più persone: la diffamazione richiede la presenza di almeno due destinatari (art. 595 c.p.).",
                "Invocare l'esimente del diritto di cronaca e di critica: verità dei fatti, pertinenza e continenza formale (art. 51 c.p.).",
                "Verificare la veridicità della notizia e la sua provenienza: la verità putativa richiede una verifica diligente delle fonti.",
                "Contestare la qualificazione in ingiuria (art. 594 c.p.) o in esercizio del diritto di satira, che tollera maggiore mordacità.",
                "Verificare la tempestività della querela e la richiesta di rettifica, che può escludere o attenuare il dolo.",
                "Per la calunnia (art. 368 c.p.) accertare la falsità dell'incolpazione e la consapevolezza: la denuncia di un fatto dubbio non integra il reato.",
                "Valutare l'offerta di riparazione e la remissione della querela come uscita rapida dal procedimento.",
            ]
        },        new LegalTopic
        {
            Id = "pubblica-amministrazione",
            Name = "Reati contro la pubblica amministrazione",
            Branch = Legal.CaseType.Criminal,
            Triggers = ["peculato", "concussione", "corruzione", "tangente", "pubblico ufficiale", "appalto", "abuso d'ufficio", "truffa ai danni dello stato", "finanziamento pubblico", "sovvenzione"],
            Expand = ["peculato", "concussione", "corruzione per l'esercizio della funzione", "corruzione per atto contrario ai doveri d'ufficio", "pubblico ufficiale", "incaricato di pubblico servizio", "indebita percezione", "danno erariale", "abuso di ufficio"],
            CriminalArticles = ["314", "316", "317", "318", "319", "319-ter", "320", "322", "323", "246", "316-bis", "316-ter", "331", "334", "335", "336", "357", "358"],
            DefencePoints =
            [
                "Verificare la qualifica di pubblico ufficiale o incaricato di pubblico servizio: se manca, il reato proprio non sussiste (artt. 357-358 c.p.).",
                "Accertare l'atto contrario ai doveri d'ufficio o l'esercizio della funzione: la promessa o dazione deve essere legata a un atto determinato o determinabile.",
                "Per la concussione distinguere la costrizione (art. 317 c.p.) dall'induzione indebita (art. 319-quater c.p.): la qualificazione cambia la posizione del privato.",
                "Verificare l'appropriazione effettiva e la destinazione delle somme nel peculato (art. 314 c.p.).",
                "Contestare la prova del patto corruttivo: senza prova dell'accordo il fatto può restare un illecito amministrativo o disciplinare.",
                "Valutare l'art. 323 c.p. dopo le modifiche che ne hanno ristretto l'ambito e la natura residuale rispetto ad altri illeciti.",
                "Verificare la prescrizione, spesso decisiva per reati commessi anni prima, e le attenuanti per collaborazione (artt. 323-bis, 62-bis c.p.).",
            ]
        },
        new LegalTopic
        {
            Id = "informatici",
            Name = "Reati informatici e accesso abusivo a sistemi",
            Branch = Legal.CaseType.Criminal,
            Triggers = ["hacker", "hackerato", "accesso abusivo", "sistema informatico", "database", "credenziali", "password", "virus", "malware", "ransomware", "intercettazione", "profilo", "account", "dati personali", "spyware"],
            Expand = ["accesso abusivo", "sistema informatico", "telematico", "misure di sicurezza", "intercettazione", "comunicazioni informatiche", "danneggiamento informatico", "dati", "programmi", "documenti informatici", "frode informatica"],
            CriminalArticles = ["615-ter", "615-quater", "615-quinquies", "617-bis", "617-quater", "617-quinquies", "635-bis", "635-ter", "635-quater", "635-quinquies", "640-ter", "491-bis", "616"],
            DefencePoints =
            [
                "Verificare l'assenza di titolo all'accesso e l'elusione delle misure di sicurezza: se le credenziali erano legittimamente nella disponibilità del soggetto, manca l'abuso (art. 615-ter c.p.).",
                "Accertare la qualificazione del sistema come informatico o telematico e la sua riconducibilità a un titolare.",
                "Distinguere l'accesso abusivo (615-ter) dalla detenzione di strumenti (615-quater) e dalla frode informatica (640-ter): le pene sono molto diverse.",
                "Per l'intercettazione (artt. 617-bis, 617-quater c.p.) verificare la natura delle comunicazioni e la presenza di misure di sicurezza.",
                "Contestare il danno informatico (artt. 635-bis e ss. c.p.) quando non vi è distruzione o deterioramento effettivo dei dati.",
                "Verificare l'elemento soggettivo: l'errore sulle credenziali o l'uso per finalità di verifica aziendale esclude il dolo.",
                "Valutare la particolare tenuità (art. 131-bis c.p.) e la sospensione condizionale per fatti di minima offensività.",
            ]
        },
        new LegalTopic
        {
            Id = "danneggiamento-incendio",
            Name = "Danneggiamento, incendio e turbativa del possesso",
            Branch = Legal.CaseType.Criminal,
            Triggers = ["danneggiamento", "danneggiato", "rotto", "distrutto", "incendio", "incendiato", "fuoco", "vandalismo", "graffiti", "occupazione", "cantiere"],
            Expand = ["danneggiamento", "distruzione", "deterioramento", "cosa mobile altrui", "incendio", "pericolo comune", "incendio boschivo", "turbativa", "esercizio arbitrario delle proprie ragioni", "violenza sulle cose"],
            CriminalArticles = ["635", "635.1", "635-bis", "423", "423-bis", "424", "427", "429", "633", "634", "639", "392", "393", "62", "131-bis"],
            DefencePoints =
            [
                "Verificare l'entità del danno: la fattispecie dell'art. 635 c.p. richiede distruzione, dispersione, deterioramento o inservibilità della cosa.",
                "Accertare il valore del bene e la sua proprietà altrui: se il bene è proprio o di nessuno il reato non sussiste.",
                "Per l'incendio accertare il pericolo comune e l'attitudine a propagarsi della fiamma (art. 423 c.p.): senza pericolo comune si applicano altre fattispecie.",
                "Valutare la riqualificazione nell'esercizio arbitrario delle proprie ragioni con violenza sulle cose (art. 392 c.p.), reato meno grave e procedibile a querela.",
                "Verificare la presenza di un titolo che giustifichi l'attività (contratto, autorizzazione, ordine dell'autorità).",
                "Risarcimento del danno prima del giudizio: attenuante e possibile definizione con oblazione per le contravvenzioni.",
                "Contestare le aggravanti (danno di rilevante gravità, beni culturali, pubblico interesse) che spostano la competenza.",
            ]
        },
        new LegalTopic
        {
            Id = "associazione-mafia",
            Name = "Associazione per delinquere, mafia e misure di prevenzione",
            Branch = Legal.CaseType.Criminal,
            Triggers = ["associazione", "mafia", "mafioso", "camorra", "ndrangheta", "clan", "sodalizio", "misure di prevenzione", "sequestro beni", "confisca"],
            Expand = ["associazione per delinquere", "associazione di tipo mafioso", "vincolo associativo", "forza di intimidazione", "programma delittuoso", "misure di prevenzione", "pericolosità sociale", "confisca", "proposta"],
            CriminalArticles = ["416", "416-bis", "416-ter", "418", "270-bis", "289-bis", "648-bis"],
            DefencePoints =
            [
                "Accertare l'esistenza del vincolo associativo stabile e del programma delittuoso: la pluralità di reati occasionali non integra l'associazione (art. 416 c.p.).",
                "Per l'associazione mafiosa provare la forza di intimidazione e l'assoggettamento: senza questi elementi non si configura l'art. 416-bis c.p.",
                "Verificare la posizione individuale (affiliazione, partecipazione, concorso esterno) e il contributo causale effettivo.",
                "Contestare la validità delle fonti dichiarative: attendibilità, riscontri individualizzanti e intrinseci (orientamento costante di legittimità).",
                "Nelle misure di prevenzione contestare la pericolosità sociale attuale e la sproporzione tra redditi e beni (artt. 16-24 d.lgs. 159/2011).",
                "Verificare i termini di durata della misura e la possibilità di revoca o modifica.",
                "Valutare la prescrizione dei reati presupposto e l'eventuale irrilevanza penale dei fatti più risalenti.",
            ]
        },
        new LegalTopic
        {
            Id = "usura-falso",
            Name = "Usura, falso e reati contro la fede pubblica",
            Branch = Legal.CaseType.Criminal,
            Triggers = ["usura", "usuraio", "interessi", "tasso", "falso", "falsificato", "documento falso", "firma falsa", "patente falsa", "certificato", "perizia", "testimonianza", "giuramento"],
            Expand = ["usura", "interessi usurari", "tasso soglia", "vantaggio", "stato di bisogno", "falso materiale", "falso ideologico", "documento", "atto pubblico", "scrittura privata", "falsa testimonianza", "perizia"],
            CriminalArticles = ["644", "644-bis", "476", "477", "478", "479", "480", "482", "483", "485", "489", "491", "491-bis", "493", "368", "372", "373", "374", "375", "376", "377"],
            DefencePoints =
            [
                "Per l'usura verificare il superamento del tasso soglia e lo stato di bisogno della vittima (art. 644 c.p.): senza stato di bisogno la fattispecie non si configura.",
                "Ricalcolare gli interessi effettivi secondo le regole della legge 108/1996 e le rilevazioni trimestrali.",
                "Per il falso verificare la natura dell'atto (pubblico o privato) e la sua idoneità a trarre in inganno: cambia il titolo di reato.",
                "Accertare l'elemento soggettivo del falso: la falsa rappresentazione dovuta a errore o a interpretazione non integra il dolo.",
                "Per la falsa testimonianza (art. 372 c.p.) verificare la rilevanza della deposizione e la volontarietà della mendacità.",
                "Valutare la ritrattazione (art. 376 c.p.) e la desistenza, che escludono o attenuano la punibilità.",
                "Contestare le aggravanti e verificare la prescrizione, spesso maturata nei processi per falso documentale risalente.",
            ]
        },
        new LegalTopic
        {
            Id = "prescrizione-procedibilita",
            Name = "Prescrizione, querela e condizioni di procedibilità",
            Branch = Legal.CaseType.Criminal,
            Triggers = ["prescrizione", "prescritto", "querela", "denuncia", "anni fa", "remissione", "procedibilita", "oblazione", "improcedibile", "termine"],
            Expand = ["prescrizione del reato", "termine di prescrizione", "interruzione", "sospensione", "querela", "remissione di querela", "condizione di procedibilita", "oblazione", "improcedibilita"],
            CriminalArticles = ["157", "158", "159", "160", "161", "172", "173", "120", "123", "124", "126", "127", "162", "162-bis", "131-bis"],
            DefencePoints =
            [
                "Calcolare il termine di prescrizione in base alla pena massima edittale, con le sospensioni e le interruzioni (artt. 157-161 c.p.).",
                "Verificare che la querela sia stata proposta entro il termine di tre mesi dalla conoscenza del fatto (art. 124 c.p.).",
                "Controllare la legittimazione del querelante e la forma della querela.",
                "Valutare la remissione di querela e l'accettazione della persona offesa (artt. 152-155 c.p.).",
                "Verificare l'ammissibilità dell'oblazione per le contravvenzioni (artt. 162-162-bis c.p.).",
                "Eccepire l'improcedibilità o l'assenza delle condizioni di procedibilità nella fase iniziale del giudizio.",
                "Chiedere la particolare tenuità del fatto (art. 131-bis c.p.) quando ricorrono i presupposti.",
            ]
        },        new LegalTopic
        {
            Id = "contratto-inadempimento",
            Name = "Contratto, inadempimento e risoluzione",
            Branch = Legal.CaseType.Civil,
            Triggers = ["contratto", "inadempimento", "inadempiente", "non ha pagato", "non paga", "fornitore", "prestazione", "risoluzione", "clausola", "penale", "caparra", "acconto", "appalto", "preventivo", "ordine"],
            Expand = ["inadempimento", "obbligazione", "diligenza", "buona fede", "risoluzione per inadempimento", "diffida", "termine essenziale", "eccezione di inadempimento", "clausola penale", "caparra confirmatoria", "condizioni generali di contratto", "interpretazione del contratto"],
            CivilArticles = ["1218", "1219", "1223", "1225", "1226", "1227", "1175", "1176", "1372", "1375", "1453", "1454", "1455", "1456", "1460", "1461", "1382", "1383", "1384", "1385", "1341", "1342", "1362", "1366", "1370", "1371", "1321", "1325", "1418", "1421", "1435", "1441", "1442", "2946", "2964"],
            DefencePoints =
            [
                "Verificare l'esatto contenuto delle obbligazioni e il termine di adempimento: senza prova della prestazione dovuta non vi è inadempimento (artt. 1218, 1372 c.c.).",
                "Costituire in mora il debitore o dimostrare che la mora è automatica (art. 1219 c.c.).",
                "Documentare il danno effettivo e il nesso causale, nonché la sua prevedibilità (artt. 1223, 1225 c.c.).",
                "Valutare la risoluzione giudiziale o la diffida ex art. 1454 c.c., o la clausola risolutiva espressa (art. 1456 c.c.).",
                "Eccepire l'inadempimento della controparte e rifiutare la prestazione (art. 1460 c.c.) o chiedere la sospensione (art. 1461 c.c.).",
                "Contestare clausole vessatorie non specificamente approvate (artt. 1341, 1342 c.c.) e clausole abusive nei contratti con consumatori.",
                "Ridurre la penale manifestamente eccessiva (art. 1384 c.c.) o chiedere la restituzione della caparra (art. 1385 c.c.).",
                "Verificare prescrizione e decadenza dell'azione (artt. 2946, 2947, 2964 c.c.).",
            ]
        },
        new LegalTopic
        {
            Id = "risarcimento-danno",
            Name = "Responsabilità civile e risarcimento del danno",
            Branch = Legal.CaseType.Civil,
            Triggers = ["risarcimento", "risarcire", "danno", "danni", "incidente", "sinistro", "caduto", "caduta", "investito", "assicurazione", "malasanita", "medico", "errore medico", "vicino", "infiltrazione", "allagamento"],
            Expand = ["responsabilita extracontrattuale", "fatto illecito", "nesso causale", "danno ingiusto", "risarcimento", "danno non patrimoniale", "responsabilita del custode", "responsabilita del datore di lavoro", "circolazione di veicoli", "concorso di colpa", "onere della prova", "quantificazione del danno"],
            CivilArticles = ["2043", "2049", "2050", "2051", "2052", "2054", "2055", "2056", "2057", "2058", "2059", "2047", "2048", "2087", "1223", "1226", "1227", "2697", "1176", "1218", "1219", "1229", "2934"],
            DefencePoints =
            [
                "Verificare gli elementi dell'illecito: condotta, nesso causale, danno ingiusto, imputabilità (art. 2043 c.c.).",
                "Accertare il concorso di colpa del danneggiato e ridurre proporzionalmente il risarcimento (art. 1227 c.c.).",
                "Per la circolazione di veicoli la responsabilità è presunta (art. 2054 c.c.): provare la causa improvvisa ed inevitabile o il caso fortuito.",
                "Per la responsabilità del custode (art. 2051 c.c.) provare il caso fortuito e l'adeguatezza delle misure di sicurezza.",
                "Per la responsabilità medica accertare la diligenza, la correttezza della condotta e il nesso con l'evento lesivo (artt. 1176, 2236 c.c.).",
                "Quantificare il danno con criteri certi: tabelle milanesi, danno biologico, perdita di capacità lavorativa e spese documentate (artt. 1223, 1226, 2059 c.c.).",
                "Verificare la prescrizione: cinque anni per l'illecito, con decorrenza dalla conoscenza del danno e della sua ingiustizia.",
                "Onere della prova: il danneggiato prova il fatto e il nesso; il danneggiante prova il caso fortuito o l'assenza di colpa quando la responsabilità è presunta (art. 2697 c.c.).",
            ]
        },
        new LegalTopic
        {
            Id = "compravendita-vizi",
            Name = "Compravendita, vizi della cosa e garanzia",
            Branch = Legal.CaseType.Civil,
            Triggers = ["compravendita", "vendita", "venditore", "comprato", "acquisto", "vizio", "vizi", "difettoso", "garanzia", "rimborso", "auto usata", "immobile", "rogito", "notaio"],
            Expand = ["compravendita", "vizi della cosa venduta", "garanzia per vizi", "difetto di conformita", "termine di decadenza", "denuncia dei vizi", "evizione", "consegna", "pagamento del prezzo", "caparra confirmatoria", "risoluzione", "riduzione del prezzo"],
            CivilArticles = ["1470", "1475", "1476", "1477", "1479", "1481", "1490", "1491", "1492", "1493", "1494", "1495", "1497", "1498", "1502", "1503", "1512", "1519", "1520", "1522", "1385", "1453"],
            DefencePoints =
            [
                "Verificare la denuncia dei vizi entro otto giorni dalla scoperta, salvo diversa pattuizione (art. 1495 c.c.): la decadenza preclude l'azione.",
                "Accertare che i vizi fossero occulti e non facilmente riconoscibili al momento dell'acquisto (art. 1490 c.c.).",
                "Distinguere la garanzia per vizi (artt. 1490-1497 c.c.) dal difetto di conformità nel contratto con il consumatore (artt. 128-135 cod. cons.).",
                "Valutare l'esclusione convenzionale della garanzia e la sua opponibilità in caso di mala fede del venditore (art. 1490, comma 2 c.c.).",
                "Chiedere la riduzione del prezzo o la risoluzione con restituzione del prezzo e rimborso delle spese (artt. 1492, 1493 c.c.).",
                "Per gli immobili verificare la conformità catastale e urbanistica e le dichiarazioni del venditore nell'atto.",
                "Verificare la presenza di ipoteche o pesi e l'evizione (artt. 1481-1489 c.c.).",
                "Prescrizione: un anno dalla consegna per i vizi (art. 1495 c.c.).",
            ]
        },
        new LegalTopic
        {
            Id = "locazione-sfratto",
            Name = "Locazione, affitto e rilascio dell'immobile",
            Branch = Legal.CaseType.Civil,
            Triggers = ["locazione", "affitto", "affittuario", "inquilino", "padrone di casa", "sfratto", "morosita", "canone", "deposito cauzionale", "appartamento", "conduttore", "locatore", "disdetta"],
            Expand = ["locazione", "conduttore", "locatore", "canone", "manutenzione", "riparazioni", "mora", "sfratto per morosita", "rilascio dell immobile", "disdetta", "deposito cauzionale", "miglioramenti", "sublocazione"],
            CivilArticles = ["1571", "1573", "1574", "1575", "1576", "1577", "1580", "1587", "1590", "1591", "1592", "1593", "1595", "1596", "1609", "1611", "1614", "1615", "1621", "1622", "1623", "1633", "1634", "1635", "1636", "1453", "1454", "1455", "1456", "1385", "2740", "2764"],
            DefencePoints =
            [
                "Verificare la forma e la registrazione del contratto: la mancata registrazione consente la riduzione del canone e la durata secondo la legge 392/1978.",
                "Contestare la morosità con la prova dei pagamenti o con l'eccezione di inadempimento del locatore (artt. 1460, 1575-1580 c.c.).",
                "Verificare l'obbligo di manutenzione straordinaria del locatore e la riduzione del canone per vizi (artt. 1575, 1576, 1577, 1580 c.c.).",
                "Opporsi allo sfratto per morosità con la sanatoria della morosità e la richiesta di termine per il pagamento.",
                "Contestare la disdetta anticipata e verificare il preavviso contrattuale (art. 1596 c.c.).",
                "Chiedere la restituzione del deposito cauzionale e il rimborso delle migliorie autorizzate (artt. 1592, 1593 c.c.).",
                "Verificare le spese condominiali addebitabili al conduttore e la loro documentazione.",
                "Accertare la sussistenza di un contratto di comodato o di occupazione senza titolo, con disciplina diversa dalla locazione.",
            ]
        },        new LegalTopic
        {
            Id = "condominio",
            Name = "Condominio e rapporti di vicinato",
            Branch = Legal.CaseType.Civil,
            Triggers = ["condominio", "condomino", "assemblea", "amministratore", "spese condominiali", "millesimi", "lastrico", "tetto", "ascensore", "cortile", "garage", "rumore", "immissioni", "disturbo", "confine"],
            Expand = ["condominio", "parti comuni", "spese di manutenzione", "assemblea condominiale", "delibera", "impugnazione della delibera", "amministratore", "millesimi", "tabelle millesimali", "immissioni", "distanze legali", "luci e vedute"],
            CivilArticles = ["1117", "1118", "1119", "1120", "1121", "1122", "1123", "1124", "1125", "1126", "1127", "1128", "1129", "1130", "1131", "1132", "1135", "1136", "1137", "1138", "1139", "1140", "1141", "1142", "1143", "1144", "1145", "1146", "1147", "844", "873", "949", "1102"],
            DefencePoints =
            [
                "Verificare la regolarità della convocazione e delle maggioranze dell'assemblea: il vizio rende la delibera annullabile (artt. 1136, 1137 c.c.).",
                "Impugnare la delibera entro trenta giorni dalla comunicazione o dalla deliberazione (art. 1137 c.c.).",
                "Contestare la ripartizione delle spese non conformi alle tabelle millesimali o all'uso effettivo (artt. 1123, 1124, 1125, 1126 c.c.).",
                "Verificare che le spese di manutenzione straordinaria siano deliberate e che l'amministratore abbia i poteri (artt. 1130, 1135 c.c.).",
                "Per le immissioni accertare la normale tollerabilità, il luogo e le condizioni di vita (art. 844 c.c.).",
                "Per le distanze legali e le vedute verificare le prescrizioni del codice e le eventuali convenzioni.",
                "Accertare il titolo della proprietà delle parti comuni e l'eventuale destinazione d'uso esclusiva.",
                "Chiedere la nomina giudiziale dell'amministratore in caso di inerzia dell'assemblea (art. 1129 c.c.).",
            ]
        },
        new LegalTopic
        {
            Id = "famiglia-separazione",
            Name = "Separazione, divorzio, figli e rapporti familiari",
            Branch = Legal.CaseType.Civil,
            Triggers = ["separazione", "divorzio", "mantenimento", "assegno", "figli", "affidamento", "collocamento", "visite", "matrimonio", "coniuge", "convivente", "casa familiare", "alimenti", "tradimento"],
            Expand = ["separazione personale", "scioglimento del matrimonio", "affidamento dei figli", "mantenimento dei figli", "assegno di mantenimento", "casa familiare", "comunione dei beni", "obblighi dei coniugi", "interesse del minore", "alimenti"],
            CivilArticles = ["143", "144", "145", "147", "148", "150", "151", "156", "157", "158", "160", "177", "192", "230-bis", "230-ter", "315-bis", "316", "316-bis", "330", "333", "336", "336-bis", "337-bis", "337-ter", "337-quater", "337-sexies", "337-octies", "433", "438"],
            DefencePoints =
            [
                "Verificare i presupposti della separazione (intollerabilità della convivenza) o del divorzio e la data di cessazione della convivenza.",
                "Ricostruire i redditi e il tenore di vita matrimoniale per determinare assegno e mantenimento (artt. 156, 337-ter c.c.).",
                "Per i figli accertare l'interesse morale e materiale, il collocamento e la bigenitorialità (art. 337-ter c.c.).",
                "Contestare la richiesta di addebito della separazione: serve la prova della violazione dei doveri coniugali e del nesso causale (artt. 143, 151 c.c.).",
                "Verificare la proprietà della casa familiare e l'assegnazione in favore del coniuge collocatario dei figli (art. 337-sexies c.c.).",
                "Accertare la composizione della comunione dei beni e le pretese di rimborso (artt. 177-192 c.c.).",
                "Valutare i presupposti degli alimenti e la loro misura (artt. 433-448 c.c.).",
                "Considerare la mediazione familiare e gli accordi di negoziazione assistita come via rapida.",
            ]
        },
        new LegalTopic
        {
            Id = "successioni-testamento",
            Name = "Successioni, testamento e legittima",
            Branch = Legal.CaseType.Civil,
            Triggers = ["successione", "eredita", "erede", "eredi", "testamento", "legittima", "legittimario", "quota", "de cuius", "donazione", "collazione", "usufrutto", "divisione"],
            Expand = ["successione legittima", "successione testamentaria", "quota di legittima", "legittimario", "azione di riduzione", "collazione", "donazione", "accettazione dell eredita", "rinuncia", "testamento olografo", "invalidita del testamento", "comunione ereditaria", "divisione"],
            CivilArticles = ["456", "457", "460", "461", "462", "464", "470", "471", "475", "476", "484", "490", "519", "536", "540", "542", "544", "553", "556", "564", "565", "566", "572", "580", "581", "582", "583", "587", "588", "591", "602", "603", "606", "619", "620", "624", "625", "630", "696", "713", "715", "718", "724", "725", "727", "729", "735", "737", "746", "747", "748", "750", "751", "752", "753", "769"],
            DefencePoints =
            [
                "Verificare la qualità di erede e la quota spettante secondo la successione legittima o testamentaria (artt. 565-586, 587-588 c.c.).",
                "Accertare i requisiti di forma del testamento olografo: autografia, data e sottoscrizione (art. 602 c.c.); la loro mancanza ne comporta l'annullabilità.",
                "Verificare i vizi della volontà (errore, violenza, dolo) e l'incapacità di testare (artt. 591, 624, 625 c.c.).",
                "Calcolare la legittima e proporre l'azione di riduzione delle disposizioni lesive (artt. 536-564 c.c.), previa imputazione delle donazioni.",
                "Verificare la collazione delle donazioni e la riunione fittizia del patrimonio (artt. 737-751, 556 c.c.).",
                "Controllare i termini di prescrizione dell'azione di riduzione e di annullamento del testamento.",
                "Per la divisione ereditaria verificare le quote, i debiti e le spese (artt. 713-729 c.c.).",
                "Valutare l'accettazione con beneficio d'inventario per limitare la responsabilità per i debiti ereditari (artt. 484-490 c.c.).",
            ]
        },
        new LegalTopic
        {
            Id = "lavoro",
            Name = "Rapporto di lavoro, licenziamento e retribuzione",
            Branch = Legal.CaseType.Civil,
            Triggers = ["lavoro", "lavoratore", "dipendente", "licenziamento", "licenziato", "dimissioni", "stipendio", "retribuzione", "straordinario", "ferie", "datore di lavoro", "mansioni", "demansionamento", "mobbing", "infortunio", "sicurezza sul lavoro", "apprendistato"],
            Expand = ["rapporto di lavoro subordinato", "licenziamento per giusta causa", "licenziamento per giustificato motivo", "retribuzione", "mansioni", "dequalificazione", "obbligo di sicurezza", "infortunio sul lavoro", "onere della prova", "preavviso", "indennita", "orario di lavoro"],
            CivilArticles = ["2060", "2087", "2094", "2095", "2099", "2103", "2104", "2105", "2106", "2109", "2110", "2113", "2118", "2119", "2120", "2121", "2124", "2125", "2126", "1375", "1175", "1176", "1218", "2697"],
            DefencePoints =
            [
                "Verificare la natura subordinata del rapporto e la prova documentale (buste paga, contratti, comunicazioni).",
                "Per il licenziamento accertare la giusta causa o il giustificato motivo e la motivazione scritta (artt. 2118, 2119 c.c. e norme speciali).",
                "Verificare il rispetto delle procedure (contestazione disciplinare, termine per la difesa) e la tempestività della contestazione.",
                "Contestare il demansionamento e la dequalificazione professionale (art. 2103 c.c.).",
                "Per il mobbing provare la condotta vessatoria reiterata, l'evento lesivo e il nesso causale.",
                "Per l'infortunio accertare l'obbligo di sicurezza e la responsabilità del datore (art. 2087 c.c.).",
                "Quantificare le differenze retributive con conteggi analitici (straordinari, ferie, TFR).",
                "Verificare la prescrizione quinquennale dei crediti di lavoro e la decadenza per i licenziamenti illegittimi.",
            ]
        },
        new LegalTopic
        {
            Id = "proprieta-possesso",
            Name = "Proprietà, possesso, usucapione e confini",
            Branch = Legal.CaseType.Civil,
            Triggers = ["proprieta", "proprietario", "possesso", "usucapione", "occupazione", "confine", "servitu", "passaggio", "usufrutto", "comunione", "divisione", "terreno", "fondo", "recinzione", "albero", "muro", "rivendica"],
            Expand = ["proprieta", "possesso", "usucapione", "azione di rivendicazione", "azione negatoria", "servitu di passaggio", "confini", "comunione", "divisione", "frutti", "accessione", "denuncia di nuova opera"],
            CivilArticles = ["832", "833", "840", "841", "844", "870", "873", "875", "889", "892", "896", "896-bis", "948", "949", "950", "951", "952", "957", "978", "981", "1014", "1021", "1027", "1030", "1051", "1052", "1053", "1054", "1055", "1058", "1100", "1102", "1111", "1140", "1141", "1145", "1147", "1150", "1151", "1152", "1153", "1155", "1158", "1159", "1161", "1162", "1163", "1164", "1165", "1168", "1169", "1170"],
            DefencePoints =
            [
                "Verificare il titolo di proprietà e la catena dei trasferimenti per l'azione di rivendicazione (art. 948 c.c.).",
                "Per l'usucapione provare il possesso continuato, pacifico, pubblico e non equivoco per il tempo richiesto (artt. 1158-1165 c.c.).",
                "Verificare la natura del possesso (di buona o mala fede) e il diritto ai frutti (artt. 1147-1148 c.c.).",
                "Per la servitù di passaggio accertare il titolo o la costituzione per destinazione del padre di famiglia o per usucapione (artt. 1051-1061 c.c.).",
                "Contestare le immissioni e le opere abusive con le azioni di manutenzione e di nuova opera (artt. 844, 1171-1172 c.c.).",
                "Verificare le distanze legali per costruzioni, alberi e vedute (artt. 873-907 c.c.).",
                "Per la comunione chiedere la divisione o contestare il godimento esclusivo (artt. 1111-1113 c.c.).",
                "Controllare la prescrizione delle azioni reali e gli effetti dell'acquisto per usucapione abbreviata (art. 1159 c.c.).",
            ]
        },
        new LegalTopic
        {
            Id = "prova-prescrizione",
            Name = "Prova, prescrizione e decadenza nel processo civile",
            Branch = Legal.CaseType.Civil,
            Triggers = ["prova", "prove", "testimone", "documento", "prescrizione", "decadenza", "termine", "onere della prova", "ctu", "perizia", "consulenza tecnica", "scrittura privata", "confessione", "giuramento"],
            Expand = ["onere della prova", "prova documentale", "prova testimoniale", "prescrizione", "decadenza", "termine", "consulenza tecnica d ufficio", "scrittura privata", "confessione", "giuramento", "argomenti di prova"],
            CivilArticles = ["2697", "2698", "2699", "2700", "2702", "2704", "2705", "2709", "2712", "2721", "2722", "2724", "2725", "2727", "2729", "2730", "2733", "2735", "2736", "2739", "2740", "2934", "2935", "2936", "2937", "2941", "2942", "2943", "2944", "2946", "2947", "2948", "2949", "2954", "2955", "2956", "2964", "2965", "2966", "2967", "2968", "2969"],
            DefencePoints =
            [
                "Individuare chi ha l'onere della prova e quali fatti sono contestati (art. 2697 c.c.).",
                "Verificare l'ammissibilità della prova testimoniale e i limiti per i contratti (artt. 2721-2726 c.c.).",
                "Accertare la data certa della scrittura privata e la sua autenticità (artt. 2702-2704 c.c.).",
                "Eccepire la prescrizione del diritto e individuarne la decorrenza (artt. 2934-2935 c.c.).",
                "Distinguere prescrizione e decadenza: la decadenza non è soggetta a interruzione (artt. 2964-2966 c.c.).",
                "Verificare i termini di prescrizione speciali (breve, biennale, quinquennale) applicabili al rapporto.",
                "Chiedere la consulenza tecnica d'ufficio quando i fatti richiedono competenze specialistiche.",
                "Valutare il valore delle presunzioni e degli argomenti di prova (artt. 2727-2729 c.c.).",
            ]
        }
    ];
}