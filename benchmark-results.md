# Retrieval benchmark

Corpus: **4,251 articles**, **23,049 judgments** (Italian civil and criminal sources).
Gold set: **35 queries** (20 criminal, 15 civil), hand-labelled with the articles a lawyer opens for that fact pattern.

| Engine | P@5 | R@10 | MRR@10 | Case-law affinity@10 | Latency |
|---|---|---|---|---|---|
| Hybrid: curated topics + relevance floor + court mix (FTS5) | 0.274 | 0.814 | 0.760 | 0.280 | 809.5 ms |
| SQLite FTS5 + BM25 (Italian fold+stem) | 0.217 | 0.576 | 0.663 | 0.237 | 197.0 ms |
| Lucene.Net 4.8 BM25 + ItalianAnalyzer | 0.097 | 0.281 | 0.268 | 0.073 | 18.3 ms |
| Lucene.Net 4.8 BM25 + StandardAnalyzer | 0.011 | 0.024 | 0.034 | 0.016 | 17.3 ms |

- **P@5** — share of the first five articles that are relevant.
- **R@10** — share of the relevant articles found in the first ten.
- **MRR@10** — mean reciprocal rank of the first relevant article.
- **Case-law affinity@10** — share of the first ten judgments that cite at least one relevant article of the case: it measures whether the retrieved case law is anchored to the right norms, not just topically similar.
- **Latency** — mean per query on the machine that produced this report.

Winner: **Hybrid: curated topics + relevance floor + court mix (FTS5)**.
