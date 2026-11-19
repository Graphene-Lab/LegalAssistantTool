# LegalAssistantTool — notes for coding agents

> Read this file first. This repo is one tool plugin of the AIOffice solution: it is compiled on
> its own and loaded by the hosts from `Tools/LegalAssistantTool/` at runtime.

## What this is

Italian legal research: in-force code articles and case law from a **locally synchronised
archive**, and a grounded legal opinion whose citations are all verifiable.

## Read before you edit

| If you touch… | Read first |
|---|---|
| anything | [PLUGIN-GUARDRAILS](../AIOrchestrator/docs-dev/PLUGIN-GUARDRAILS.md) — the contract shared by the 12 plugins; the file lives in the sibling AIOrchestrator repo (private), reachable from the solution checkout |
| the overall design | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and [docs/DEVELOPER-GUIDE.md](docs/DEVELOPER-GUIDE.md) |
| retrieval, the archive, the index | [docs/RETRIEVAL.md](docs/RETRIEVAL.md), [docs/PIPELINE.md](docs/PIPELINE.md) |
| the opinion writer | [docs/OPINION-ENGINE.md](docs/OPINION-ENGINE.md) |
| a tool method or its XML docs | [AGENT_TOOLS_GUIDE](../AIOrchestrator/API/AGENT_TOOLS_GUIDE.md) |

## Non-negotiables

1. The class derives from `BaseAgentTool` and keeps `[ToolPurpose(ToolPurpose.Legal)]`.
2. Public methods and their XML docs **are** the agent's surface: no public method without docs,
   and a public method is never renamed (the model calls it by that name).
3. AnyCPU, no `RuntimeIdentifier`, `GenerateDocumentationFile` stays on.
4. The plugin folder never carries `AIOrchestrator.dll`; no state is written into `Tools/`.
5. **Every citation must exist in the local archive.** A plausible-looking citation that cannot be
   resolved is the one failure this tool must not have; the opinion path is grounded by
   construction (`docs/OPINION-ENGINE.md`).
6. **The non-host dependencies are listed one by one** in `AIOffice.csproj` (Lucene, SQLite/FTS5,
   `UglyToad.PdfPig`, `e_sqlite3` native). Do not replace them with a `**\*` glob: the plugin's bin
   also contains the host's own assets.
7. A new metadata goes in a class attribute (or a `virtual` member with a default) — never an
   `abstract` member: the dlls already shipped must keep loading.

## Where things are

- `LegalAssistantTool.cs` — the tool class; its public methods are what the model can call
- `Legal/` — the legal-domain model and the archive handling
- `docs/` — architecture, retrieval, pipeline, opinion engine, developer guide
- `.data/` — the local archive/index working data
- `LegalAssistantTool.Tests/`, `.Benchmark/`, `.Puppet/` — nested projects, excluded from the
  library compile

## Shipping

Publishes a GitHub release zip **and** a NuGet package. AIOffice builds it into
`Tools/LegalAssistantTool` on every dev build; AgentBridge does not fetch it.
