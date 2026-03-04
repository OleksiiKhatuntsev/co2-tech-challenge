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

### Emissions API — Cache-Aside (Lazy), No Warm-up

Emissions data is cached **per individual 15-min timestamp** using `IMemoryCache`. Key format: `emission:{timestamp}`, TTL: 24 hours.

**Pattern: Cache-Aside (lazy loading)**

On request: check if ALL needed 15-min timestamps are cached → all hit → return from cache, no HTTP call → any miss → fetch full range from API → cache each point individually → return.

**Why point-level granularity (not range-based)?**

Caching by `(from, to)` range key means a query for `[0, 3600)` and a query for `[900, 1800)` are two separate cache entries with no overlap. Point-level caching means any arbitrary range is assembled from already-cached individual points. Overlapping queries reuse cached points.

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
