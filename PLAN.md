# Calculator API Implementation Plan

## Context

Build a Calculator API (.NET 8, Minimal API) that sits between a client and two upstream APIs (Measurements + Emissions), calculates total CO₂ in kg for a given user and time range. A skeleton project exists but is empty.

**Architecture**: Clean Architecture with separate .csproj projects — compile-time dependency enforcement.

---

## Project Structure (Clean Architecture)

```
calculator-api/src/
├── TechChallenge.Calculator.Domain/            ← domain models (no dependencies)
│   └── Models.cs
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
    └── Program.cs
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
- `calculator-api/src/TechChallenge.Calculator.Infrastructure/TechChallenge.Calculator.Infrastructure.csproj` — SDK: `Microsoft.NET.Sdk`, net8.0, refs: Application, Domain; packages: `Microsoft.Extensions.Http.Resilience`, `Microsoft.Extensions.Caching.Memory`

### 1.2 Update existing Calculator.Api.csproj
- Add ProjectReference: Application, Infrastructure

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

## Step 2. Domain — models

File: `TechChallenge.Calculator.Domain/Models.cs`

```csharp
public record EnergyReading(long Timestamp, double Watts);
public record EmissionFactor(long Timestamp, double KgPerWattHr);
public record CarbonFootprint(double TotalKg);
```

These are our business concepts, not external API shapes. External DTOs live in Infrastructure.

---

## Step 3. Application — interfaces + business logic

### 3.1 Port interfaces (in Application)
- `IMeasurementsClient`: `Task<EnergyReading[]> GetReadingsAsync(string userId, long from, long to, CancellationToken ct)`
- `IEmissionsClient`: `Task<EmissionFactor[]> GetFactorsAsync(long from, long to, CancellationToken ct)`

Returns domain models, not DTOs — Infrastructure maps DTOs → domain.

### 3.2 CalculatorService
- Interface: `ICalculatorService.CalculateAsync(string userId, long from, long to, CancellationToken ct) → CarbonFootprint`
- Dependencies: `IMeasurementsClient`, `IEmissionsClient`

Algorithm:
1. Parallel fetch via `Task.WhenAll(measurements, emissions)`
2. Emissions → `Dictionary<long, double>` (timestamp → factor)
3. Measurements → group by 15-min period: `timestamp / 900 * 900`
4. For each group:
   - `avg_watts = measurements.Average(m => m.Watts)` — skip empty groups
   - `kWh = avg_watts / 4.0 / 1000.0`
   - `co2 = kWh * emissionFactors[periodStart]` — use `TryGetValue`, skip if factor missing
5. Sum → `CarbonFootprint(total)`

Edge cases:
- Empty measurements for a period → skip (contributes 0)
- Missing emission factor for a period → skip (no factor = can't calculate)
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
- Deserializes `MeasurementResponseDto[]` → maps to `EnergyReading[]`
- Resilience: configured externally in Program.cs (Polly v8 pipeline)

Polly v8 Resilience Pipeline (configured in Program.cs):
- Retry: 3 attempts, exponential backoff (1s → 2s → 4s), on HTTP 5xx
- Circuit Breaker: break after 5 consecutive failures, 30s recovery

### 4.3 EmissionsClient
- Implements `IEmissionsClient`
- Typed HttpClient
- Deserializes `EmissionResponseDto[]` → maps to `EmissionFactor[]`
- **Cache-Aside with 15-minute block granularity** via `IMemoryCache`:
  - On response: cache each `EmissionFactor` individually, key = `emission:{timestamp}`
  - On request: check if ALL needed 15-min timestamps are cached
    - All cached → return from cache, no HTTP call (chaos completely bypassed)
    - Any missing → fetch full range from API, cache each block, return
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
5. `app.MapGet("/calculate/{userId}", handler)`

Error handling:
- 400: invalid/missing `from`/`to`
- 502: upstream unavailable after all retries (catch `HttpRequestException` / `TimeoutRejectedException`)

---

## Step 6. E2E Tests

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

### Add to `Directory.Packages.props`:
- `Microsoft.AspNetCore.Mvc.Testing`
- `WireMock.Net`
- `xunit` + `xunit.runner.visualstudio`

---

## Step 7. Verification

1. `dotnet build TechChallenge.sln`
2. `dotnet test` — all E2E tests pass
3. Manual: start 3 services, `curl "http://localhost:5000/calculate/alpha?from=1609459200&to=1609462800"`
4. `docker compose up --build` — verify containerized setup

---

## Files to modify/create

| File | Action |
|------|--------|
| `Directory.Packages.props` | Add Resilience, Caching, WireMock, xUnit packages |
| `TechChallenge.sln` | Add 3 new projects + test project |
| **Domain** | |
| `calculator-api/src/TechChallenge.Calculator.Domain/*.csproj` | **Create** |
| `calculator-api/src/TechChallenge.Calculator.Domain/Models.cs` | **Create** |
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
| `calculator-api/src/TechChallenge.Calculator.Api/Program.cs` | Update — DI, resilience, endpoint |
| **Tests** | |
| `calculator-api/tests/TechChallenge.Calculator.E2E/*.csproj` | **Create** |
| `calculator-api/tests/TechChallenge.Calculator.E2E/CalculatorE2ETests.cs` | **Create** |