# Notes

## Contents

1. [Running the solution](#1-running-the-solution)
2. [Architecture](#2-architecture)
3. [Design decisions](#3-design-decisions)
4. [Measured results](#4-measured-results)
5. [Things I found in the boilerplate](#5-things-i-found-in-the-boilerplate)
6. [Tests](#6-tests)
7. [Challenges](#7-challenges)
8. [Towards production](#8-towards-production)

---

## 1. Running the solution

### Prerequisites

| Dependency | Setup |
|---|---|
| .NET 8 SDK | |
| RabbitMQ on `localhost:5672`, `guest/guest` | `docker compose up -d` from the repository root (management UI at http://localhost:15672), or the Windows service / `docker-up.bat` from the brief |
| SQL Server 2022 on `localhost` with Windows authentication | Run **`DatabaseGenerate.sql`** once. It creates `OT_Assessment_DB` and all objects, and is safe to re-run |

The connection string is the one from the brief, plus `TrustServerCertificate=True`:

```
SERVER=.; DATABASE=OT_Assessment_DB; Integrated Security=SSPI; TrustServerCertificate=True;
```

`Microsoft.Data.SqlClient` 5 encrypts by default, and a local SQL Server's self-signed certificate is otherwise rejected with *"The certificate chain was issued by an authority that is not trusted."*

**Using SQL Server in Docker instead** (`docker compose --profile sql up -d`): run `DatabaseGenerate.sql` against it, then override the connection string for both processes with an environment variable. No file needs editing:

```powershell
$env:ConnectionStrings__DatabaseConnection = "Server=localhost,1433;Database=OT_Assessment_DB;User Id=sa;Password=OtAssess!2026;TrustServerCertificate=True;"
```

### Run

1. Start **OT.Assessment.App** with the **`https`** launch profile. The Tester posts to `https://localhost:7120`.
2. Start **OT.Assessment.Consumer**.
3. Run **OT.Assessment.Tester**.

The exchange, queue and dead-letter queue are declared automatically by whichever process starts first. If either process logs `PRECONDITION_FAILED - inequivalent arg` for `ot.casino.wagers`, the broker still has that queue from a run with different queue settings: delete the queue in the management UI (Queues › ot.casino.wagers › Delete), or recreate the RabbitMQ container, and start again. `GET /health/ready` reports whether SQL Server and RabbitMQ are reachable.

> **About the row count after a Tester run:** expect slightly fewer than 7,000 rows in `dbo.CasinoWager`, typically about 6,930–6,970. The Tester sends some wagers twice; nothing is lost. See [section 5](#5-things-i-found-in-the-boilerplate). The consumer log reports duplicates on every batch (`... 5 already stored ...`), and this query proves every distinct wager is stored exactly once:
> ```sql
> SELECT COUNT(*) AS Wagers, COUNT(DISTINCT WagerId) AS DistinctWagers FROM dbo.CasinoWager;
> ```

---

## 2. Architecture

```
 Tester ──HTTP──▶ OT.Assessment.App ──publish (confirmed)──▶ RabbitMQ ──▶ OT.Assessment.Consumer ──TVP batch──▶ SQL Server
                   │  validate                               exchange ot.casino            │  buffer ▸ batch ▸ write ▸ ack
                   │  200 once broker has it                 queue ot.casino.wagers        │  bad message ▸ dead-letter
                   │                                         DLQ ot.casino.wagers.dead     ▼
                   └──────────── GET endpoints ◀──────────────────────────────────── stored procedures (Dapper)
```

| Project | Responsibility |
|---|---|
| `OT.Assessment.Core` | Message contract, read models and interfaces (`ICasinoWagerPublisher`, `ICasinoWagerWriter`, `IPlayerReadRepository`). No infrastructure dependencies. |
| `OT.Assessment.Infrastructure` | RabbitMQ connection, topology and publisher; Dapper repositories; health checks; DI registration. |
| `OT.Assessment.App` | REST API: validation, mapping, HTTP concerns only. |
| `OT.Assessment.Consumer` | Background service: consume, batch, persist, settle. |
| `OT.Assessment.UnitTests` | Unit, API-pipeline and SQL integration tests. |

The API and Consumer depend on the abstractions in Core and wire the implementations from Infrastructure. Swapping RabbitMQ for Kafka, or Dapper for something else, touches one project.

---

## 3. Design decisions

### API

- **POST returns `200 OK`, not `202 Accepted`.** `202` is the more RESTful answer for "accepted for asynchronous processing", but the Tester counts only `"OK"` as success. I kept `200` to meet the contract the assessment measures against. In a real system I'd agree `202` with the client.
- **`200` means durable.** The publisher uses **publisher confirms** with persistent messages, so the API responds only after RabbitMQ has stored the wager. A fire-and-forget publish would be faster but could lose wagers that were already acknowledged to the caller.
- **Pooled channels.** AMQP channels should not be shared by concurrent publishers, and opening one per request is expensive. A fixed pool of 16 channels, each awaiting its own confirms, is shared by all requests. A closed channel is replaced on next use. The pool is filled at start-up.
- **Validation at the edge.** DataAnnotations reject malformed wagers with a `400 ValidationProblemDetails`. Identifiers are `Guid?`, so a missing id is a validation error rather than a silent `Guid.Empty`.
- **`amount` is bound as `decimal`, never `double`.** A unit test caught that `(decimal)doubleValue` kept only about 15 significant digits (`38273.974454660885` became `38273.9744546609`). System.Text.Json parses the JSON number straight into `decimal`, so money never passes through binary floating point.
- **Bus outage returns `503` with `Retry-After`** and a ProblemDetails body, so callers can tell "retry later" from a bug. The controller returns it directly rather than letting the exception reach the exception-handler middleware, because in .NET 8 that middleware logs every exception it sees as an error with a stack trace (see Challenges).
- **A circuit breaker fails fast when RabbitMQ is down.** Each publish is bounded by a 5-second timeout and runs behind a Polly circuit breaker. Once half of at least 20 publishes in 10 seconds have failed, the circuit opens for 15 seconds: requests get a `503` straight away, before a pooled channel is rented, with `Retry-After` set to the time left on the break. Without it, every request during an outage would hold one of the 16 channels while waiting to fail, and the pool would back up. After the break one request is let through as a trial; success closes the circuit, failure starts a new break.
  - The timeout matters as much as the breaker. Under a memory or disk alarm RabbitMQ keeps the connection open but stops reading from publishers, so a publish would wait indefinitely and the breaker would never see a failure. The publisher also listens for the broker's "connection blocked" notification and opens the circuit immediately, closing it again on "unblocked".
  - A publish that fails or times out empties its pool slot, because the channel may still be waiting on a confirm that will never be read. Caller cancellation is not counted as a broker failure.
  - `/health/ready` reports `Degraded` while the circuit is not closed: reads still work, new wagers are being refused.
- `GET {playerId}/casino` validates `page ≥ 1` and `1 ≤ pageSize ≤ 100`. `topSpenders` validates `1 ≤ count ≤ 100`. An unknown player returns an empty page, not a 404, because "no wagers yet" is a valid state.

### Consumer

- **Batching is the main throughput lever.** The RabbitMQ callback only copies the delivery into a bounded in-memory channel. One loop drains it into batches of up to **500** (or whatever has arrived after a **200 ms** window), writes the batch in **one stored-procedure call** using a table-valued parameter, then acknowledges each delivery on its own tag. Prefetch is two batches (1,000), so the next batch fills while the current one is written.
- **Ack after commit.** Messages are acknowledged only after the database transaction commits. A crash at any point causes redelivery, never loss (at-least-once delivery).
- **Idempotent writes make redelivery safe.** `WagerId` is a unique key. The procedure de-duplicates within the batch and skips wagers already stored, so redelivered or duplicate messages are acknowledged without being stored or totalled twice.
- **Poison messages are dead-lettered.**
  - A malformed message, or one that breaks database invariants, is published by the consumer to `ot.casino.wagers.dead` with a `dead-letter-reason` header saying why. The original delivery is acked only after the broker confirms that publish, so a failed dead-letter publish means redelivery, not loss. The main queue also keeps its `x-dead-letter-exchange` argument as a safety net: anything rejected without requeue lands in the same queue instead of being dropped.
  - If SQL Server rejects a whole batch with a non-transient error, the processor writes each message individually. The one bad row is dead-lettered and the other 499 are stored. A row that cannot be joined is not inserted and is not added to the player total; the procedure fails the batch so that row is isolated.
- **Transient errors are retried.** Deadlocks, timeouts, failover errors and any connection-level failure (SQL Server severity 20 and above, or a socket error) are retried with exponential backoff and jitter (Polly). If the database stays down, the batch is held, unacknowledged, and written again later (see the circuit breaker below). Only errors about the data itself dead-letter a wager, so a database outage never does. This was caught in end-to-end testing: stopping SQL Server mid-run originally dead-lettered a valid wager, because a failed pre-login handshake was not on the retry list. With the fix, the wager waits in the queue and is stored once SQL Server is back.
- **A circuit breaker holds the batch during a database outage.** It sits outside the retries, so a batch that fails after all its retries counts as one failure; two such batches in a row open it for 30 seconds. While it is open the write is refused without touching SQL Server, and the worker keeps the current batch unacknowledged and waits for the break to pass instead of nacking and requeueing. Because nothing new is read, the prefetch limit stops the broker delivering more, so the backlog waits in the queue rather than cycling through nack and requeue every few seconds (which also inflated redelivery counts and the log). When the break is over the held batch is the trial write. Shutting down, or the channel closing, ends the hold and the deliveries are redelivered as before.
  - Only transient errors count towards opening it. A row the database rejects is dealt with by isolation and dead-lettering; if it counted, one poison wager could stop the whole pipeline.
  - An open circuit must never be mistaken for bad data. The processor treats any non-transient error as a rejected row, and Polly's "circuit open" exception is not a SQL transient error, so it is excluded explicitly; otherwise an outage would dead-letter every wager as "rejected by the database". A test pins this.
  - Unexpected errors that are not database outages still requeue the batch and pause, as before.
- **Testable core.** The settlement rules live in `CasinoWagerBatchProcessor`, which has no RabbitMQ dependency. The worker only translates its result into ack/nack calls.

### Database

- **Normalised to remove duplication.**
  - `Provider` (unique name).
  - `Game`, unique per `(ProviderId, Name)`, because the Tester reuses game names across providers.
  - `Player`, keyed by `AccountId` with the username.
  - `CasinoWager`, holding only per-wager facts and foreign keys to the above.
- **`sessionData` is not stored.** The brief says not all data is required, and no endpoint uses it. It still travels on the queue, which carries the complete event, so a future consumer can use it.
- **Clustered key is a `BIGINT IDENTITY`, not the GUID.** Random GUIDs as a clustered key cause page splits and fragmentation under insert load. `WagerId` is a separate unique non-clustered key.
- **Covering index `IX_CasinoWager_PlayerId_CreatedDateTime (PlayerId, CreatedDateTime DESC, CasinoWagerId DESC) INCLUDE (WagerId, GameId, Amount)`.** The paged query is an index seek already in "latest first" order, with no sort and no key lookups.
- **Running totals for top spenders and page counts.** `Player.TotalAmountSpent` and `TotalWagerCount` are updated **in the same transaction** from the rows the insert actually wrote, so they cannot drift from `dbo.CasinoWager`. Top spenders becomes an ordered scan of `IX_Player_TotalAmountSpent` instead of a `SUM … GROUP BY` over every wager, and the page total is a single-row read instead of a `COUNT(*)`. This is the one deliberate denormalisation. I considered an indexed view (`SUM`/`COUNT_BIG` per player): it is maintained automatically, but it is stricter about session settings and adds hidden write cost. I preferred explicit, visible logic in the procedure.
- **`READ_COMMITTED_SNAPSHOT` is on.** The GET endpoints read the last committed version and are never blocked by an open batch transaction. This is what keeps the GETs fast during the load test.
- **Types.**
  - `DECIMAL(19,4)` for money (rounded away from zero to four places).
  - `DATETIMEOFFSET` to keep the caller's offset.
  - `CHAR(2)` country code.
  - `UNIQUEIDENTIFIER` for external ids.
- **The insert procedure is safe under concurrency.** Dimension inserts use `UPDLOCK, HOLDLOCK` existence checks, so several consumer instances can run it at the same time.
- **Naming conventions.**
  - Singular PascalCase tables.
  - `PK_`, `FK_`, `UQ_`, `IX_`, `CK_`, `DF_` constraint prefixes.
  - `usp_<Entity>_<Action>` procedures.

---

## 4. Measured results

Measured in a 2-vCPU Linux container with SQL Server 2022 and RabbitMQ 3 in Docker, with the API, Consumer and Tester all on the same machine. A real workstation should do better.

| Scenario | Result |
|---|---|
| Tester, 7,000 requests | **7,000 OK, 0 failed**, ~233–250 req/s (the rate the Tester injects) |
| API latency, warm | p50 9 ms, p95 36–43 ms |
| API latency, first run after start-up | p50 11–12 ms, p95 190–250 ms (JIT compilation and per-request TLS handshakes on a 2-core box shared with SQL Server and RabbitMQ) |
| Consumer keeping pace | Last wager stored within a second of the last request; the queue never built up |
| **Consumer drain: 7,000 messages already queued** | **2.2 s, including process start-up**; 14 batches of 500; 1.5 s total database time; a warm 500-row batch takes ~40–80 ms |
| GET endpoints during load | 200 OK in 85–210 ms while batches were being written |
| **Circuit breakers, end to end** (same setup) | Normal Tester run: neither circuit tripped, 7,000 / 7,000 accepted. SQL Server stopped for 66 s mid-run: the consumer held 1,000 unacked with no requeue churn, the circuit opened, and once SQL Server was back the held batch was the trial write and the ~3,750 backlog drained within one break; all 7,000 accepted, nothing dead-lettered, totals reconciled. RabbitMQ stopped: 503s in under 20 ms, the circuit opened after ~20 failures, `Retry-After` counted down from 15, the first request after the break succeeded and closed it, and every wager that got a 200 was stored |
| Integrity after 3 runs (20,876 wagers) | `COUNT(*) = COUNT(DISTINCT WagerId)`; `SUM(Player.TotalAmountSpent) = SUM(CasinoWager.Amount)`; `SUM(TotalWagerCount) = COUNT(*)`; both queues empty; no errors logged |

---

## 5. Things I found in the boilerplate

- **The Tester did not compile.** A stray `/` after `.RuleFor(o => o.Username, f => f.Person.UserName)` in `BogusGenerator.cs`. Fixed in its own commit.
- **The Tester sends duplicate wagers.** It picks the payload with `total[(int)context.InvocationNumber]`, and under `IterationsForInject` NBomber does not hand out unique invocation numbers. I confirmed this in isolation with a probe using the same load simulation: **7,000 invocations produced only 6,945 distinct invocation numbers.** About 0.5–1% of requests therefore repeat an earlier wager, with the same `WagerId`. The system stores each wager once and acknowledges the repeat, which is the correct behaviour for an idempotent consumer. Hence ~6,950 rows rather than 7,000. A fix for the Tester would be an `Interlocked.Increment` counter instead of `InvocationNumber`. I left the Tester's behaviour alone so the solution is measured against the Tester you will run.
- **The Tester creates a new `HttpClient` per request,** so every request pays a new TCP and TLS handshake. That makes the measured latency pessimistic, and at higher rates it would exhaust sockets on the client.
- `Program.cs` registered `AddSwaggerGen` twice, and the project wrote its XML documentation file into the source folder, where it was committed. Both are tidied up.

---

## 6. Tests

`dotnet test` runs 75 tests:

- **Consumer:**
  - batch window and size behaviour;
  - settlement rules: dead-lettering, isolating a rejected row, transient retry, failure after retries are exhausted, duplicates;
  - the SQL Server circuit breaker, on a fake clock: it opens after repeated transient failures and then refuses without calling the database; an open circuit is classed as "retry later", never as a rejected row; rejected rows never open it; a trial after the break closes it on success and reopens it on failure;
  - per-tag ack and requeue, including a batch whose tags are not a contiguous prefix, and a poison message that is not acked when the dead-letter publish fails;
  - message invariants.
- **API (real ASP.NET Core pipeline via `WebApplicationFactory`, bus and database substituted):**
  - the brief's sample on the Tester's exact route and casing (`/api/Player/CasinoWager`, `"Username"`, `"Duration"`) returns `200`;
  - validation failures return `400`, including an empty `wagerId` or `accountId`;
  - bus outage returns `503`, and an open circuit's remaining break becomes `Retry-After` in whole seconds, rounded up;
  - response shapes match the brief;
  - paging bounds are enforced.
- **Publish circuit breaker:** the real publisher against a port with nothing listening, on a fake clock. Failures below the threshold carry no retry hint; after the threshold it refuses at once with `Retry-After` equal to the break; after the break the broker is tried again; it can be opened and closed by hand, as the "connection blocked" notification does. Settings validation and `Retry-After` rounding are covered too.
- **Schema guard:** a test parses `DatabaseGenerate.sql` and checks that the TVP column order matches the C# `DataTable`. TVPs bind by position, so drift between the two would put values in the wrong columns.
- **SQL integration** (runs when `OT_TEST_SQL_CONNECTION` is set; otherwise skipped):
  - idempotent re-writes;
  - paging order and totals;
  - unknown player;
  - top-spender ordering;
  - same game name under two providers.

```powershell
$env:OT_TEST_SQL_CONNECTION = "Server=.;Database=OT_Assessment_DB;Integrated Security=SSPI;TrustServerCertificate=True;"
dotnet test
```

CI (GitHub Actions) builds and runs the test suite on every push.

---

## 7. Challenges

- **Telling loss apart from duplicates.** The first load test stored 6,969 of 7,000. Before assuming lost messages, I checked every hop: all 7,000 requests got `200`, the queue was empty, nothing was dead-lettered, and the consumer had acknowledged 7,000 deliveries, 31 of them as "already stored". That pointed at the input, and the NBomber probe confirmed it. Logging duplicates per batch is what made this visible, so it stays in.
- **Cold-start latency.** I assumed the cold p95 was the RabbitMQ connection and added a start-up warm-up for the channel pool. Re-measuring showed the warm-up is worthwhile but was not the main factor: on this 2-core machine the cold cost is mostly JIT compilation and the Tester's per-request TLS. The notes say so rather than over-claiming.
- **Acknowledging one tag at a time.** The first version settled each batch with one `BasicAck(multiple: true)`. That is correct here: there is one channel and the buffer keeps delivery order, so a batch is always a contiguous run of tags, and a redelivered message comes back with a new, higher tag. But a multiple-ack settles *every* outstanding tag up to the one given, so it is only safe while that invariant holds, for example it would break if batches were ever processed in parallel. Each delivery is now acked or requeued on its own tag. Measured cost: total database-and-ack time for a 7,000-message backlog went from about 1.0 s to 1.6 s; the backlog still drained in 2.5 s.

- **One error per refused request.** Running the stack with RabbitMQ stopped showed every 503 being logged twice at error level, once with a full stack trace by ASP.NET Core's exception-handler middleware, which in .NET 8 logs every exception before any `IExceptionHandler` runs. That was true of every 503 before the circuit breaker too, but with an open circuit refusing requests at full rate it would flood the logs. The 503 is an expected outcome of posting a wager, so the controller now returns it directly: a genuine publish failure is logged once, and a request refused while the circuit is open is logged at debug level only.
- **A circuit that never closed.** The first version of the publish breaker checked the circuit state before renting a channel, to fail fast. A test that advanced a fake clock past the break showed the circuit stayed open for good: Polly keeps reporting `Open` after the break has passed, until a call goes through and moves it to half-open, and the early check meant no call ever did. The early check now asks whether the break is still running, not what state Polly reports.

---

## 8. Towards production

**Reliability**

- Quorum queues (replicated) instead of classic queues, and a RabbitMQ cluster.
- A tool or endpoint to inspect and replay the dead-letter queue.
- Message schema versioning (a version header) so publisher and consumer can be deployed independently.

**Observability**

- OpenTelemetry tracing propagated through the message headers, so one trace spans API → queue → consumer → SQL.
- Metrics: queue depth, consumer lag, batch size and latency, dead-letter rate.
- Structured logs shipped to a central sink (Serilog to Seq or Elastic).
- Alerting on consumer lag and dead-letter growth.

**Scale**

- Run several consumer instances. The procedure is already safe under concurrency. At higher volume, cache the provider, game and player ids in memory to shrink the per-batch upsert.
- Keyset (seek) pagination on `(CreatedDateTime, CasinoWagerId)` instead of `OFFSET` for deep pages.
- Partition `CasinoWager` by `CreatedDateTime`, with an archive or retention policy.
- Short-lived caching of `topSpenders` (an output cache of a few seconds), since it is a leaderboard that tolerates slight staleness.
- If the API ever needed to write to the database as well as publish, a transactional outbox to keep the two consistent.

**Security and operations**

- Authentication and authorisation on the API (client credentials for game platforms).
- Rate limiting per client.
- Secrets in a vault or managed identity instead of configuration files.
- Containerise the API and Consumer.
- Run the SQL integration tests in CI against a SQL Server service container.
- Database migrations through a versioned tool (DbUp or SSDT) rather than a single script.
- Decide how username changes for an existing account are handled. Today the first username seen is kept.
