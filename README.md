# Keyloop Technical Assessment — Unified Service Scheduler

**Scenario A (Ownership)** · **Backend implementation** · ASP.NET Core 8 · PostgreSQL 16 · EF Core

A REST API that books vehicle service appointments. A booking is confirmed only if a **service bay** *and* a **qualified technician** are both free for the **entire service duration**, and it is persisted as an `Appointment` linking customer, vehicle, technician and bay.

The interesting property: two customers racing for the last slot can **never** both win. Consistency is enforced by **PostgreSQL exclusion constraints**, not by application locks, so it holds across any number of API instances.

📄 **System Design Document:** [`docs/SYSTEM_DESIGN.md`](docs/SYSTEM_DESIGN.md) (architecture, data flow, assumptions, technology choices, observability, GenAI usage)
📜 **API contract:** [`docs/openapi.yaml`](docs/openapi.yaml) · Swagger UI at `/swagger` when running
🎬 **Video script:** [`docs/VIDEO_SCRIPT.md`](docs/VIDEO_SCRIPT.md)

---

## Quick start

Prerequisites: Docker (with Compose v2). For local development/tests also the .NET 8 SDK.

```bash
docker compose up --build          # PostgreSQL + API on http://localhost:8080 (seed data loaded)
open http://localhost:8080/swagger # or follow the cURL walkthrough:
./docs/curl-examples.sh            # books, replays, conflicts, back-to-back, validation, cancel
```

With tracing (Jaeger UI on http://localhost:16686):

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://jaeger:4317 docker compose --profile observability up --build
```

### Run from source

```bash
docker compose up -d db                      # just PostgreSQL
dotnet run --project src/Scheduler.Api       # http://localhost:8080
```

### Build & test

```bash
dotnet build
dotnet test
```

* Integration tests start a **real PostgreSQL via Testcontainers**, so Docker must be running.
* No Docker? Point the tests at any PostgreSQL 16 instance (user needs `CREATE EXTENSION btree_gist`, a trusted extension, or superuser):
  `TEST_DB_CONNECTION="Host=localhost;Port=5432;Database=scheduler_test;Username=postgres;Password=..." dotnet test`

### Seed data (used by the cURL examples)

| What | Id |
|---|---|
| Dealership **London** (3 bays, 3 technicians, 08:00–17:00) | `11111111-1111-1111-1111-111111111111` |
| Dealership **Manchester** (1 bay, 1 technician, 09:00–17:30) — handy for demoing conflicts | `22222222-2222-2222-2222-222222222222` |
| Service types: Oil Change 60m `cccccccc-…0001`, **MOT 60m `…0002`**, Brake 120m `…0003`, Full 180m `…0004`, EV check 90m `…0005` | `cccccccc-0000-0000-0000-00000000000N` |
| Customers Jane `dddddddd-…0001`, Omar `…0002` · Vehicles Golf `eeeeeeee-…0001`, Tesla `…0002` (Jane), Defender `…0003` (Omar) | |

> Booking times must be **in the future**, **15-minute aligned**, within **local opening hours**, Mon–Sat (see assumptions in the design doc).

### Minimal example

```bash
curl -i -X POST http://localhost:8080/api/v1/appointments \
  -H 'Content-Type: application/json' -H 'Idempotency-Key: demo-1' \
  -d '{"customerId":"dddddddd-0000-0000-0000-000000000001",
       "vehicleId":"eeeeeeee-0000-0000-0000-000000000001",
       "dealershipId":"22222222-2222-2222-2222-222222222222",
       "serviceTypeId":"cccccccc-0000-0000-0000-000000000002",
       "startTime":"2026-11-03T10:00:00Z"}'
# 201 Created + Location; repeat -> 200 (Idempotent-Replayed); overlap by another customer -> 409
```

---

## How it works (30-second version)

```
HTTP → Endpoints → BookingService → Domain (pure rules) ─┐
                         │                                 │ "which bay + technician?"
                         └──── EF Core ──► PostgreSQL ◄────┘
                                  INSERT … EXCLUDE USING gist (bay | technician | vehicle, time range)
```

1. Validate (future, 15-min slot, opening hours in the dealership's local time, ownership).
2. Load what is busy in the window; pure `ResourceAllocator` picks a free bay **and** a free qualified technician.
3. `INSERT` the appointment. If a concurrent request got there first, PostgreSQL rejects it (`23P01`); the service re-reads and re-allocates (bounded retries) or answers `409`.
4. Idempotency-Key makes client retries safe.

## Project layout

```
src/Scheduler.Api/
  Domain/           pure business rules (no I/O): BookingPolicy, ResourceAllocator, AvailabilityCalculator, entities
  Application/      BookingService — orchestration, retry-on-conflict, metrics/traces
  Infrastructure/   EF Core DbContext, idempotent schema + exclusion constraints, seed data
  Endpoints/        minimal-API routes, contracts, HTTP/problem-details mapping
  Observability/    ActivitySource + business metrics
tests/Scheduler.Tests/
  Unit/             domain logic (fast, deterministic)
  Integration/      full HTTP stack on real PostgreSQL, incl. concurrency races
docs/               system design, OpenAPI, cURL walkthrough, video script
```

## Observability

Structured JSON logs (Serilog) · OpenTelemetry traces & metrics (OTLP; set `OTEL_EXPORTER_OTLP_ENDPOINT`) · custom metrics `scheduler.bookings{outcome}`, `scheduler.booking.duration`, `scheduler.booking.conflict_retries` · `/health/live` and `/health/ready` · every error is RFC 7807 with a `traceId`. Details in §9 of the design doc.

---

## AI Collaboration Narrative

> **Author's note — edit this section before submitting.** It is written from the factual record of how this repository was produced. The assessment grades *your* direction and verification process, so replace the bracketed parts with what you actually did and decided, and re-run the verification steps yourself so every claim below is true for you.

**Tools.** Claude (chat + agentic file/shell tools) as the primary collaborator. `[add any others, e.g. GitHub Copilot]`

**Strategy for directing the AI**
1. *Choose before building.* I had the AI compare the four scenarios against the evaluation criteria and picked A + backend because it has the richest correctness problem (resource-constrained booking under concurrency) and matches my .NET / PostgreSQL / Docker / CI experience. `[your own reasoning]`
2. *Design before code.* Assumptions, architecture, data model and the concurrency strategy were agreed in the design document first; the code follows it, not the other way round.
3. *Keep the logic AI can get wrong small, pure and testable.* Scheduling rules live in dependency-free functions (`BookingPolicy`, `ResourceAllocator`, `AvailabilityCalculator`) so they can be verified in isolation, while the part that must be *correct under concurrency* is delegated to a database constraint rather than to clever application code.
4. *Constrain the AI's choices.* I specified the stack, folder boundaries, error contract (RFC 7807, 201/200/404/409/422), idempotency semantics and observability signals instead of letting it improvise.

**How I verified and refined its output**
* **Executed, not assumed.** The pure domain logic was compiled and run against 28 assertions (opening hours, DST/BST, half-open boundaries, skill matching, availability grid). The constraint SQL was run on a real PostgreSQL 16 for overlap on bay / technician / vehicle, back-to-back, cancelled rows and re-booking after cancel, confirming the `23P01` SQLSTATE the retry logic depends on.
* **Reviewed for design flaws, found one.** Reviewing the concurrency tests against the retry logic exposed that a fixed 3-attempt retry would reject the 4th of 4 racing requests even though capacity existed. Each lost race implies another request consumed a resource, so retries are now bounded by *resources + 1*.
* **Tests that would fail if the AI were wrong.** 23 unit tests over the pure rules, 15 integration tests on real PostgreSQL (Testcontainers), and 3 race tests that fire 4–10 simultaneous requests and assert the *exact* number of winners. An in-memory/SQLite database was deliberately avoided because it cannot express exclusion constraints and would have hidden the behaviour that matters most.
* **Environment limits, stated honestly.** `[Fill in: the sandbox that generated the first draft could not restore NuGet packages, so the API/EF/test projects were first compiled and run on MY machine. List what failed on first build/run and what you fixed.]`

**How I ensured final quality / ownership**
* `[Run `dotnet test` and paste the result; note any test you changed and why.]`
* I can explain every layer and the trade-off behind each decision (exclusion constraints vs. serializable transactions vs. pessimistic locks; why only `Confirmed` rows are constrained; why intervals are half-open; why the allocator is pure) — see design doc §7.
* CI (`.github/workflows/ci.yml`) builds, runs the full suite and builds the container image on every push.

**What I deliberately did not delegate:** where consistency lives, the HTTP/error contract, and scope boundaries (what is *not* built is listed in design doc §12).

---

## Known limitations / next steps

Authentication, customer/vehicle/dealership admin APIs, rescheduling, technician shifts, notifications (outbox), availability caching, versioned migrations, Prometheus scrape endpoint — see design doc §12.
