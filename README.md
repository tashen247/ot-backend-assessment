# Online Betting Data Capture

A .NET 8 solution that captures casino wager events at load, stores them in SQL Server, and serves player wager history and a top-spenders leaderboard, built for the OT backend assessment ([original brief](docs/ASSESSMENT.md)).

```
 Tester ──HTTP──▶ OT.Assessment.App ──publish (confirmed)──▶ RabbitMQ ──▶ OT.Assessment.Consumer ──TVP batch──▶ SQL Server
                   │ validate                                 ot.casino.wagers            │ buffer ▸ batch ▸ write ▸ ack
                   │ 200 once the broker has it               DLQ ot.casino.wagers.dead   │ bad message ▸ dead-letter
                   └──────────── GET endpoints ◀─────────────────────────────── stored procedures (Dapper)
                                      │ topSpenders cached 5 s in Redis (shared; optional)
```

## Highlights

- **A 200 means the wager is durable.** The API returns only after RabbitMQ confirms the message is persisted.
- **Batched writes.** The consumer stores up to 500 wagers per stored-procedure call using a table-valued parameter, and acknowledges messages only after the transaction commits.
- **Idempotent on `WagerId`.** Redelivered or duplicate messages are never stored or totalled twice.
- **Nothing is silently lost.** Bad messages go to a dead-letter queue with the reason recorded; database and broker outages are retried and held, not dropped.
- **Circuit breakers on both sides.** If RabbitMQ is down or blocking, the API stops trying and answers `503` at once with a `Retry-After`. If SQL Server is down, the consumer holds its batch unacknowledged and stops pulling more until the database recovers.
- **Reads never wait for writes.** Read committed snapshot keeps both GET endpoints fast during load.
- **The leaderboard is cached in Redis.** `topSpenders` is served from a cache shared by every API instance for up to 5 seconds, so SQL Server runs it about once per 5 seconds however many callers there are. Redis is optional: if it is down the API serves from SQL Server with no errors, and caching resumes as soon as Redis is back.

## Results against the provided Tester

| | |
|---|---|
| Tester requests | 7,000 / 7,000 returned 200 |
| 7,000-message backlog | stored in about 2.5 s from consumer start |
| GET endpoints during load | all 200, typically under 60 ms |
| `topSpenders` under 20 concurrent readers while the Tester runs | p50 32–38 ms with Redis vs 53–55 ms without; SQL Server queried 6–7 times in 30 s instead of about 9,000 |
| Integrity | every distinct wager stored once; player totals reconcile with the wager table to the cent |

Measured on a 2-vCPU machine running SQL Server, RabbitMQ, the API, the consumer and the Tester together. Details in [Notes.md](Notes.md#4-measured-results).

## Quick start

Prerequisites: .NET 8 SDK, SQL Server on `localhost` (Windows authentication), RabbitMQ on `localhost:5672` with `guest/guest`, and optionally Redis on `localhost:6379`.

```powershell
# 1. RabbitMQ (management UI on http://localhost:15672) and Redis
docker compose up -d

# 2. Database: run DatabaseGenerate.sql against your SQL Server (SSMS or sqlcmd); it is safe to re-run
sqlcmd -S localhost -E -C -i DatabaseGenerate.sql

# 3. API (the Tester calls https://localhost:7120)
dotnet run --project src/OT.Assessment.App --launch-profile https

# 4. Consumer (second terminal)
dotnet run --project src/OT.Assessment.Consumer

# 5. Load test (third terminal)
dotnet run --project test/OT.Assessment.Tester
```

`GET https://localhost:7120/health/ready` reports whether SQL Server and RabbitMQ are reachable, and `Degraded` (still a 200) if Redis is not. Swagger is at `https://localhost:7120/swagger`. To use SQL Server in Docker instead, see [Notes.md](Notes.md#1-running-the-solution).

## Endpoints

| Method | Route | Purpose |
|---|---|---|
| `POST` | `api/player/casinowager` | Accept a wager and publish it to the queue |
| `GET` | `api/player/{playerId}/casino?pageSize=10&page=1` | A player's wagers, latest first, paged |
| `GET` | `api/player/topSpenders?count=10` | Players by total amount wagered, highest first (cached up to 5 s) |

## Projects

| Project | Responsibility |
|---|---|
| `OT.Assessment.Core` | Message contract, read models and interfaces; no infrastructure dependencies |
| `OT.Assessment.Infrastructure` | RabbitMQ connection, topology and confirmed publisher; Dapper repositories; health checks |
| `OT.Assessment.App` | REST API: validation, mapping, HTTP concerns, the leaderboard cache |
| `OT.Assessment.Consumer` | Background service: consume, batch, persist, settle |
| `OT.Assessment.UnitTests` | Unit, API-pipeline, SQL Server and Redis integration tests |
| `OT.Assessment.Tester` | The provided NBomber load test |

## Tests

```powershell
dotnet test
# include the SQL Server integration tests:
$env:OT_TEST_SQL_CONNECTION = "Server=.;Database=OT_Assessment_DB;Integrated Security=SSPI;TrustServerCertificate=True;"
# and the Redis integration tests:
$env:OT_TEST_REDIS_CONNECTION = "localhost:6379"
dotnet test
```

## Further reading

- [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md): a step-by-step walkthrough of a wager through the API, RabbitMQ, the consumer and SQL Server, with the RabbitMQ and Docker concepts explained.
- [Notes.md](Notes.md): design decisions, measured results, findings in the boilerplate, challenges, and what I would add for production.
- [docs/ASSESSMENT.md](docs/ASSESSMENT.md): the original assessment brief.
