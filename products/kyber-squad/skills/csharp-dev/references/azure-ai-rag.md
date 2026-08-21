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

## Current Architecture (CRITICAL — Read Before Coding)

This project uses **direct Azure SDK integrations** alongside the **Microsoft Agent Framework** (https://github.com/microsoft/agent-framework). Do not introduce Semantic Kernel.

### SDK Packages in Use
- `Azure.AI.OpenAI` — Chat completions, embeddings, vision
- `Azure.Search.Documents` — Hybrid vector/keyword search, semantic ranking
- `Azure.AI.DocumentIntelligence` — PDF layout extraction, OCR, table detection
- `HtmlAgilityPack` — Web content extraction

### Agent System
The system implements a custom multi-agent RAG pattern:

| Agent | Location | Purpose |
|---|---|---|
| `QueryPlannerAgent` | Application / Agents | Analyzes queries via Azure OpenAI chat completions, generates search strategies |
| `VectorSearchAgent` | Application / Agents | Hybrid vector/keyword search via Azure AI Search |
| `WebSearchAgent` | Application / Agents | Trusted source web scraping with credibility scoring |
| `AgentOrchestrator` | Application / Agents | Coordinates sequential agent execution (Vector → Web → PDF) |

### Agent Framework Components
| Component | Purpose |
|---|---|
| `AgentFrameworkAdapter` | Bridge between agents and tool execution |
| `AgentState` | Execution context, message history, result accumulation |
| `ToolDefinitions` | Callable tools: `vector_search`, `web_search`, `pdf_search`, `plan_search_strategy` |

### Persistence Wrappers
| Wrapper | Location | Purpose |
|---|---|---|
| `AzureOpenAIClientWrapper` | Persistence / Azure | Resilient Azure OpenAI access with retries and mock support |
| `AzureSearchClientWrapper` | Persistence / Azure | Azure AI Search operations with health checks and batch indexing |

---

## Patterns to Follow

### Embedding Generation
- **Model**: `text-embedding-3-large` (3072 dimensions)
- **Batch size**: 100 embeddings per API call (CSV), 10 per call (PDF)
- **Content format**: `"Field: Value | Field: Value"` for structured data
- **Error handling**: Retry on transient failures, continue on individual chunk failures

### Azure AI Search Index
- **Index name**: `SearchOptions.IndexName` is the sole index reference — do not hardcode a host-specific name; the current configuration may be a single index or partitioned per category
- **Search type**: Hybrid (vector + keyword + semantic ranking)
- **Vector algorithm**: HNSW (m=4, efConstruction=400, efSearch=500)
- **Semantic ranker**: Re-ranks top 50 results
- **Batch indexing**: 100 documents per operation with retry

### Data Ingestion
- **CSV**: Row-based chunking preserving relational integrity (grouped by Make/Model/Year), 100 rows per chunk
- **PDF**: Semantic chunking via Azure Document Intelligence layout model, 500-1500 token chunks with 200 token overlap, GPT-4 Vision for diagrams
- **Web**: Trusted source scraping with credibility scores (0.7 minimum), rate-limited (5 concurrent, 500ms interval), 1-hour cache TTL

### Result Fusion
- Sequential execution: Vector → Web → PDF
- Deduplication by document ID
- Semantic re-ranking with `text-embedding-3-large`
- Response generation via GPT-4o-mini

### Resilience
- All Azure SDK wrappers use Polly policies (retries, circuit breakers)
- Health checks on Azure services
- Graceful degradation when individual agents fail

---

## MUST NOT
- Introduce `Microsoft.SemanticKernel`
- Use direct Azure SDK calls outside the persistence wrapper layer — always go through `AzureOpenAIClientWrapper` / `AzureSearchClientWrapper`
- Hardcode Azure endpoints, keys, or connection strings — use environment variables
- Skip credibility validation for web search results
- Bypass rate limiting on web scraping operations
- Create new Azure AI Search indexes without approval — use the index named by `SearchOptions.IndexName`

## MUST DO
- Follow the wrapper pattern: Application layer calls wrapper interfaces, Persistence implements them
- Use parameterized tool definitions for agent communication
- Track execution state via `AgentState` for all agent operations
- Include citation tracking (page numbers, sections, figures) for PDF results
- Preserve document hierarchy (chapters/sections) in chunked content
- Log all agent operations with structured logging and operation context
