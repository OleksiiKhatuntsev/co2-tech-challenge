# CO₂ Tech Challenge

## Assistant Behavior

- **Always respond in Russian**, regardless of what language is used
- **Style:** concrete, no fluff — lead with the point, then details
- **For every architectural decision:** name the pattern explicitly, explain trade-offs (what we gain, what we sacrifice), teach when to apply it
- **For every question:** first explain *why* we do it this way, then *how*
- **Every architectural decision made during the session must be recorded in this file** under the relevant section or a new `## Architectural Decisions` section at the bottom

---

## What We Are Building

A **Calculator API** in .NET 8 that sits in front of two existing APIs:

```
Client → Calculator API → Measurements API  (energy in Watts, chaos: 30% error)
                        → Emissions API     (CO₂ factors in kg/kWh, chaos: 50% × 15s delay)
```

**Input:** `userId` (string), `from` / `to` (Unix timestamps, seconds since epoch)
**Output:** `double` — total CO₂ in kg

---

## Solution Structure

```
TechChallenge.sln
├── measurements-api/src/TechChallenge.Measurements.Api/   ← DO NOT BREAK (tested against original)
├── emissions-api/src/TechChallenge.Emissions.Api/          ← DO NOT BREAK (tested against original)
├── shared/src/
│   ├── TechChallenge.ChaosMonkey/    ← chaos injection (ErrorChaosMonkey, DelayChaosMonkey)
│   ├── TechChallenge.Common/         ← NotFoundException
│   └── TechChallenge.DataSimulator/  ← deterministic data generation via seeded RNG
└── calculator-api/src/TechChallenge.Calculator.Api/        ← TO BE CREATED
```

`Directory.Build.props` applies globally: `ImplicitUsings=enable`, `Nullable=enable`.

---

## Existing API Contracts

### Measurements API — `http://localhost:5153`

```
GET /measurements/{userId}?from={unix}&to={unix}
→ MeasurementResponse(long Timestamp, double Watts)[]
```

- Resolution: 1–10 seconds per user (deterministic, varies by user)
- `ErrorChaosMonkey`: 30% probability → throws `Exception` → HTTP 500
- Valid userIds: `alpha`, `betta`, `gamma`, `delta`, `epsilon`, `zeta`, `eta`, `theta`
- Swagger: `http://localhost:5153/swagger`

### Emissions API — `http://localhost:5139`

```
GET /emissions?from={unix}&to={unix}
→ EmissionResponse(long Timestamp, double KgPerWattHr)[]
```

- Resolution: exactly 900 seconds (15 minutes), no userId — global data
- `DelayChaosMonkey`: 50% probability → `Task.Delay(15s)`
- Swagger: `http://localhost:5139/swagger`

---

## Calculation Algorithm

```
for each 15-minute period [t, t+900):
    measurements = all Watts readings in this period
    avg_watts    = average(measurements)
    kWh          = avg_watts / 4 / 1000      ← /4 because 15min = 1/4 hour
    CO₂          = kWh × emission_factor[t]  ← factor from Emissions API

total = sum(CO₂ for all periods)
```

**Key assumption from spec:** resolution does not change per user, no missing data.
This means simple average — no need for weighted time-based integration.

---

## Critical Optimization Opportunity

**Primary load pattern: many users, same time period.**

Consequence: Emissions data is the same for all users in a given timeframe.
→ Cache emissions results by `(from, to)` key — one HTTP call serves all concurrent users.
→ This is **Cache-Aside** pattern: check cache → miss → fetch → store → return.

Data is always from the past → cache entries never go stale. TTL can be very long (hours/days).

---

## Chaos Handling Strategy

| API | Chaos Type | Strategy |
|-----|-----------|----------|
| Measurements | 30% HTTP 500 | **Retry** with exponential backoff (Polly) — 3 attempts sufficient |
| Emissions | 50% × 15s delay | **Timeout** + **Cache** — cached response bypasses chaos entirely |

For Measurements, consider **Circuit Breaker** if retry budget is exhausted.
For Emissions, the cache is the primary defense — timeout is a secondary safeguard.

---

## Docker Issues (Known Bugs)

**Emissions Dockerfile** (`emissions-api/src/TechChallenge.Emissions.Api/Dockerfile`):
- Line 1: `FROM mcr.microsoft.com/dotnet/aspnet:6.0` — wrong version, must be `aspnet:8.0`

**Measurements Dockerfile** (`measurements-api/src/TechChallenge.Measurements.Api/Dockerfile`):
- Missing `WORKDIR /src` before `COPY` in the build stage

**docker-compose.yml:** does not exist yet — must be created at repo root.

Build context for all Dockerfiles is the **repo root** (shared libraries require it).

---

## Build & Run Commands

```bash
# Build entire solution
dotnet build TechChallenge.sln

# Run services locally (each in its own terminal)
dotnet run --project measurements-api/src/TechChallenge.Measurements.Api
dotnet run --project emissions-api/src/TechChallenge.Emissions.Api
dotnet run --project calculator-api/src/TechChallenge.Calculator.Api

# Docker (after fixing Dockerfiles)
docker compose up --build
```

---

## Conventions

- Minimal API style (no controllers) — match existing projects
- Project name: `TechChallenge.Calculator.Api`
- 3rd-party libraries are allowed — Polly is the de-facto standard for resilience in .NET
- C# 12 features are encouraged (primary constructors, collection expressions, etc.)
