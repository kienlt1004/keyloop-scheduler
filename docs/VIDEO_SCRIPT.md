# Video script (target 8 minutes, hard limit 10)

Speak in your own words — this is a skeleton with timings and what to *show*. Keep a terminal, the repo, Swagger UI and the design doc open in tabs.

| Time | Section | Say | Show |
|---|---|---|---|
| 0:00–0:45 | **Intro** | Who you are (role, stack: .NET/React/Docker/CI-CD, AI-assisted dev). Scenario A: Unified Service Scheduler. Why you chose it and the **backend**: richest correctness problem, matches your strengths. | Title slide or README header |
| 0:45–1:30 | **The problem** | Book vehicle + service + dealership + time. Needs a free bay **and** a qualified technician for the *whole* duration. The hard part: two customers racing for the last slot. | Design doc §1 |
| 1:30–4:30 | **Design walkthrough** | Architecture diagram (stateless API → PostgreSQL). Layers; why the domain is pure. **Concurrency decision**: options considered (lock, serializable, pessimistic, Redis) → exclusion constraints + optimistic retry; half-open ranges; cancel frees slot via `WHERE status='Confirmed'`; idempotency key. Assumptions (§2). Observability: logs, OTel traces, 3 business metrics, health. | Diagram, sequence diagram, `DbInitializer.cs` SQL, `BookingService.cs` retry loop |
| 4:30–6:00 | **AI collaboration story (1–2 min)** | Strategy: design first, small pure logic, DB owns consistency. Verification: ran domain checks, ran the SQL on real Postgres, **the bug review caught** (fixed retry count rejects 4th of 4 racers → bound by resources+1), tests that assert exact winner counts, avoided SQLite. What *you* corrected on your machine. What you did not delegate. | README AI narrative; the retry-bound code; a failing→passing moment from your own run |
| 6:00–7:30 | **Demo** | `docker compose up`; Swagger/`curl-examples.sh`: book → 201; same Idempotency-Key → 200; overlap → 409; back-to-back → 201; outside hours → 422; cancel then rebook. Optionally Jaeger trace for one booking. Run `dotnet test`, point at the 10-requests-1-winner test. | Terminal + Swagger + (Jaeger) |
| 7:30–8:30 | **Learnings & challenges** | Learned: push invariants into the database; pure functions make AI output verifiable. Challenges: time zones/DST, retry bound, testing real concurrency. Next: auth, rescheduling, availability cache, migrations, outbox. | Design doc §12 |

**Checklist before recording**
- [ ] `docker compose up --build` works from a clean clone
- [ ] `dotnet test` is green on your machine (note the count)
- [ ] You can explain every line of `BookingService.BookCoreAsync` and the SQL constraints
- [ ] AI narrative edited to reflect what *you* actually did
- [ ] Booking dates in the demo are in the future (the script uses "next Tuesday")
