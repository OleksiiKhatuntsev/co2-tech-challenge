# Calculator API Implementation Plan

## Context

Build a Calculator API (.NET 8, Minimal API) that sits between a client and two upstream APIs (Measurements + Emissions), calculates total CO₂ in kg for a given user and time range. A skeleton project exists but is empty.

**Architecture**: Clean Architecture with separate .csproj projects — compile-time dependency enforcement.

---

## Project Structure (Clean Architecture)

```
calculator-api/src/
├── TechChallenge.Calculator.Domain/            ← domain models + exceptions (no dependencies)
│   ├── Models.cs
│   └── Exceptions/
│       ├── CalculatorDomainException.cs         ← base exception
│       ├── UpstreamUnavailableException.cs      ← upstream failed after retries
│       └── InvalidCalculationRequestException.cs ← validation errors
├── TechChallenge.Calculator.Application/       ← business logic + interfaces
│   ├── ICalculatorService.cs
│   ├── CalculatorService.cs
│   ├── IMeasurementsClient.cs                  ← port (interface)
│   └── IEmissionsClient.cs                     ← port (interface)
├── TechChallenge.Calculator.Infrastructure/    ← adapters (HTTP clients, cache)
│   ├── MeasurementsClient.cs
│   ├── EmissionsClient.cs
│   └── Dto/
│       ├── MeasurementResponseDto.cs
│       └── EmissionResponseDto.cs
└── TechChallenge.Calculator.Api/               ← composition root (already exists)
    ├── Program.cs
    └── Middleware/
        └── ExceptionHandlingMiddleware.cs       ← global exception → HTTP status mapping
```

**Dependency graph** (enforced by ProjectReference):
```
Api → Application, Infrastructure
Infrastructure → Application, Domain
Application → Domain
Domain → (nothing)
```

Interfaces in Application, implementations in Infrastructure — Dependency Inversion Principle enforced at compile time. Infrastructure references Application — this is correct: outer layer depends on inner layer.

---

## ✅ Step 1. Create projects and wire references — DONE

### 1.1 Create class library projects ✅
- `calculator-api/src/TechChallenge.Calculator.Domain/TechChallenge.Calculator.Domain.csproj` — created
- `calculator-api/src/TechChallenge.Calculator.Application/TechChallenge.Calculator.Application.csproj` — created, refs: Domain
- `calculator-api/src/TechChallenge.Calculator.Infrastructure/TechChallenge.Calculator.Infrastructure.csproj` — created, refs: Application, Domain; package: `Microsoft.Extensions.Caching.Memory`

### 1.2 Update existing Calculator.Api.csproj ✅
- Added ProjectReference: Application, Infrastructure
- Added PackageReference: `Microsoft.Extensions.Http.Resilience`

### 1.3 Add packages to `Directory.Packages.props` ✅
Added all packages (both infra and test):
- `Microsoft.Extensions.Http.Resilience` v8.10.0
- `Microsoft.Extensions.Caching.Memory` v8.0.1
- `NSubstitute` v5.3.0
- `FluentAssertions` v7.0.0
- `Microsoft.NET.Test.Sdk` v17.11.1
- `xunit` v2.9.2
- `xunit.runner.visualstudio` v2.8.2
- `Microsoft.AspNetCore.Mvc.Testing` v8.0.11
- `WireMock.Net` v1.6.7

### 1.4 Add all projects to `TechChallenge.sln` ✅
Added 5 projects: Domain, Application, Infrastructure, UnitTests, E2E

### 1.5 Configure `appsettings.json` ✅
```json
"Upstream": {
  "MeasurementsUrl": "http://localhost:5153",
  "EmissionsUrl": "http://localhost:5139"
}
```

### 1.6 Create test project scaffolding ✅
- `calculator-api/tests/TechChallenge.Calculator.UnitTests/` — refs: Domain, Application, Infrastructure
- `calculator-api/tests/TechChallenge.Calculator.E2E/` — refs: Api; packages: WireMock.Net, Mvc.Testing

**Result:** `dotnet build TechChallenge.sln` → Build succeeded (0 errors)

---

## ✅ Step 2. Domain — models + exceptions — DONE

### 2.1 Models ✅

File: `TechChallenge.Calculator.Domain/Models.cs`

```csharp
public record EnergyReading(long Timestamp, double Watts);
public record EmissionFactor(long Timestamp, double KgPerWattHr);
public record CarbonFootprint(double TotalKg);
```

These are our business concepts, not external API shapes. External DTOs live in Infrastructure.

### 2.2 Domain Exceptions ✅

Files in `TechChallenge.Calculator.Domain/Exceptions/`:
- `CalculatorDomainException.cs` — base, primary constructor `(string message, Exception? inner = null)`
- `UpstreamUnavailableException.cs` — `(string serviceName, Exception inner)`, message: `"Upstream service '{serviceName}' is unavailable"`
- `InvalidCalculationRequestException.cs` — `(string reason)`, message: `"Invalid calculation request: {reason}"`

Why domain exceptions: they belong to our business language, not to HTTP or infrastructure. The API layer maps them to HTTP status codes — domain doesn't know about HTTP.

**Result:** `dotnet build TechChallenge.sln` → Build succeeded (0 errors)

---

## Step 2b. Logging Strategy

Log levels by layer:

| Level | Where | What |
|-------|-------|------|
| `Debug` | EmissionsClient | Cache hit/miss details, cached block count |
| `Debug` | CalculatorService | Per-period calculation details (avg watts, kWh, co2) |
| `Information` | Program.cs endpoint | Incoming request: userId, from, to |
| `Information` | CalculatorService | Calculation complete: userId, total CO₂, period count, elapsed ms |
| `Warning` | MeasurementsClient | Retry attempt (attempt N, status code, delay) |
| `Warning` | EmissionsClient | Timeout triggered, retrying |
| `Warning` | CalculatorService | Missing emission factor for period, skipping |
| `Error` | ExceptionHandlingMiddleware | Unhandled exception (full stack trace) |
| `Error` | MeasurementsClient | All retries exhausted → UpstreamUnavailableException |
| `Error` | EmissionsClient | All retries exhausted → UpstreamUnavailableException |

Implementation: use `ILogger<T>` (built-in .NET), no extra packages needed. Polly v8 has built-in logging for retry/timeout events via `ResilienceHandlerOptions`.

Use high-performance logging with `LoggerMessage.Define` or `[LoggerMessage]` source generator for hot paths (per-period calculation).

---

## ✅ Step 3. Application — interfaces + business logic — DONE

### 3.1 Port interfaces (in Application) ✅
- `IMeasurementsClient.cs` — `Task<EnergyReading[]> GetReadingsAsync(string userId, long from, long to, CancellationToken ct)`
- `IEmissionsClient.cs` — `Task<EmissionFactor[]> GetFactorsAsync(long from, long to, CancellationToken ct)`
- `ICalculatorService.cs` — `Task<CarbonFootprint> CalculateAsync(string userId, long from, long to, CancellationToken ct)`

Returns domain models, not DTOs — Infrastructure maps DTOs → domain.

### 3.2 CalculatorService ✅
- Primary constructor: `(IMeasurementsClient, IEmissionsClient, ILogger<CalculatorService>)`
- Validation: throws `InvalidCalculationRequestException` if `from >= to` or `from < 0`
- Parallel fetch via `Task.WhenAll`, group by 900s periods, avg watts → kWh → CO₂
- Logging: Information (request/result), Warning (missing factor), Debug (per-period)

### 3.3 Additional dependency ✅
- Added `Microsoft.Extensions.Logging.Abstractions` v8.0.2 to `Directory.Packages.props` + Application.csproj (classlib needs explicit reference for `ILogger<T>`)

### 3.4 Unit tests ✅
- `CalculatorServiceTests.cs` — 8 tests, all passing
- Happy path, empty measurements, missing factor, invalid from/to, negative from, parallel calls, multi-period grouping

**Result:** `dotnet test` → Passed: 8, Failed: 0

---

## Step 4. Infrastructure — HTTP clients

### 4.1 DTOs (in Infrastructure/Dto/)
```csharp
public record MeasurementResponseDto(long Timestamp, double Watts);
public record EmissionResponseDto(long Timestamp, double KgPerWattHr);
```

### 4.2 MeasurementsClient
- Implements `IMeasurementsClient`
- Typed HttpClient via `IHttpClientFactory`
- Dependencies: `HttpClient`, `ILogger<MeasurementsClient>`
- Deserializes `MeasurementResponseDto[]` → maps to `EnergyReading[]`
- Catches `HttpRequestException` / `TimeoutRejectedException` after Polly exhaustion → throws `UpstreamUnavailableException("Measurements", ex)`
- Resilience: configured externally in Program.cs (Polly v8 pipeline)

Polly v8 Resilience Pipeline (configured in Program.cs):
- Retry: 3 retries (4 total attempts), exponential backoff (1s → 2s → 4s), on HTTP 5xx
  - Why 3 retries: P(all fail) = 0.3⁴ = 0.81%, P(success) = 99.19%. Worst case latency = 7s (1+2+4).
    Going to 4 retries gains only 0.57% success but worst case jumps to 23s (1+2+4+8+8 with jitter cap) — not worth it.
- Circuit Breaker: break after 5 consecutive failures, 30s recovery

### 4.3 EmissionsClient
- Implements `IEmissionsClient`
- Typed HttpClient
- Dependencies: `HttpClient`, `IMemoryCache`, `ILogger<EmissionsClient>`
- Deserializes `EmissionResponseDto[]` → maps to `EmissionFactor[]`
- Catches `HttpRequestException` / `TimeoutRejectedException` after Polly exhaustion → throws `UpstreamUnavailableException("Emissions", ex)`
- **Cache-Aside with 15-minute block granularity** via `IMemoryCache`:
  - On response: cache each `EmissionFactor` individually, key = `emission:{timestamp}`
  - On request: check if ALL needed 15-min timestamps are cached
    - All cached → log Debug "cache hit", return from cache, no HTTP call
    - Any missing → log Debug "cache miss", fetch full range from API, cache each block, return
  - TTL: 24 hours (historical data never changes)

Polly v8 Resilience Pipeline (configured in Program.cs):
- **5 fast attempts**: 1s timeout each — cancel and retry if chaos delay hits. Why 1s: normal response is ~200ms, chaos is 15s, nothing in between. 1s = 5× normal response time — safe margin for unexpected API slowdowns (GC pauses, network jitter) while not wasting time when chaos clearly hit.
- **6th attempt: no timeout** (or 30s safety net) — wait for guaranteed response
- Emissions chaos is a delay, not an error — data always arrives. Total failure (502) is unacceptable → 6th attempt guarantees success.
- Fast path: 96.88% of requests complete within ≤5s (P(fail in 5s) = 1/32). Worst case: 5×1s + 15s = 20s (P = 3.12%). E[latency] = 1.4s.
- **No Circuit Breaker** — conscious decision: cache is the primary defense. After the first successful fetch, all subsequent requests for the same time range are served from cache, bypassing HTTP entirely.

---

## Step 5. Program.cs — Composition Root + Endpoint

```
GET /calculate/{userId}?from={unix}&to={unix}
→ { "totalKg": 123.45 }
```

DI registration:
1. `builder.Services.AddMemoryCache()`
2. `builder.Services.AddHttpClient<IMeasurementsClient, MeasurementsClient>(...)` + `.AddResilienceHandler(...)` (Polly v8)
3. `builder.Services.AddHttpClient<IEmissionsClient, EmissionsClient>(...)` + `.AddResilienceHandler(...)`
4. `builder.Services.AddScoped<ICalculatorService, CalculatorService>()`
5. `app.UseMiddleware<ExceptionHandlingMiddleware>()`
6. `app.MapGet("/calculate/{userId}", handler)`
7. `app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))` — liveness probe

### 5.2 Health Check Endpoint

`GET /health` → `{ "status": "healthy" }` (200 OK)

This is a **liveness probe** — confirms the Calculator process is alive and can serve HTTP. No upstream checks — that's a readiness probe concern (relevant in Kubernetes, not in our docker-compose setup).

Used by docker-compose `healthcheck` to gate `depends_on: condition: service_healthy` if needed. Lightweight, no dependencies, always returns 200.

### 5.3 Exception Handling Middleware

File: `TechChallenge.Calculator.Api/Middleware/ExceptionHandlingMiddleware.cs`

Global middleware that catches exceptions and maps to HTTP responses:

| Exception | HTTP Status | Log Level |
|-----------|-------------|-----------|
| `InvalidCalculationRequestException` | 400 Bad Request | Warning |
| `UpstreamUnavailableException` | 502 Bad Gateway | Error |
| Any other `Exception` | 500 Internal Server Error | Error |

Response body format:
```json
{ "error": "error message here" }
```

Registered in Program.cs via `app.UseMiddleware<ExceptionHandlingMiddleware>()` before endpoint mapping.

This keeps the endpoint handler clean — it just calls `ICalculatorService` and returns the result. All error mapping is centralized.

---

## Step 6. Unit Tests

Project: `calculator-api/tests/TechChallenge.Calculator.UnitTests/` (xUnit + NSubstitute)

Unit tests for **every public method** across all layers. Dependencies mocked via **NSubstitute** (chosen over Moq due to SponsorLink incident in NSubstitute 4.20.0 — telemetry without consent. NSubstitute has cleaner syntax and no trust issues).

### 6.1 CalculatorService tests ✅ (8 tests passing)
- `CalculateAsync_HappyPath_ReturnsCorrectCo2` ✅
- `CalculateAsync_EmptyMeasurements_ReturnsZero` ✅
- `CalculateAsync_MissingEmissionFactor_SkipsPeriod` ✅
- `CalculateAsync_InvalidFromTo_ThrowsInvalidCalculationRequestException` ✅ (Theory: from==to, from>to)
- `CalculateAsync_NegativeFrom_ThrowsInvalidCalculationRequestException` ✅
- `CalculateAsync_CallsUpstreamsInParallel` ✅
- `CalculateAsync_MultiplePeriodsGroupedCorrectly` ✅

### 6.2 MeasurementsClient tests
- `GetReadingsAsync_Success_ReturnsMappedDomainModels` — mock HttpMessageHandler returns DTOs → verify EnergyReading[]
- `GetReadingsAsync_HttpFailure_ThrowsUpstreamUnavailableException` — mock returns 500 → verify exception type + message
- `GetReadingsAsync_EmptyResponse_ReturnsEmptyArray`

### 6.3 EmissionsClient tests
- `GetFactorsAsync_CacheMiss_FetchesFromApi` — empty cache → verify HTTP call made + factors cached
- `GetFactorsAsync_CacheHit_ReturnsFromCache` — pre-populated cache → verify NO HTTP call
- `GetFactorsAsync_PartialCacheHit_FetchesFromApi` — some blocks cached → verify HTTP call + all blocks returned
- `GetFactorsAsync_HttpFailure_ThrowsUpstreamUnavailableException`
- `GetFactorsAsync_CachesIndividualBlocks` — verify each 15-min factor cached separately

### 6.4 ExceptionHandlingMiddleware tests
- `Invoke_InvalidCalculationRequestException_Returns400`
- `Invoke_UpstreamUnavailableException_Returns502`
- `Invoke_UnhandledException_Returns500`
- `Invoke_NoException_PassesThrough`

---

## Step 7. E2E Tests

Project: `calculator-api/tests/TechChallenge.Calculator.E2E/` (xUnit)

### Approach: WebApplicationFactory + WireMock
- `WebApplicationFactory<Program>` to host Calculator API in-memory
- **WireMock.Net** to mock Measurements and Emissions APIs
- Override `Upstream:MeasurementsUrl` and `Upstream:EmissionsUrl` to point to WireMock instances

### Test cases:
1. **Happy path** — both APIs respond normally → returns correct CO₂ value
2. **Measurements chaos** — mock returns 500 on first 2 calls, 200 on 3rd → retry succeeds, correct result
3. **Emissions cache hit** — two sequential requests with same time range → second doesn't hit WireMock emissions
4. **Emissions timeout** — mock delays beyond timeout → retry → eventually succeeds
   - Use reduced timeouts in test config (timeout: 1s, delay: 3s) to keep test fast (~3-4s instead of 15-20s). Same behavior verified, just faster.
5. **Invalid parameters** — missing `from`/`to` → 400
6. **Empty data** — no measurements in range → returns 0
7. **Upstream down** — all retries fail → 502 Bad Gateway
8. **Exception handling** — verify error response format `{ "error": "..." }`

---

## Step 8. Verification

1. `dotnet build TechChallenge.sln`
2. `dotnet test` — all unit + E2E tests pass
3. Manual: start 3 services, `curl "http://localhost:5000/calculate/alpha?from=1609459200&to=1609462800"`
4. `docker compose up --build` — verify containerized setup

---

## Files to modify/create

| File | Action |
|------|--------|
| `Directory.Packages.props` | ✅ Done — 9 packages added |
| `TechChallenge.sln` | ✅ Done — 5 projects added |
| **Domain** | |
| `calculator-api/src/TechChallenge.Calculator.Domain/*.csproj` | ✅ Done |
| `calculator-api/src/TechChallenge.Calculator.Domain/Models.cs` | ✅ Done |
| `calculator-api/src/TechChallenge.Calculator.Domain/Exceptions/CalculatorDomainException.cs` | ✅ Done |
| `calculator-api/src/TechChallenge.Calculator.Domain/Exceptions/UpstreamUnavailableException.cs` | ✅ Done |
| `calculator-api/src/TechChallenge.Calculator.Domain/Exceptions/InvalidCalculationRequestException.cs` | ✅ Done |
| **Application** | |
| `calculator-api/src/TechChallenge.Calculator.Application/*.csproj` | ✅ Done |
| `calculator-api/src/TechChallenge.Calculator.Application/IMeasurementsClient.cs` | ✅ Done |
| `calculator-api/src/TechChallenge.Calculator.Application/IEmissionsClient.cs` | ✅ Done |
| `calculator-api/src/TechChallenge.Calculator.Application/ICalculatorService.cs` | ✅ Done |
| `calculator-api/src/TechChallenge.Calculator.Application/CalculatorService.cs` | ✅ Done |
| **Infrastructure** | |
| `calculator-api/src/TechChallenge.Calculator.Infrastructure/*.csproj` | ✅ Done |
| `calculator-api/src/TechChallenge.Calculator.Infrastructure/Dto/MeasurementResponseDto.cs` | **Create** |
| `calculator-api/src/TechChallenge.Calculator.Infrastructure/Dto/EmissionResponseDto.cs` | **Create** |
| `calculator-api/src/TechChallenge.Calculator.Infrastructure/MeasurementsClient.cs` | **Create** |
| `calculator-api/src/TechChallenge.Calculator.Infrastructure/EmissionsClient.cs` | **Create** |
| **Api** | |
| `calculator-api/src/TechChallenge.Calculator.Api/*.csproj` | ✅ Done — project refs + Resilience package added |
| `calculator-api/src/TechChallenge.Calculator.Api/appsettings.json` | ✅ Done — Upstream section added |
| `calculator-api/src/TechChallenge.Calculator.Api/Program.cs` | Update — DI, resilience, endpoint, middleware |
| `calculator-api/src/TechChallenge.Calculator.Api/Middleware/ExceptionHandlingMiddleware.cs` | **Create** |
| **Unit Tests** | |
| `calculator-api/tests/TechChallenge.Calculator.UnitTests/*.csproj` | ✅ Done |
| `calculator-api/tests/TechChallenge.Calculator.UnitTests/CalculatorServiceTests.cs` | ✅ Done (8 tests) |
| `calculator-api/tests/TechChallenge.Calculator.UnitTests/MeasurementsClientTests.cs` | **Create** |
| `calculator-api/tests/TechChallenge.Calculator.UnitTests/EmissionsClientTests.cs` | **Create** |
| `calculator-api/tests/TechChallenge.Calculator.UnitTests/ExceptionHandlingMiddlewareTests.cs` | **Create** |
| **E2E Tests** | |
| `calculator-api/tests/TechChallenge.Calculator.E2E/*.csproj` | ✅ Done |
| `calculator-api/tests/TechChallenge.Calculator.E2E/CalculatorE2ETests.cs` | **Create** |
| **Docs** | |
| `Notes.md` (repo root) | **Create** — architectural decisions log |
