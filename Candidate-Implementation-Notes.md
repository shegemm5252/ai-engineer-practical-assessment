# Candidate Implementation Notes & Architecture Summary

Please use this document to explain your technical design decisions, trade-offs, and scaling considerations. This provides the evaluation committee with direct insight into your engineering thought process.

---

## 1. Multi-Stage AI Agent Architecture & Orchestration

### Q1.1 — Describe how you structured and initialized `ExpenseClassifierAgent`.

**Answer:**

`ExpenseClassifierAgent` implements `IExpenseClassifierAgent` and is constructed via DI with:

- `IChatClient` — LLM transport (Azure OpenAI or `SimulationChatClient`)
- `CompanyPolicyTool` / `SpendingLimitTool` — policy and spending-limit tools
- `IMemoryCache` — cache-aside layer for classification results
- `ILogger<ExpenseClassifierAgent>` — structured logging

On construction, the agent **pre-builds** two reusable `ChatOptions` instances:

| Options | Purpose |
|---------|---------|
| `_primaryAgentOptions` | Stage 1 — tools bound (`GetPolicy`, `GetSpendingLimits`) |
| `_fallbackAgentOptions` | Stage 2 — empty options (no tools) |

This avoids rebuilding tool metadata on every request.

---

### Q1.2 — How did you register and bind `CompanyPolicyTool` and `SpendingLimitTool` to the primary agent?

**Answer:**

Tools are registered as **singletons** in `Program.cs`, then injected into the agent. In the agent constructor they are bound once with Microsoft.Extensions.AI:

```csharp
var tools = new List<AITool>
{
    AIFunctionFactory.Create(_policyTool.GetPolicy, "GetPolicy"),
    AIFunctionFactory.Create(_spendingLimitTool.GetSpendingLimits, "GetSpendingLimits")
};

_primaryAgentOptions = new ChatOptions { Tools = tools };
_fallbackAgentOptions = new ChatOptions(); // no tools
```

The primary system prompt instructs the model to always call `GetPolicy` and `GetSpendingLimits`. The fallback prompt classifies broader categories without tools.

---

### Q1.3 — How did you ensure thread-safety, avoid per-request reflection overhead, and manage the transition/fallback to the secondary general agent?

**Answer:**

**Thread-safety**

- Tools are stateless string-returning helpers and are safe as singletons.
- `IMemoryCache` is thread-safe and shared application-wide.
- The agent is **scoped** (one instance per HTTP request), so concurrent requests do not share mutable agent state.
- Batch classification writes into a pre-sized `ResponseDto[]` by index, avoiding shared mutable contention.

**Avoiding per-request reflection overhead**

- `AIFunctionFactory.Create(...)` runs **once in the constructor**.
- Reusable `_primaryAgentOptions` / `_fallbackAgentOptions` are reused for every classification in that scope.

**Primary → secondary (fallback) transition**

1. **Stage 1** — primary policy agent with tools.
2. If the primary result is `null` (empty/malformed/error) **or** `Category == "Other"`, run **Stage 2** — fallback general agent (no tools).
3. If both fail, emit a deterministic default: category `"Other"`, `ComplianceStatus = "Unverifiable"`, stage `"FallbackGeneralAgent"`.
4. Successful paths set `ClassificationStage` to `"PolicyAgentWithTools"` or `"FallbackGeneralAgent"` (cache hits use `"CacheHit"`).

---

### Q1.4 — How did you achieve strictly typed, deterministic structured output (`ResponseDto`) from both stages?

**Answer:**

Both stages enforce a fixed JSON schema in the system prompts and instruct the model to return **raw JSON only** (no markdown fences).

Pipeline:

1. `GetResponseAsync` → assistant text
2. `CleanJsonResponse` strips accidental ``` / ```json wrappers
3. `JsonSerializer.Deserialize<ResponseDto>` with camelCase + case-insensitive property names
4. Deserialization / empty / exception → `null` → triggers fallback or default response

This yields a strongly typed `ResponseDto` at the API boundary (category, subcategory, amount, currency, merchant, confidence, compliance fields, stage, and original description).

---

## 2. Dependency Injection & Service Lifetime Strategy

### Q2.1 — Summarize your DI configuration in `Program.cs`.

**Answer:**

```text
AddMemoryCache()
Singleton  CompanyPolicyTool
Singleton  SpendingLimitTool
if UseSimulation:
    Singleton  IChatClient → SimulationChatClient
else:
    Scoped     AzureOpenAIClient
    Scoped     IChatClient → Azure OpenAI AsIChatClient()
Scoped     IExpenseClassifierAgent → ExpenseClassifierAgent
```

Minimal APIs:

- `POST /classify` — single expense
- `POST /classify/batch` — batch with aggregate summary

---

### Q2.2 — What lifetimes did you choose for `AzureOpenAIClient`, `IChatClient`, `CompanyPolicyTool`, `SpendingLimitTool`, and `IExpenseClassifierAgent`, and why?

**Answer:**

| Service | Lifetime | Why |
|---------|----------|-----|
| `IMemoryCache` | Singleton (via `AddMemoryCache`) | Shared cache across requests |
| `CompanyPolicyTool` | Singleton | Stateless; safe to share; avoids reallocation |
| `SpendingLimitTool` | Singleton | Same as policy tool |
| `SimulationChatClient` (`IChatClient`) | Singleton | Deterministic, no credentials; one shared simulator |
| `AzureOpenAIClient` | Scoped | Ties client lifetime to the request; avoids holding long-lived credential/client state incorrectly |
| `IChatClient` (Azure) | Scoped | Created from the scoped `AzureOpenAIClient` + deployment name |
| `IExpenseClassifierAgent` | Scoped | Fresh agent + pre-bound options per request; aligns with scoped chat client |

**Trade-off:** Binding tools in a scoped agent constructor means tool factories run once per request, not once per process. That is still far cheaper than reflecting on every LLM call, and keeps chat-client lifetimes aligned. Promoting the agent to singleton would require a singleton-safe `IChatClient` (or a factory).

---

### Q2.3 — How does your solution support offline local development / CI via `SimulationChatClient`?

**Answer:**

Controlled by config:

```json
"ExpenseClassifier": {
  "UseSimulation": true
}
```

When `true`, DI registers `SimulationChatClient` instead of Azure OpenAI. That client:

- Implements `IChatClient` with deterministic heuristics (keywords, amount/currency regex, compliance thresholds)
- When tools are present and the category is not a policy match, returns `"Other"` so the **fallback stage** is exercised in tests
- Needs no API keys — suitable for local runs and CI

Unit tests can also construct `SimulationChatClient` directly without hosting the web app.

---

## 3. High-Performance Caching & Concurrency

### Q3.1 — Explain your cache key normalization and sanitization strategy (e.g. whitespace, case insensitivity, tokenization).

**Answer:**

Before caching:

1. `Trim()`
2. `ToLowerInvariant()` (case-insensitive)
3. Collapse runs of whitespace via generated regex `\s+` → single space

Key format: `expense_class_{normalizedDescription}`

Example: `"  Uber  Ride  "` and `"uber ride"` share one cache entry. TTL is **30 minutes** (`DefaultCacheTtl`). Cache hits clone the DTO and set `ClassificationStage = "CacheHit"`.

---

### Q3.2 — How did you prevent cache stampedes and ensure thread-safety under concurrent load?

**Answer:**

**Current design:** classic cache-aside (`TryGetValue` → miss → classify → `Set`).

- `IMemoryCache` is thread-safe for concurrent get/set.
- **No single-flight / per-key lock** today — concurrent identical misses can trigger duplicate LLM calls (acceptable for the assignment scope; see Q5 for production mitigation).

---

### Q3.3 — How did you implement bounded concurrency in `ClassifyBatch` (e.g. `Parallel.ForEachAsync`, `SemaphoreSlim`)?

**Answer:**

Batch processing uses:

```csharp
var parallelOptions = new ParallelOptions
{
    MaxDegreeOfParallelism = DefaultMaxBatchConcurrency, // 5
    CancellationToken = cancellationToken
};

await Parallel.ForEachAsync(indexedItems, parallelOptions, async (entry, ct) =>
{
    results[entry.index] = await Classify(entry.item.Description, ct);
});
```

- **`Parallel.ForEachAsync` + `MaxDegreeOfParallelism = 5`** bounds concurrent LLM work (equivalent intent to a `SemaphoreSlim(5)` gate).
- Order is preserved via indexed writes into a fixed array.
- After completion, aggregates compliance counts, per-currency totals, and processing time into `BatchSummary`.

---

## 4. Resilience, Error Handling & API Robustness

### Q4.1 — How did you handle rate-limiting (HTTP 429), upstream timeouts, transient failures, and malformed LLM responses?

**Answer:**

| Concern | Current behavior |
|---------|------------------|
| Malformed / markdown-wrapped JSON | Stripped by `CleanJsonResponse`; deserialize failure → catch → `null` |
| Empty LLM text | Treated as stage failure (`null`) |
| Upstream / LLM exceptions | Logged as warning; stage returns `null` → fallback agent → ultimately default `Unverifiable` response |
| Request cancellation | `CancellationToken` threaded from API → `Classify` / batch / `GetResponseAsync` |
| HTTP 429 / explicit retries / hard timeouts | Not implemented in this iteration (soft-fail + fallback instead); called out in Q5 |

Design choice: prefer **graceful degradation** (usable classification DTO) over failing the HTTP request for LLM flakiness, while still validating client input strictly.

---

### Q4.2 — How are RFC 7807 `ProblemDetails` formatted for client errors vs internal upstream failures?

**Answer:**

**Client errors (4xx)** — invalid payloads return `Results.BadRequest(new ProblemDetails { ... })`:

- `/classify` — empty/whitespace description
- `/classify/batch` — null/empty items list

Fields set: `Title`, `Detail`, `Status` (400).

**Internal / upstream failures** — not mapped to 5xx ProblemDetails in this version. Agent failures degrade into fallback/default `ResponseDto` with `200 OK`, so clients always receive a structured classification when the request itself is valid. A production hardening step would add exception middleware that returns ProblemDetails for unexpected 5xx (timeouts, unhandled faults) while keeping soft LLM degradation for classification quality issues.

---

## 5. Production & Enterprise Scale Roadmap

### Q5 — If you were rolling this service into production at scale (e.g. 100,000 expense claims/day), what architectural changes would you introduce? (e.g. Semantic Vector Caching with Redis/Qdrant, Asynchronous Message Queues / Azure Service Bus, Managed Identity authentication, OpenTelemetry tracing with Aspire/Prometheus).

**Answer:**

At ~**100,000 expense claims/day**, the following changes would be introduced:

**Caching & retrieval**

- **Semantic / vector cache** (Redis + embeddings, or Qdrant/Azure AI Search) so near-duplicate descriptions reuse classifications without exact-string keys
- **Single-flight** per cache key (e.g. `GetOrCreateAsync` + `SemaphoreSlim` / distributed lock) to prevent stampedes
- Hash or truncate normalized keys; move TTL/concurrency to configuration

**Throughput & async architecture**

- **Azure Service Bus / queue-based workers** for batch and peak load — API accepts claims, workers classify asynchronously, clients poll or receive webhooks
- Dedicated worker pool with configurable concurrency and backpressure into Azure OpenAI quota
- Optional outbox + idempotency keys for claim IDs

**Resilience & observability**

- **Polly / resilience pipelines**: retry with Retry-After on 429, exponential backoff, circuit breaker, per-call timeouts
- **OpenTelemetry** traces/metrics/logs (Aspire dashboard and/or Prometheus + Grafana): latency, cache hit ratio, stage mix, token usage, 429 rate
- Health checks for Azure OpenAI reachability and cache/Redis

**Security & identity**

- Replace API keys in config with **Managed Identity** + Azure RBAC to Azure OpenAI
- Secrets in Key Vault / user-secrets for local; never commit credentials
- API authentication (Entra ID / API keys), rate limiting at the edge (APIM / YARP)

**Model & agent quality**

- Enable proper **function invocation** on the live chat client pipeline
- Prefer **schema-constrained / typed structured output** APIs where available
- Prompt versioning, evaluation harness, and human-in-the-loop for low-confidence or `PolicyViolation` claims

**Data plane**

- Persist classifications for audit/compliance; correlate claim ID → stage → model → tools called
- Multi-tenant policy packs (per company) instead of a single static `CompanyPolicyTool`

These build on the current foundations: two-stage agent orchestration, tool binding, cache-aside, simulation for CI, bounded batch parallelism, and ProblemDetails validation — while addressing stampede control, hard resilience, identity, and horizontal scale.
