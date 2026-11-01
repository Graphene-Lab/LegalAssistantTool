# Architecture

## 1. Layers

The tool is a plugin assembly with no static dependency on the host: `AIOrchestrator` supplies
`BaseAgentTool`, `IFileTool`, `Log`, `LLMUtility`, `FileManager` and `SandboxPath`. Everything legal
lives in the `AIOrchestrator.API.Legal` namespace and is `internal`, so the only surface the agent
sees is the two public methods.

```mermaid
flowchart TB
    Agent["LLM agent (host: AIOffice / AgentBridge)"]

    subgraph Public["Public surface"]
        Tool["LegalAssistantTool<br/>GetLaws · LegalOpinion"]
    end

    subgraph Orchestration["Orchestration"]
        Service["LegalService<br/>readiness · jurisdiction · routing"]
        Archive["LegalArchive<br/>sync · nightly · readiness gate"]
        Engine["LegalOpinionEngine<br/>input gate · attachments · drafting"]
        Retriever["LegalRetriever<br/>thesaurus rounds · scoring · floors"]
    end

    subgraph Support["Deterministic support"]
        Thesaurus["LegalThesaurus<br/>topics · rubrics · defence points"]
        Corpus["LegalCorpus<br/>in-memory corpus cache"]
        Index["LegalIndex<br/>SQLite FTS5 + BM25"]
        Lucene["LegalLucene<br/>benchmark engines"]
        Normalizer["LegalNormalizer<br/>JSON/MD → LegalDocument"]
        Web["LegalWebEnrichment<br/>search · rating · reliability"]
        Guard["LegalCitationGuard<br/>closed citation list"]
        Llm["LegalLlm<br/>time budget · JSON answers"]
        Md["LegalMarkdown<br/>Markdown rendering"]
        Text["ItalianText<br/>fold · stem · tokenize"]
    end

    subgraph Persistence["Persistence"]
        Storage["LegalStorage<br/>data root · paths · state.json"]
        Native["LegalNative<br/>e_sqlite3 bootstrap"]
        Disk[("Local disk<br/>raw/ normalized/ index/")]
    end

    Agent --> Tool
    Tool --> Service
    Service --> Archive
    Service --> Retriever
    Service --> Engine
    Engine --> Retriever
    Engine --> Web
    Engine --> Guard
    Engine --> Llm
    Engine --> Md
    Retriever --> Thesaurus
    Retriever --> Corpus
    Retriever --> Index
    Retriever --> Lucene
    Retriever --> Text
    Archive --> Normalizer
    Archive --> Index
    Archive --> Corpus
    Archive --> Storage
    Index --> Storage
    Index --> Native
    Storage --> Disk
```

## 2. Component reference

| Type | File | Responsibility | Key members |
|---|---|---|---|
| `LegalAssistantTool` | `LegalAssistantTool.cs` | Agent surface, logging, exception → `Error:` text. | `GetLaws`, `LegalOpinion` |
| `LegalService` | `Legal/LegalService.cs` | Jurisdiction check, readiness wait, routing to retrieval or opinion. | `Prepare`, `CheckJurisdiction`, `Laws`, `Opinion`, `ReadyWait` |
| `LegalArchive` | `Legal/LegalArchive.cs` | Commit probe, ZIP download/extract, rebuild decision, nightly loop, readiness gate. | `EnsureReady`, `StartSync`, `SyncNow`, `NextNightlyRun`, `FormatVersion`, `NightlyHour` |
| `LegalStorage` | `Legal/LegalStorage.cs` | Data-root resolution and every path; `state.json` read/write. | `Root`, `SqlitePath`, `ArticlesJsonl`, `ReadState`, `WriteState` |
| `LegalNormalizer` | `Legal/LegalNormalizer.cs` | Maps both repositories to `LegalDocument` and writes JSONL. | `NormalizeAll`, `NormalizeArticles`, `NormalizeJudgments` |
| `LegalCorpus` | `Legal/LegalCorpus.cs` | Lazy, cached in-memory corpus loaded from JSONL. | `Load`, `ById`, `Article`, `Invalidate` |
| `LegalIndex` | `Legal/LegalIndex.cs` | FTS5 table creation, insert, BM25 search, MATCH expression. | `Build`, `Search`, `Count`, `BuildMatchExpression` |
| `LegalLucene` | `Legal/LegalLucene.cs` | Optional Lucene indexes used by the benchmark. | `Build`, `Search`, `CreateAnalyzer`, `DefaultEngine` |
| `LegalRetriever` | `Legal/LegalRetriever.cs` | Research orchestration: topics, rounds, scoring, floors, diversification; engine registry. | `Research`, `CuratedArticles`, `JudgmentScore`, `ArticleAffinity`, `LegalSearchEngines.Resolve` |
| `LegalResearch` | `Legal/LegalRetriever.cs` | Accumulator for one research: hits with weights, curated numbers, queries. | `AddArticles`, `AddJudgments`, `BoostCurated`, `EnsureCurated`, `ApplyRelevanceFloor`, `Finalize` |
| `LegalThesaurus` | `Legal/LegalThesaurus.cs` | Curated knowledge base: 23 topics with triggers, curated articles per branch, expansion terms, defence points. | `Topics`, `Match`, `ExpansionTerms`, `DefencePoints` |
| `LegalOpinionEngine` | `Legal/LegalOpinionEngine.cs` | Input gate, attachment budget, research, web enrichment, drafting, report assembly. | `CheckInput`, `Opinion` |
| `LegalAttachments` | `Legal/LegalAttachments.cs` | Reads attachments through the sandbox (PDF, DOCX, HTML, text). | `Read`, `HtmlToText` |
| `LegalWebEnrichment` | `Legal/LegalWebEnrichment.cs` | Web search + per-page usefulness rating + source reliability; silently skipped offline. | `Enrich`, `ReliabilityOf` |
| `LegalLlm` | `Legal/LegalLlm.cs` | Single entry point for model calls: global time budget, JSON parsing, summarisation. | `Ask`, `AskJson`, `Summarize`, `AttachmentBudget`, `Reset` |
| `LegalCitationGuard` | `Legal/LegalCitationGuard.cs` | Sentence-level audit against the closed citation list. | `Audit`, `JudgmentKey` |
| `LegalMarkdown` | `Legal/LegalMarkdown.cs` | Renders `GetLaws`, the 12-section opinion and the insufficient-input document. | `RenderLaws`, `RenderOpinion`, `RenderInsufficientInput` |
| `ItalianText` | `Legal/ItalianText.cs` | Fold, stem, tokenize, coverage, sentence split, clipping. | `Normalize`, `Stem`, `Tokenize`, `Coverage`, `Sentences` |
| `LegalNative` | `Legal/LegalNative.cs` | Loads the bundled `e_sqlite3` from the plugin folder by absolute path. | `EnsureSqlite` |
| `LegalModel` | `Legal/LegalModel.cs` | `LegalJurisdiction`, `CaseType`, `LegalDocKind`, `SourceReliability`, `LegalDocument`. | — |

## 3. Object model

```mermaid
classDiagram
    class LegalDocument {
        +string Id
        +LegalDocKind Kind
        +CaseType? CaseType
        +string Heading
        +string Body
        +string Citation
        +string StateCode
        +string StateLabel
        +bool NotApplicable
        +bool IsCitable
        +string SearchText
        +string Url
        +string PdfUrl
        +string Court
        +string Matter
        +string DecisionDate
        +string CodeId
        +string ArticleNumber
        +string Section
        +string JudgmentType
        +bool ServesCase(CaseType)
        +string NumberAndRubric()
    }
    class LegalHit {
        +LegalDocument Document
        +double Score
    }
    class LegalResearch {
        +CaseType CaseType
        +List~LegalDocument~ Articles
        +List~LegalDocument~ Judgments
        +List~LegalTopic~ Topics
        +List~string~ Queries
        +HashSet~string~ CuratedNumbers
        +AddArticles()
        +AddJudgments()
        +BoostCurated()
        +EnsureCurated()
        +ApplyRelevanceFloor()
        +Finalize()
        +bool IsEmpty
    }
    class LegalTopic {
        +string Id
        +string Name
        +CaseType? Branch
        +string[] Triggers
        +string[] Expand
        +string[] CivilArticles
        +string[] CriminalArticles
        +string[] DefencePoints
        +ArticlesFor(CaseType)
    }
    class ILegalSearchEngine {
        <<interface>>
        +string Id
        +Search(query, caseType, limit, kind, court)
    }
    class Fts5LegalSearchEngine
    class LuceneLegalSearchEngine
    class LuceneStandardLegalSearchEngine

    LegalResearch "1" o-- "*" LegalHit
    LegalHit --> LegalDocument
    LegalResearch "1" o-- "*" LegalTopic
    ILegalSearchEngine <|.. Fts5LegalSearchEngine
    ILegalSearchEngine <|.. LuceneLegalSearchEngine
    ILegalSearchEngine <|.. LuceneStandardLegalSearchEngine
```

## 4. Runtime lifecycle

The first call of a fresh installation performs the whole cold install inline, then answers. Every
later call is served from the local index.

```mermaid
sequenceDiagram
    autonumber
    participant A as Agent
    participant T as LegalAssistantTool
    participant S as LegalService
    participant Ar as LegalArchive
    participant N as LegalNormalizer
    participant I as LegalIndex
    participant R as LegalRetriever
    participant M as LegalMarkdown

    A->>T: GetLaws(IT, Criminal, "furto aggravato")
    T->>S: Laws(...)
    S->>S: CheckJurisdiction → null (IT)
    S->>Ar: EnsureReady(ReadyWait)
    alt archive ready
        Ar-->>S: Ready
    else cold or stale
        Ar->>Ar: SyncCore(): commit probe, ZIP download, extract
        Ar->>N: NormalizeAll(leggi, giuri)
        N-->>Ar: counts (4,251 articles, 23,049 judgments)
        Ar->>I: Build(corpus)
        Ar-->>S: Ready (or Preparing on timeout)
    end
    S->>R: Research(caseType, topic, rounds: 2)
    R-->>S: LegalResearch (articles + judgments)
    S->>M: RenderLaws(research, topic, ...)
    M-->>S: Markdown
    S-->>T: Markdown
    T-->>A: Markdown + log lines
```

Measured cold install on a normal connection: **313 s** (62 s download, 238 s normalisation, 13 s FTS5
build). Warm calls take 0.4–2.6 s. `LEGALASSISTANTTOOL_READY_SECONDS` (default 360, clamp 30–900)
bounds how long a call waits; past it the call returns a "still preparing" text while the background
sync continues.

```mermaid
stateDiagram-v2
    [*] --> Unavailable: no usable state
    Unavailable --> Preparing: EnsureReady starts SyncCore
    Preparing --> Ready: normalise + index completed
    Preparing --> Unavailable: download failed / process exited
    Ready --> Preparing: nightly refresh (03:30 local) or stale format
    Ready --> Ready: query (warm)
    Ready --> Unavailable: state.json unreadable and corpus missing
```

`ArchiveStatus` values map to the three texts produced by `LegalService.Prepare`: `Ready` → no error,
`Preparing` → "still being prepared, retry in a couple of minutes", `Unavailable` → "check the
internet connection and retry".

## 5. Storage layout

The data root is `%LOCALAPPDATA%\Graphene\LegalAssistantTool` on Windows and
`~/.local/share/Graphene/LegalAssistantTool` elsewhere; `LEGALASSISTANTTOOL_DATA` overrides it (the
test harness points it at `<project>\.data`). Nothing is written next to the executable or inside the
plugin folder — the plugin folder is replaced on update.

```text
<root>/
├── state.json               # commits, counts, format version, index metadata
├── raw/
│   ├── leggi-italiane/      # extracted ZIP (~8 MB)
│   └── giurisprudenza-db/   # extracted ZIP (~54 MB)
├── normalized/
│   ├── articles.jsonl       # one LegalDocument per line
│   └── judgments.jsonl
└── index/
    ├── legal.db             # SQLite + FTS5 (the index the tool searches)
    └── lucene-<engine>/     # only when LEGALASSISTANTTOOL_LUCENE=1 (benchmark)
```

Total footprint ≈466 MB. There is **no database server and no configuration file**: `legal.db` is a
single embedded SQLite file created on demand with `SqliteOpenMode.ReadWriteCreate`, and the native
`e_sqlite3` travels inside the plugin package.

## 6. Concurrency and caching

| Mechanism | Where | Purpose |
|---|---|---|
| `SemaphoreSlim Gate` | `LegalArchive` | Serialises syncs: concurrent tool calls never download twice. |
| `_sync` / `_nightly` tasks | `LegalArchive` | One in-flight sync task and one nightly loop per process. |
| `_progress` (volatile string) | `LegalArchive` | Human-readable progress shown in the "still preparing" text. |
| `_indexLoaded` | `LegalArchive` | Loads the corpus into memory once per process. |
| `LegalCorpus` cache | `LegalCorpus` | `Load(forceReload)` + `Invalidate()` after a rebuild. |
| `LegalLlm.Reset()` + `RemainingSeconds` | `LegalLlm` | Per-call model time budget; every call re-checks it. |
| Pooled read-only connections | `LegalIndex` | Each search opens a `SqliteOpenMode.ReadOnly` pooled connection; writers only exist during a rebuild. |

## 7. Error philosophy

* The two public methods never throw: every exception becomes `"Error: …"` with the cause and a next
  action (see `LegalAssistantTool`).
* Optional stages degrade silently: web enrichment (no connection, blocked host, unreadable page) and
  model drafting (no provider, budget exhausted, invalid JSON) log a line and the deterministic path
  continues.
* Required stages fail loudly but politely: no archive after the wait → "still preparing"; empty facts
  → insufficient-input document; non-`IT` jurisdiction → deterministic error.

## 8. Plugin loading

`ToolPluginHost` (`AIOrchestrator/ToolPluginHost.cs`) resolves only **managed** assemblies from
`Tools\<Plugin>\` through `AssemblyLoadContext.Default.Resolving`, and host assemblies win. That is why
`LegalNative.EnsureSqlite()` loads the bundled native `e_sqlite3.dll` by absolute path from the plugin
folder instead of relying on the default native probe, and why the plugin folder ships its own
`Microsoft.Data.Sqlite`, `SQLitePCLRaw.*`, `Lucene.Net*`, `J2N`, `UglyToad.PdfPig*` — but never
`AIOrchestrator.dll`.