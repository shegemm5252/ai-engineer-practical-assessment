# Candidate Implementation Notes & Architecture Summary

Please use this document to explain your technical design decisions, trade-offs, and scaling considerations. This provides the evaluation committee with direct insight into your engineering thought process.

---

## 1. Multi-Stage AI Agent Architecture & Orchestration
* Describe how you structured and initialized `ExpenseClassifierAgent`.
* How did you register and bind `CompanyPolicyTool` and `SpendingLimitTool` to the primary agent?
* How did you ensure thread-safety, avoid per-request reflection overhead, and manage the transition/fallback to the secondary general agent?
* How did you achieve strictly typed, deterministic structured output (`ResponseDto`) from both stages?

*(Your notes here)*

---

## 2. Dependency Injection & Service Lifetime Strategy
* Summarize your DI configuration in `Program.cs`.
* What lifetimes did you choose for `AzureOpenAIClient`, `IChatClient`, `CompanyPolicyTool`, `SpendingLimitTool`, and `IExpenseClassifierAgent`, and why?
* How does your solution support offline local development / CI via `SimulationChatClient`?

*(Your notes here)*

---

## 3. High-Performance Caching & Concurrency
* Explain your cache key normalization and sanitization strategy (e.g. whitespace, case insensitivity, tokenization).
* How did you prevent cache stampedes and ensure thread-safety under concurrent load?
* How did you implement bounded concurrency in `ClassifyBatch` (e.g. `Parallel.ForEachAsync`, `SemaphoreSlim`)?

*(Your notes here)*

---

## 4. Resilience, Error Handling & API Robustness
* How did you handle rate-limiting (HTTP 429), upstream timeouts, transient failures, and malformed LLM responses?
* How are RFC 7807 `ProblemDetails` formatted for client errors vs internal upstream failures?

*(Your notes here)*

---

## 5. Production & Enterprise Scale Roadmap
* If you were rolling this service into production at scale (e.g. 100,000 expense claims/day), what architectural changes would you introduce? (e.g. Semantic Vector Caching with Redis/Qdrant, Asynchronous Message Queues / Azure Service Bus, Managed Identity authentication, OpenTelemetry tracing with Aspire/Prometheus).

*(Your notes here)*
