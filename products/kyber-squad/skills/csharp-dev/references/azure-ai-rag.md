---
name: azure-ai-rag
description: Azure AI / RAG development patterns (Azure OpenAI, Azure AI Search, ingestion, and agent orchestration).
license: MIT
metadata:
  author: David R Palfery
  version: 1.0.0
---

# Azure AI / RAG Development Patterns

**Trigger:**
This skill MUST be loaded whenever working on:
- Multi-agent search orchestration or agent coordination.
- Azure OpenAI integration (chat completions, embeddings, vision).
- Azure AI Search indexing, hybrid search, or semantic ranking.
- Data ingestion pipelines (CSV, PDF, Web).
- Query planning, result fusion, or response generation.
- Azure OpenAI, Azure AI Search, ingestion, or agent-orchestration code under Application or Persistence.

---

## SDK Packages in Use
- `Azure.AI.OpenAI` — Chat completions, embeddings, vision
- `Azure.Search.Documents` — Hybrid vector/keyword search, semantic ranking
- `Azure.AI.DocumentIntelligence` — PDF layout extraction, OCR, table detection
- `HtmlAgilityPack` — Web content extraction

The agent-orchestration framework, the concrete agent classes, and any persistence-wrapper
types are a host's own architecture decision, not portable technique; this skill does not
prescribe them.

---

## Patterns to Follow

### Embedding Generation
- **Batch size**: batch embeddings per API call rather than one call per item — larger
  batches for CSV rows, smaller for PDF chunks
- **Content format**: `"Field: Value | Field: Value"` for structured data
- **Error handling**: Retry on transient failures, continue on individual chunk failures

### Azure AI Search Index
- **Index name**: `SearchOptions.IndexName` is the sole index reference — do not hardcode a host-specific name; the current configuration may be a single index or partitioned per category
- **Search type**: Hybrid (vector + keyword + semantic ranking)
- **Semantic ranker**: Re-ranks the top search results
- **Batch indexing**: 100 documents per operation with retry

### Data Ingestion
- **CSV**: Row-based chunking preserving relational integrity (grouped by a natural key), 100 rows per chunk
- **PDF**: Semantic chunking via a layout-aware model, 500-1500 token chunks with 200 token overlap so context is not lost at a chunk boundary, vision-model support for diagrams
- **Web**: Trusted source scraping with credibility scores (0.7 minimum), rate-limited (5 concurrent, 500ms interval), 1-hour cache TTL

### Result Fusion
- Sequential execution across result sources
- Deduplication by document ID
- Semantic re-ranking of the fused results
- Response generation with citations back to source chunks

### Resilience
- Transient calls use retry policies, with circuit breakers where call volume warrants one
- Health checks on Azure services
- Graceful degradation when an individual source fails

---

## MUST NOT
- Azure secrets — endpoints, keys, connection strings — follow **<csharp-coding-standard>** § Safety
- Skip credibility validation for web search results
- Bypass rate limiting on web scraping operations
- Create new Azure AI Search indexes without approval — use the index named by `SearchOptions.IndexName`

## MUST DO
- Use parameterized tool definitions for agent communication
- Include citation tracking (page numbers, sections, figures) for PDF results
- Preserve document hierarchy (chapters/sections) in chunked content
- Log all agent operations with structured logging and operation context
