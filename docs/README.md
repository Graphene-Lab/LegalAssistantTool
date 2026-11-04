# Developer documentation — LegalAssistantTool

Technical documentation for the developers who maintain and extend the tool. The user-facing
documentation (data sources, benchmark results, installation, configuration) is in
[`../README.md`](../README.md); this folder covers **how the code is built and why**.

| Document | Read it when you need to… |
|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | Understand the layers, the types and who calls what; find the class that owns a behaviour. |
| [PIPELINE.md](PIPELINE.md) | Change the synchronisation, the normalisation or the index build. |
| [RETRIEVAL.md](RETRIEVAL.md) | Change how articles and judgments are ranked; run and read the benchmark. |
| [OPINION-ENGINE.md](OPINION-ENGINE.md) | Change the opinion pipeline: input gate, attachments, web enrichment, drafting, citation guard, Markdown. |
| [DEVELOPER-GUIDE.md](DEVELOPER-GUIDE.md) | Build, test, benchmark, pack, debug; follow the recipes for the most common changes. |

## The three invariants

Every change must preserve these three properties. They are what makes the tool usable for legal work;
everything else is negotiable.

1. **No invented source.** An article number or a judgment citation reaches the output only if the
   document is in the archive. The model is given a *closed* list of citations and
   `LegalCitationGuard` deletes any sentence that cites something outside it.
2. **Deterministic floor.** With no model available (offline, budget exhausted, provider error) both
   research methods still return a complete document built from the retrieved texts, and a form lookup
   still returns the converted form. The model improves the prose and picks the best form; it never
   gates the answer.
3. **Self-describing failure.** Insufficient input, unsupported jurisdiction, cold archive and offline
   web all produce an explicit, actionable text — never an exception and never a silently empty result.

## Where the log lines are

Every strategic step logs through `AIOrchestrator.Log.LogStep` into `logs/<pid>.txt` (application base directory).
A single opinion produces roughly 40 lines: `LegalService.Prepare`, `LegalService.Laws`/`LegalService.Opinion`, `Research topics`,
`Curated articles`, `Round N on <engine>`, `JudgmentScore`/floor lines, `Web search`,
`Web page <url>: usefulness=`, `Citation guard removed a sentence citing`, `Web enrichment: N pages rated, M used`, and the final
`LegalOpinion: completed (...)`. See [DEVELOPER-GUIDE.md](DEVELOPER-GUIDE.md#5-reading-a-run-in-the-log)
for the order in which to read them.