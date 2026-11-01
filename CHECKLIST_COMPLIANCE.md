# LegalAssistantTool — TOOL_CHECKLIST Compliance

Checked 2026-11-01 against `AIOrchestrator/API/TOOL_CHECKLIST.md` (current version incl. the
"Completion — compliance file" point). `LegalAssistantTool` is an offline legal-research agent
tool: it serves `get_laws` and `legal_opinion`, reads the case documents the agent attaches, and
keeps its archive, corpus and indexes under the user's local app-data folder.

## Release & layout (plugin tools)

- OK — csproj `<Version>$([System.DateTime]::Now.ToString("1.yy.MM.dd"))</Version>`: a push on a new day produces a new version; the same value is emitted as `NuGetPackageId`/`NuGetPackageVersion` assembly metadata for `PluginUpdater`.
- OK — both channels on `v*` tags: `.github/workflows/plugin-release.yml` (self-contained zip for hosts, `Tools/LegalAssistantTool/`) and `.github/workflows/publish.yml` (`dotnet pack` + `dotnet nuget push` of `Graphene.LegalAssistantTool`).
- OK — AIOrchestrator referenced as a sibling (`..\AIOrchestrator\AIOrchestrator.csproj` ProjectReference when the repo is present, `Graphene.AIOrchestrator` 1.* package otherwise, for CI/standalone builds); it is never copied into the plugin tree.
- OK — the plugin folder never ships `AIOrchestrator.dll` or its dependency graph: the host copies only `LegalAssistantTool.*` plus the plugin's unique packages, listed one package at a time (`Lucene.Net*`, `J2N`, `Microsoft.Data.Sqlite`, `SQLitePCLRaw.*`, `UglyToad.PdfPig*`) and only when the host bin does not already provide them; `plugin-release.yml` builds the zip from the plugin nupkg plus the restore graph minus the AIOrchestrator closure.
- OK — the archive, the normalised corpus, the indexes and `state.json` live under `%LOCALAPPDATA%\Graphene\LegalAssistantTool` (`LegalStorage`, overridable with `LEGALASSISTANTTOOL_DATA`), never next to the host executable and never inside `Tools\LegalAssistantTool\`, which is replaced on update.
- OK — no external database and no configuration: the index is an embedded SQLite/FTS5 file created on first use (`ReadWriteCreate`), the `e_sqlite3` native library ships in the package and is loaded by absolute path from the plugin folder (`LegalNative`), and there is no server, connection string, migration or config file. Uninstall = delete the data folder. The only external requirement is internet for the first download and the nightly refresh.
- OK — independent of the host launch directory: every path derives from `LegalStorage.Root` (app-data) or from `SandboxPath` (attachments); nothing uses the process CWD. The native SQLite library is located from the plugin assembly folder (`LegalNative`), not from the launch directory.

## Agent-facing descriptions

- OK — descriptions never mention internals: no repository sync, no ZIP download, no FTS5/Lucene/BM25, no prompt pipeline, no summarisation, no model, no nightly scheduler, no cache or index format.
- OK — minimal: two methods, `<summary>` one sentence each, one `<param>` line per parameter, one `<returns>` line; the class `<remarks>` carries only the cross-method rules.
- OK — methods state the outcome (articles in force and judgments on a topic; a reasoned opinion), never how it is obtained.
- OK — nothing tells the agent it is in a sandbox or that paths are virtual; attachments are described as "workspace-relative paths".
- OK — `attachments` is the only path-bearing parameter and is documented as workspace-relative; no absolute or host path is ever emitted in the Markdown result (the "Fonti e verificabilità" block carries public source URLs only).
- OK — every public method has `<summary>`, `<param>` for each parameter and `<returns>` covering both the normal result and the `Error:` form.
- OK — class summary is one line (competency) plus cross-method rules (Italy only; cite only what the tool returns; which method to use for what), and does not explain the methods.
- OK — no redundancy: parameter formats (ISO 3166-1 alpha-2 code, PDF/DOCX/HTML/text, Italian legal terms) live only in `<param>`; the outcome shape lives only in `<returns>`.
- OK — one instruction per line; every `///` line is one continuous source line.
- OK — formats specified: `jurisdiction` as a two-letter ISO 3166-1 alpha-2 code with the only accepted value (`IT`), `caseType` as the `Civil`/`Criminal` enum, attachment formats, topic in Italian legal terms, the insufficient-input answer.
- OK — closed value sets are enums, not strings: `LegalJurisdiction { IT }` and `CaseType { Civil, Criminal }` are in the signature and therefore in the generated scheme; no `ToString()`-and-reparse anywhere (the branch is used as a typed value).
- OK — cross-references: `attachments` is documented as the workspace-relative paths of the case documents, i.e. what the file tools return.
- OK — method-name references in descriptions are natural (`get_laws`, `legal_opinion`); the generator normalises them.
- OK — runtime messages never hardcode a PascalCase method name: the `Error:` returns name no method, and no log line is surfaced to the agent.
- OK — errors are actionable and prefixed: `Error: the legal lookup failed (<type>: <message>). Retry; if the failure persists the local archive may need to be rebuilt.` (same shape for the opinion).
- OK — no `[[name]]` dynamic placeholders in any description, so no `Analyzer.DynamicDescriptionRequested` registration is needed.

## Surface & sandbox

- OK — only the two agent operations are public on the tool class; the whole engine (`LegalService`, `LegalArchive`, `LegalRetriever`, `LegalIndex`, `LegalLucene`, `LegalOpinionEngine`, `LegalStorage`, …) is `internal`.
- OK — attachments are resolved with `SandboxPath.TryResolve`; a path outside the workspace is not read and becomes a note in the result ("percorso fuori dal workspace: allegato ignorato") instead of an error. No host path is echoed back.
- OK — no public method exposes credentials, configuration or host paths; the data root, the model endpoint and the LLM budget are internal settings, never returned.

## Code & conventions

- OK — class name ends with `Tool`; package id `Graphene.LegalAssistantTool`.
- OK — `GetLaws` and `LegalOpinion` are declared directly on `LegalAssistantTool`; the only inherited public member is `BaseAgentTool.LoadSkill` (no skills shipped, so not overridden).
- OK — `Log.LogStep()` at entry, outcome and failure of both public methods, plus the strategic steps of the pipeline (sync, normalisation, index build, retrieval rounds, rating, citation audit) for the test log review.
- OK — derives from `BaseAgentTool` and implements `IFileTool` (it reads the attached case documents).
- OK — `[ToolPurpose(ToolPurpose.Other)]`: no other tool performs legal research, so the purpose is declared and the overlap rule is satisfied.
- OK — `GenerateDocumentationFile=True`: tool definitions are read from `LegalAssistantTool.xml` shipped next to the dll (also inside the nupkg `lib/net10.0/`).

## Standardized support — shared AIOrchestrator helpers

- N/A — the tool never describes or previews a file's content to the agent: attachments are read to build the opinion and reported only as legal content (name, how the text was obtained, whether it was summarised), so `FileManager.GetFileInfo`/`GetFilesInfo` would add host metadata the opinion does not need.
- N/A — the tool creates and modifies no file, so `GitSupport.Snapshot` does not apply; the archive is a private cache under app-data, not a user document, and rollback stays in `GitTool`.
- N/A — no file is created or modified, so there is no sandbox-relative path to return.
- OK — model output is cleaned with `Utility.RemoveFencesEncapsulationAndFixTrim(ref text)` in `LegalLlm.Ask` before any parsing (JSON extraction and the opinion draft).
- N/A — the tool emits Markdown, never HTML/SVG: `Utility.EmbedSvgIcons` not applicable.
- N/A — Italian-only by design (`LegalJurisdiction.IT`): no language detection, `Utility.DetectLanguage` not applicable.
- OK — attachment paths go through `SandboxPath.Resolve`/`TryResolve`; no `Path.GetFullPath` is applied to an agent-supplied path (see "Surface & sandbox").

## Documentation

- OK — `README.md` is the public reference (sources, benchmark, normalisation, anti-hallucination, output format, installation, configuration, versioning, limits, licence).
- OK — `docs/README.md` is the index; `docs/ARCHITECTURE.md`, `docs/PIPELINE.md`, `docs/RETRIEVAL.md`, `docs/OPINION-ENGINE.md` and `docs/DEVELOPER-GUIDE.md` cover the components, the sync/normalisation/index pipeline, retrieval and its benchmark, the opinion engine, and the change recipes, with Mermaid diagrams for every non-trivial flow.
- OK — the developer documentation is updated in the same commit as the code it describes; `docs/DEVELOPER-GUIDE.md` §"Reading a run in the log" is keyed to the real `Log.LogStep` strings, so a renamed log line is a documentation defect.
- OK — `LICENSE.md` (Andrea Bruno License 1.4, copied verbatim from `UISupportGeneric`) ships at the project root, is referenced by `<PackageLicenseFile>` and is included in the nupkg.
- OK — third-party data obligations are stated in `LICENSE.md` and in `README.md` §13: `leggi-italiane` (CC BY 4.0) and the `giurisprudenza-db` schede (CC BY 4.0) plus the Consulta open-data subset (CC BY-SA 3.0).
## Completion — compliance file

- OK — this file ships at the repository root of the plugin and states every checklist point above, each with its justification; it is updated whenever the checklist or the tool changes.
