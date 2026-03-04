# Notes

## Library Decisions

### NSubstitute over Moq

Moq 4.20.0 shipped with SponsorLink — telemetry that collected user data without consent. Although removed in 4.20.2, the trust breach remains. NSubstitute has no such history, offers cleaner syntax (`Received()` vs `Verify()`), and is equally capable. Decision: **NSubstitute** for all mocking.

### FluentAssertions — mandatory

FluentAssertions is required (not optional) for all test projects. Rationale: readable assertions are critical for test maintainability — `result.TotalKg.Should().BeApproximately(expected, 0.001)` communicates intent far better than `Assert.Equal`. Note: verify license compatibility (v7+ changed to commercial for companies with >3 developers).

### Microsoft.Extensions.Http.Resilience — Api project only

Polly v8 resilience pipelines are configured in `Program.cs` (composition root). Infrastructure clients receive a plain `HttpClient` — they don't know about retry/circuit breaker policies. This follows **Dependency Inversion**: the outer layer (Api) configures cross-cutting concerns, inner layers (Infrastructure) are unaware. The package must NOT be referenced from Infrastructure.csproj.

---

## Caching Strategy

### Cache Lives in Infrastructure, Not Application

The `IMemoryCache` for emissions data is owned by `EmissionsClient` (Infrastructure layer), not by `CalculatorService` (Application layer).

**Why:** Cache-Aside is a **transport optimization** — it decides *how* data is fetched, not *what* we do with it. The Application layer calls `IEmissionsClient.GetFactorsAsync` and is unaware whether the response came from HTTP or cache. This follows **Dependency Inversion**: the port (`IEmissionsClient`) defines the contract, the adapter (`EmissionsClient`) decides the strategy.

**When caching WOULD belong in Application:** if the cache logic depended on business rules (e.g., invalidation on tariff changes, caching computed results rather than raw data). Our case is simpler — emission factors are historical and immutable, so caching is purely an infrastructure concern.

**Trade-off:** Infrastructure owns the cache TTL and eviction policy. If business rules ever need to control cache behavior (e.g., force-refresh on regulatory change), the cache would need to move up to Application or be exposed via a configuration abstraction.

### Emissions API — Cache-Aside (Lazy), No Warm-up

Emissions data is cached **per individual 15-min timestamp** using `IMemoryCache`. Key format: `emission:{timestamp}`, TTL: 24 hours.

**Pattern: Cache-Aside (lazy loading)**

On request: check if ALL needed 15-min timestamps are cached → all hit → return from cache, no HTTP call → any miss → fetch full range from API → cache each point individually → return.

**Why point-level granularity (not range-based)?**

Caching by `(from, to)` range key means a query for `[0, 3600)` and a query for `[900, 1800)` are two separate cache entries with no overlap. Point-level caching means any arbitrary range is assembled from already-cached individual points. Overlapping queries reuse cached points.

**Why all-or-nothing fetch on cache miss (not partial fetch)?**

On partial cache hit (some timestamps cached, some not), we re-fetch the full range from the API instead of fetching only the missing timestamps. Considered and rejected partial fetch for three reasons:

1. **API contract is range-based.** `GET /emissions?from={from}&to={to}` returns a contiguous range. We cannot request arbitrary individual timestamps. Fetching only missing points would require identifying contiguous gaps, issuing one HTTP call per gap, and merging results — significant complexity.
2. **Emissions data is global, not per-user.** After the first request for a time range, all blocks are cached. Subsequent requests from any user for the same range are full cache hits. Partial cache hits are a rare transitional state (only when two requests have partially overlapping but non-identical ranges).
3. **Re-fetch cost is negligible.** Emission factors are small DTOs (~50 bytes each). Re-fetching 4 blocks instead of 2 costs ~200 extra bytes. The code complexity of gap detection + multi-request orchestration + result merging is a permanent maintenance burden for a near-zero runtime saving.

**When partial fetch WOULD be justified:** upstream API supports individual-point or batch queries, data is per-user (low cache hit rate across users), or payload is large (megabytes per response). None of these apply here.

**Boundary check before linear scan (early exit optimization)**

`TryGetAllFromCache` checks the first and last timestamp in the range before iterating all intermediate blocks. If either boundary is missing from cache, we skip the linear scan entirely and go straight to API.

*Motivation:* cache is populated atomically per API response (all blocks in a contiguous range). If a request for `[0, 172800)` (2 days = 192 blocks) partially overlaps with a previously cached `[0, 86400)` (1 day), the naive approach reads 96 cached blocks before discovering block 97 is missing. The boundary check detects this in 2 lookups instead of 96. Cost on full hit: 2 extra lookups (first and last are read twice — once in boundary check, once in linear scan). This is negligible compared to the savings on partial overlap.

**Why NOT startup cache warming (`IHostedService`)?**

Considered and rejected. Reasons:

1. **Startup latency risk.** Emissions API has 50% chance of 15s delay per request. Warm-up call at startup could block or delay the container becoming healthy. In docker-compose with `depends_on: condition: service_healthy`, slow startup may cause orchestrator to consider the service dead.
2. **Unpredictable access pattern.** We don't know which time ranges users will query. Warming the last month means ~2,880 points loaded eagerly — most may never be accessed. This is wasted work + wasted memory (minor, ~23 KB, but the principle matters).
3. **Low cost of cold miss.** A cache miss costs one HTTP call to Emissions API. After that call, all points are cached and subsequent queries for overlapping ranges are free. The lazy approach converges to a warm cache naturally after a few requests.
4. **Chaos is handled by retry + timeout.** Polly pipeline (1s timeout × 5 fast retries + 1 guaranteed no-timeout attempt) handles the 50% delay chaos. 96.9% of requests resolve within 5 seconds. Cache-Aside means the chaos is absorbed once per unique time range, not once per user request — that's already a massive improvement.

**When eager warming WOULD be justified:** predictable access pattern (e.g., all users always query last 24h), stable upstream (no chaos), and startup latency is not a concern (batch processing, not an API). None of these apply here.

---

### Emissions API — Timeout Mechanics

The Emissions API chaos is a 50% chance of `Task.Delay(15s)`. Without a timeout, every unlucky request blocks for 15 seconds. The Polly timeout strategy **cancels** the request — it does not wait and check.

**How it works:** Polly passes a `CancellationToken` to `HttpClient`. After the timeout, the token is cancelled → `HttpClient` aborts the TCP connection, releases resources, throws `TimeoutRejectedException` → Polly catches it → triggers next retry attempt immediately.

**Strategy: 5 fast attempts (1s timeout) + 1 guaranteed attempt (no timeout)**

Emissions API chaos is a **delay**, not an error — the data always arrives eventually. A total failure (502) is unacceptable when the data is guaranteed to come. So after 5 fast timeout attempts, the 6th attempt waits as long as needed.

**Why 1s timeout?** The chaos is bimodal: response arrives in ~200ms (normal) or 15s (chaos). There is no middle ground. The timeout only needs to distinguish "normal" from "chaos hit". 1s = 5× the normal response time — safe margin for unexpected API slowdowns (GC pauses, network jitter, CLR cold start), while not wasting time when chaos clearly hit.

**Why 5 fast attempts?** More fast retries = higher chance of resolving in the fast path. With 5 attempts at 1s each, all 5 fit within a 5-second budget. P(not getting answer in 5s) = 0.5⁵ = 1/32 ≈ 3.1%. Compared to fewer retries with a longer timeout, this is dramatically better: same 5-second budget covers 96.9% of requests instead of 75% (2×2s) or 50% (1×5s).

```
Attempt 1: GET /emissions → chaos (15s delay) → 1s timeout → CANCELLED
Attempt 2: GET /emissions → chaos (15s delay) → 1s timeout → CANCELLED
Attempt 3: GET /emissions → no chaos → 200ms → success ✓
```

Worst case (all 5 fast attempts hit chaos):
```
Attempts 1-5: 1s timeout each → CANCELLED (5s total)
Attempt 6:    NO TIMEOUT → waits up to 15s → success ✓ (20s total)
```

**Implementation:** Custom retry pipeline — first 5 attempts use 1s `TimeoutStrategyOptions`, 6th attempt uses `Timeout.InfiniteTimeSpan` (or 30s as a safety net against genuine network hangs).

**Probability analysis:**
- P(success on attempt 1) = 50% — latency: <1s
- P(success within 2 attempts) = 75% — latency: ≤1s
- P(success within 5 attempts) = 96.88% — latency: ≤5s
- P(reaches attempt 6) = 3.12% (1/32) — latency: 5s + up to 15s = ~20s worst case
- **P(total failure) = 0%** — attempt 6 always succeeds (Emissions API never returns errors, only delays)
- E[latency] = 1.4s — mathematical expectation across all scenarios

---

### Measurements API — No Cache

Measurements data is not cached.

**Why:**
- Measurements are per-user — the cache key would be `(userId, from, to)`, providing no sharing benefit across different users.
- The primary load pattern (many users, same timeframe) means every request has a unique `userId`, so cache hit rate would be near zero.
- The Measurements API chaos is a 30% error rate, not a delay. The correct strategy is **Retry with exponential backoff** (via Polly), not caching. A failed request returns no data to cache anyway.

---

## Application Service Design — Orchestration vs Calculation

### Decision: keep orchestration and calculation together in `CalculatorService`

`CalculatorService.CalculateAsync` has three responsibilities: input validation, parallel data fetching, and CO₂ computation. This is a classic **Application Service** (DDD) — an orchestrator that glues infrastructure to domain logic.

**Alternative considered:** extract a pure **Domain Service** (`CarbonFootprintCalculator.Calculate(readings, factors)`) — a static function with no dependencies, testable without mocks.

**Why we keep it as-is:**

1. **YAGNI.** The calculation logic is ~15 lines (group → average → multiply). Extracting it creates a second file and navigation layer with no real benefit.
2. **Single algorithm.** There are no calculation variants (by energy source, by tariff). An abstraction for a single implementation is premature.
3. **Mock-based tests are acceptable.** At the current logic volume, mocking `IMeasurementsClient` / `IEmissionsClient` causes no friction.

**When to revisit:**

- A second calculation algorithm appears (different formulas for different sources)
- Data normalization/interpolation is added
- Unit tests become awkward due to mock complexity
- The method grows beyond ~50 lines

At that point, extract `CarbonFootprintCalculator` as a pure function in the Domain layer.

---

## API Layer — Composition Root + Resilience

### Program.cs — Composition Root Pattern

`Program.cs` is the **Composition Root** — the single place where all dependencies are wired together. No other layer knows about DI registration or configuration values.

**DI registrations:**
1. `AddMemoryCache()` — backing store for `EmissionsClient` Cache-Aside
2. `AddHttpClient<IMeasurementsClient, MeasurementsClient>` — typed HttpClient with base address from `Upstream:MeasurementsUrl`
3. `AddHttpClient<IEmissionsClient, EmissionsClient>` — typed HttpClient with base address from `Upstream:EmissionsUrl`
4. `AddScoped<ICalculatorService, CalculatorService>()` — scoped because it orchestrates scoped HTTP calls

**Why typed `HttpClient` via `IHttpClientFactory`?** `HttpClient` is not thread-safe for configuration changes, and creating new instances leaks sockets. `IHttpClientFactory` manages `HttpMessageHandler` pooling (default lifetime: 2 min) and DNS rotation. Typed clients (`AddHttpClient<TInterface, TImplementation>`) additionally give compile-time safety — DI resolves `IMeasurementsClient` with its correctly configured `HttpClient` automatically.

### Endpoint Design

```
GET /calculate/{userId}?from={unix}&to={unix} → { "totalKg": 123.45 }
GET /health                                    → { "status": "healthy" }
```

The `/calculate` endpoint is deliberately thin — it logs the incoming request and delegates to `ICalculatorService`. All error handling is centralized in `ExceptionHandlingMiddleware`. This follows **Separation of Concerns**: the endpoint is a translator (HTTP → domain call → HTTP), not a decision maker.

`/health` is a **liveness probe** — confirms the process is alive and can serve HTTP. No upstream checks (that would be a readiness probe, relevant in Kubernetes but not in our docker-compose setup).

`public partial class Program` at the end — enables `WebApplicationFactory<Program>` in E2E tests to bootstrap the real app in-memory.

### ExceptionHandlingMiddleware — Centralized Error Mapping

**Pattern: Exception Handling Middleware** — a single pipeline stage that catches domain exceptions and maps them to HTTP responses. Alternative considered: per-endpoint try/catch. Rejected because it duplicates error-mapping logic across every endpoint and violates DRY.

| Exception | HTTP Status | Log Level | Rationale |
|-----------|-------------|-----------|-----------|
| `InvalidCalculationRequestException` | 400 Bad Request | Warning | Client error — bad input, not our fault |
| `UpstreamUnavailableException` | 502 Bad Gateway | Error | Our dependency failed — we're a gateway |
| Any other `Exception` | 500 Internal Server Error | Error | Unexpected — generic message, no leak of internals |

**Security decision:** unhandled exceptions return generic `"Internal server error"`, not the actual exception message. Leaking stack traces or internal details is an information disclosure vulnerability (CWE-209).

Response format: `{ "error": "message" }` — consistent JSON for all error types.

### Resilience Pipelines (Polly v8)

Configured in `Program.cs` via `AddResilienceHandler` — part of `Microsoft.Extensions.Http.Resilience`. Infrastructure clients receive a plain `HttpClient` and are unaware of retry/timeout/circuit breaker policies.

**Measurements API pipeline:**
- **Retry:** 3 retries (4 total attempts), exponential backoff (1s → 2s → 4s), jitter enabled, triggers on transient HTTP errors (5xx, 408, network failures)
- **Circuit Breaker:** failure ratio 0.5, sampling duration 30s, minimum throughput 5, break duration 30s
- Why circuit breaker here: prevents cascading failures. If Measurements API is genuinely down (not just 30% chaos), circuit breaker stops us from sending requests that will certainly fail, giving the upstream time to recover.

**Emissions API pipeline:**
- **Outer timeout (30s):** safety net for the entire pipeline. If all attempts combined exceed 30s, abort.
- **Retry:** 5 retries (6 total attempts), constant delay 200ms, jitter enabled
- **Inner timeout (1s):** per-attempt timeout. Cancels individual requests that hit the 15s chaos delay.
- **No circuit breaker:** conscious decision. Cache-Aside is the primary defense — after one successful fetch, all subsequent requests for the same range bypass HTTP entirely.

**Why two timeout levels (onion model):**
```
Outer Timeout 30s ← total budget for all attempts
  └→ Retry (5 retries)
       └→ Inner Timeout 1s ← per-attempt budget
            └→ HTTP request
```
Inner timeout cuts individual chaos delays quickly. Outer timeout caps total wall-clock time. Without inner: one attempt could consume the entire 30s budget. Without outer: 6 × 15s = 90s worst case.
