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

## Step 1. Create projects and wire references

### 1.1 Create class library projects
- `calculator-api/src/TechChallenge.Calculator.Domain/TechChallenge.Calculator.Domain.csproj` — SDK: `Microsoft.NET.Sdk`, net8.0
- `calculator-api/src/TechChallenge.Calculator.Application/TechChallenge.Calculator.Application.csproj` — SDK: `Microsoft.NET.Sdk`, net8.0, refs: Domain
- `calculator-api/src/TechChallenge.Calculator.Infrastructure/TechChallenge.Calculator.Infrastructure.csproj` — SDK: `Microsoft.NET.Sdk`, net8.0, refs: Application, Domain; packages: `Microsoft.Extensions.Caching.Memory`

### 1.2 Update existing Calculator.Api.csproj
- Add ProjectReference: Application, Infrastructure
- Add PackageReference: `Microsoft.Extensions.Http.Resilience` (for Polly v8 pipeline config in Program.cs)

### 1.3 Add packages to `Directory.Packages.props`
- `Microsoft.Extensions.Http.Resilience` (Polly v8, replaces legacy `Microsoft.Extensions.Http.Polly`)
- `Microsoft.Extensions.Caching.Memory`

### 1.4 Add all 3 new projects to `TechChallenge.sln`
- Under `calculator-api/src/` solution folder

### 1.5 Configure `appsettings.json`
```json
"Upstream": {
  "MeasurementsUrl": "http://localhost:5153",
  "EmissionsUrl": "http://localhost:5139"
}
```

---

## Step 2. Domain — models + exceptions

### 2.1 Models

File: `TechChallenge.Calculator.Domain/Models.cs`

```csharp
public record EnergyReading(long Timestamp, double Watts);
public record EmissionFactor(long Timestamp, double KgPerWattHr);
public record CarbonFootprint(double TotalKg);
```

These are our business concepts, not external API shapes. External DTOs live in Infrastructure.

### 2.2 Domain Exceptions

File: `TechChallenge.Calculator.Domain/Exceptions/`

```csharp
// Base — all domain exceptions inherit from this
public class CalculatorDomainException(string message, Exception? inner = null)
    : Exception(message, inner);

// Thrown by Infrastructure clients when upstream is unreachable after all retries
public class UpstreamUnavailableException(string serviceName, Exception inner)
    : CalculatorDomainException($"Upstream service '{serviceName}' is unavailable", inner);

// Thrown by Application when request validation fails
public class InvalidCalculationRequestException(string reason)
    : CalculatorDomainException($"Invalid calculation request: {reason}");
```

Why domain exceptions: they belong to our business language, not to HTTP or infrastructure. The API layer maps them to HTTP status codes — domain doesn't know about HTTP.

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

## Step 3. Application — interfaces + business logic

### 3.1 Port interfaces (in Application)
- `IMeasurementsClient`: `Task<EnergyReading[]> GetReadingsAsync(string userId, long from, long to, CancellationToken ct)`
- `IEmissionsClient`: `Task<EmissionFactor[]> GetFactorsAsync(long from, long to, CancellationToken ct)`

Returns domain models, not DTOs — Infrastructure maps DTOs → domain.

### 3.2 CalculatorService
- Interface: `ICalculatorService.CalculateAsync(string userId, long from, long to, CancellationToken ct) → CarbonFootprint`
- Dependencies: `IMeasurementsClient`, `IEmissionsClient`, `ILogger<CalculatorService>`
- Throws `InvalidCalculationRequestException` if `from >= to` or `from < 0`

Algorithm:
1. Log Information: incoming calculation request
2. Parallel fetch via `Task.WhenAll(measurements, emissions)`
3. Emissions → `Dictionary<long, double>` (timestamp → factor)
4. Measurements → group by 15-min period: `timestamp / 900 * 900`
5. For each group:
   - `avg_watts = measurements.Average(m => m.Watts)` — skip empty groups
   - `kWh = avg_watts / 4.0 / 1000.0`
   - `co2 = kWh * emissionFactors[periodStart]` — use `TryGetValue`, log Warning + skip if factor missing
   - Log Debug: per-period details
6. Sum → `CarbonFootprint(total)`
7. Log Information: calculation complete with elapsed time

Edge cases:
- Empty measurements for a period → skip (contributes 0)
- Missing emission factor for a period → log Warning, skip
- `from` not aligned to 900s → first period is partial, still works (just fewer readings)

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
- Retry: 3 attempts, exponential backoff (1s → 2s → 4s), on HTTP 5xx
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
- Timeout: 5s per attempt
- Retry: 3 attempts after timeout

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

### 5.2 Exception Handling Middleware

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

### 6.1 CalculatorService tests
- `CalculateAsync_HappyPath_ReturnsCorrectCo2` — known measurements + factors → verify exact result
- `CalculateAsync_EmptyMeasurements_ReturnsZero` — no readings → CarbonFootprint(0)
- `CalculateAsync_MissingEmissionFactor_SkipsPeriod` — factor missing for one period → calculates rest
- `CalculateAsync_InvalidFromTo_ThrowsInvalidCalculationRequestException` — from >= to
- `CalculateAsync_CallsUpstreamsInParallel` — verify both clients called, not sequential
- `CalculateAsync_MultiplePeriodsGroupedCorrectly` — readings split across 15-min boundaries

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

### Add to `Directory.Packages.props`:
- `NSubstitute`
- `Microsoft.NET.Test.Sdk`
- `FluentAssertions` (optional, for readable assertions)

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
4. **Emissions timeout** — mock delays 15s → timeout triggers retry → eventually succeeds
5. **Invalid parameters** — missing `from`/`to` → 400
6. **Empty data** — no measurements in range → returns 0
7. **Upstream down** — all retries fail → 502 Bad Gateway
8. **Exception handling** — verify error response format `{ "error": "..." }`

### Add to `Directory.Packages.props`:
- `Microsoft.AspNetCore.Mvc.Testing`
- `WireMock.Net`
- `xunit` + `xunit.runner.visualstudio`

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
| `Directory.Packages.props` | Add Resilience, Caching, NSubstitute, WireMock, xUnit packages |
| `TechChallenge.sln` | Add 3 new projects + 2 test projects |
| **Domain** | |
| `calculator-api/src/TechChallenge.Calculator.Domain/*.csproj` | **Create** |
| `calculator-api/src/TechChallenge.Calculator.Domain/Models.cs` | **Create** |
| `calculator-api/src/TechChallenge.Calculator.Domain/Exceptions/CalculatorDomainException.cs` | **Create** |
| `calculator-api/src/TechChallenge.Calculator.Domain/Exceptions/UpstreamUnavailableException.cs` | **Create** |
| `calculator-api/src/TechChallenge.Calculator.Domain/Exceptions/InvalidCalculationRequestException.cs` | **Create** |
| **Application** | |
| `calculator-api/src/TechChallenge.Calculator.Application/*.csproj` | **Create** |
| `calculator-api/src/TechChallenge.Calculator.Application/IMeasurementsClient.cs` | **Create** |
| `calculator-api/src/TechChallenge.Calculator.Application/IEmissionsClient.cs` | **Create** |
| `calculator-api/src/TechChallenge.Calculator.Application/ICalculatorService.cs` | **Create** |
| `calculator-api/src/TechChallenge.Calculator.Application/CalculatorService.cs` | **Create** |
| **Infrastructure** | |
| `calculator-api/src/TechChallenge.Calculator.Infrastructure/*.csproj` | **Create** |
| `calculator-api/src/TechChallenge.Calculator.Infrastructure/Dto/MeasurementResponseDto.cs` | **Create** |
| `calculator-api/src/TechChallenge.Calculator.Infrastructure/Dto/EmissionResponseDto.cs` | **Create** |
| `calculator-api/src/TechChallenge.Calculator.Infrastructure/MeasurementsClient.cs` | **Create** |
| `calculator-api/src/TechChallenge.Calculator.Infrastructure/EmissionsClient.cs` | **Create** |
| **Api** | |
| `calculator-api/src/TechChallenge.Calculator.Api/*.csproj` | Update — add project refs |
| `calculator-api/src/TechChallenge.Calculator.Api/appsettings.json` | Update — add Upstream |
| `calculator-api/src/TechChallenge.Calculator.Api/Program.cs` | Update — DI, resilience, endpoint, middleware |
| `calculator-api/src/TechChallenge.Calculator.Api/Middleware/ExceptionHandlingMiddleware.cs` | **Create** |
| **Unit Tests** | |
| `calculator-api/tests/TechChallenge.Calculator.UnitTests/*.csproj` | **Create** — refs: Application, Infrastructure, Domain |
| `calculator-api/tests/TechChallenge.Calculator.UnitTests/CalculatorServiceTests.cs` | **Create** |
| `calculator-api/tests/TechChallenge.Calculator.UnitTests/MeasurementsClientTests.cs` | **Create** |
| `calculator-api/tests/TechChallenge.Calculator.UnitTests/EmissionsClientTests.cs` | **Create** |
| `calculator-api/tests/TechChallenge.Calculator.UnitTests/ExceptionHandlingMiddlewareTests.cs` | **Create** |
| **E2E Tests** | |
| `calculator-api/tests/TechChallenge.Calculator.E2E/*.csproj` | **Create** |
| `calculator-api/tests/TechChallenge.Calculator.E2E/CalculatorE2ETests.cs` | **Create** |
| **Docs** | |
| `Notes.md` (repo root) | **Create** — architectural decisions log (NSubstitute choice rationale, etc.) |
