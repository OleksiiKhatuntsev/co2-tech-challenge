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

### Emissions API — Point-level Cache with Startup Warm-up

Emissions data is cached **per individual timestamp** (not per request range) using a combination of two patterns:

**1. Startup Cache Warming (`IHostedService`)**

On application startup, a background service fetches the last month of emissions data in a single HTTP call and populates the cache point by point:

```
startup → GET /emissions?from={month_ago}&to={now}
        → cache[timestamp] = kgPerWattHr  (per each 15-min point)
```

This absorbs the Emissions API chaos (50% × 15s delay) at startup rather than on user requests. Cost: ~2,880 points × 8 bytes ≈ 23 KB of memory.

**2. Cache-Aside as Fallback**

For any emissions point not already in the cache (queries older than one month, or if startup warm-up failed), the Cache-Aside pattern applies: check cache → miss → fetch → store per point → return.

**Why point-level granularity (not range-based)?**

Caching by `(from, to)` range key means a warm-up call covering the last month stores exactly one cache entry. A user querying any other range (e.g. last week) would be a cache miss. Point-level caching means any arbitrary `(from, to)` range is assembled from already-cached individual points, so warm-up genuinely benefits all queries.

**Result:** After startup, any query within the last month is served entirely from cache with zero HTTP calls to the Emissions API.

---

### Measurements API — No Cache

Measurements data is not cached.

**Why:**
- Measurements are per-user — the cache key would be `(userId, from, to)`, providing no sharing benefit across different users.
- The primary load pattern (many users, same timeframe) means every request has a unique `userId`, so cache hit rate would be near zero.
- The Measurements API chaos is a 30% error rate, not a delay. The correct strategy is **Retry with exponential backoff** (via Polly), not caching. A failed request returns no data to cache anyway.
