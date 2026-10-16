# How it works

A walkthrough of what happens to a casino wager from the moment it is posted until it appears in the GET endpoints, with the RabbitMQ and Docker concepts explained along the way. For setup see the [README](../README.md); for the reasoning behind each decision and measured results see [Notes.md](../Notes.md).

## Contents

1. [The big picture](#1-the-big-picture)
2. [Docker: what runs where](#2-docker-what-runs-where)
3. [RabbitMQ concepts used here](#3-rabbitmq-concepts-used-here)
4. [The topology this solution declares](#4-the-topology-this-solution-declares)
5. [Life of a wager, step by step](#5-life-of-a-wager-step-by-step)
6. [Reading the data back](#6-reading-the-data-back)
7. [What happens when things go wrong](#7-what-happens-when-things-go-wrong)
8. [Configuration reference](#8-configuration-reference)
9. [Watching it work](#9-watching-it-work)
10. [Where to find things in the code](#10-where-to-find-things-in-the-code)

---

## 1. The big picture

There are two .NET processes and two pieces of infrastructure:

```
 ┌──────────┐  POST   ┌─────────────────────┐ publish  ┌────────────────────────┐ deliver ┌─────────────────────────┐  TVP batch  ┌────────────┐
 │  Tester  │ ──────▶ │  OT.Assessment.App  │ ───────▶ │        RabbitMQ        │ ──────▶ │ OT.Assessment.Consumer  │ ──────────▶ │ SQL Server │
 │ (NBomber)│ ◀────── │  (ASP.NET Core API) │ ◀─────── │ exchange  ot.casino    │ ◀────── │  (BackgroundService)    │             │            │
 └──────────┘  200    └─────────────────────┘ confirm  │ queue ot.casino.wagers │   ack   └─────────────────────────┘             └────────────┘
                              │                        │ DLQ ot.casino.wagers.dead                                                     ▲
                              └───────────── GET endpoints read via stored procedures (Dapper) ────────────────────────────────────────┘
```

The key idea is that **writing is split in two**:

- The **API** does the fast part: validate the wager, hand it to RabbitMQ, and answer the caller as soon as RabbitMQ has safely stored it. It never touches the database on the write path.
- The **Consumer** does the slow part: pull wagers off the queue, group them into batches of up to 500, and write each batch to SQL Server in a single stored-procedure call.

The queue in the middle absorbs bursts. If wagers arrive faster than SQL Server can store them, they wait in RabbitMQ instead of slowing down or failing the API. If the Consumer or the database is down, wagers keep being accepted and are stored once it comes back.

Each half of the write path also has a **circuit breaker** guarding the dependency it calls: the API guards RabbitMQ, and the Consumer guards SQL Server. When that dependency is clearly down, the breaker stops calls to it for a while instead of letting every call wait to fail (see [§7](#circuit-breakers)).

**Reads** (`GET`) go straight from the API to SQL Server. They do not involve RabbitMQ at all.

---

## 2. Docker: what runs where

`docker-compose.yml` defines the infrastructure only. The API and Consumer run on your machine with `dotnet run`.

| Service | Image | Ports | When it starts |
|---|---|---|---|
| `rabbitmq` (container `ot-rabbitmq`) | `rabbitmq:3-management` | `5672` AMQP (what the apps talk to), `15672` management web UI | Always: `docker compose up -d` |
| `sqlserver` (container `ot-sqlserver`) | `mcr.microsoft.com/mssql/server:2022-latest` | `1433` | Only with the `sql` profile: `docker compose --profile sql up -d` |

Things worth knowing:

- **`-management` image.** The plain `rabbitmq` image has no web UI. The `-management` tag adds the UI at http://localhost:15672 (login `guest` / `guest`), which is the easiest way to see queues, message rates and dead letters.
- **Port mapping `"5672:5672"`** means `host:container`. The apps connect to `localhost:5672` and Docker forwards it into the container.
- **Health check.** `rabbitmq-diagnostics -q ping` runs every 10 s, so `docker ps` shows the container as `healthy` once the broker is actually ready, not just started.
- **Profiles.** `profiles: ["sql"]` keeps SQL Server out of a plain `docker compose up`. That lets you use a local Windows SQL Server by default and opt into the container only when you want it.
- **No volumes are declared.** Queue contents and the SQL database live inside the container. `docker compose down` removes the containers and therefore that data; `docker compose stop` keeps it.
- **The `guest` user** can only log in from `localhost`. That works here because Docker forwards the port to your machine.

Useful commands:

```powershell
docker compose up -d                  # start RabbitMQ
docker compose --profile sql up -d    # start RabbitMQ and SQL Server
docker compose ps                     # status and health
docker compose logs -f rabbitmq       # broker logs
docker compose stop                   # stop, keep data
docker compose down                   # stop and remove containers (queue contents are lost)
```

---

## 3. RabbitMQ concepts used here

| Concept | What it is | How this solution uses it |
|---|---|---|
| **Connection** | A TCP connection to the broker. Expensive to open. | One per process, shared, owned by `RabbitMqConnectionProvider`. Automatic recovery reconnects after a broker restart. |
| **Channel** | A lightweight virtual connection inside a connection. Not safe to share between concurrent publishers. | The API keeps a **pool of 16 channels**; the Consumer uses one channel for consuming and one for dead-lettering. |
| **Exchange** | Where publishers send messages. It routes them to queues; it stores nothing. | `ot.casino` (direct) for wagers, `ot.casino.dlx` (direct) for dead letters. |
| **Queue** | Where messages wait until a consumer takes them. | `ot.casino.wagers` (main) and `ot.casino.wagers.dead` (dead-letter queue). |
| **Binding + routing key** | A rule on an exchange: "messages with routing key X go to queue Y". A *direct* exchange matches the key exactly. | Both queues are bound with routing key `casino.wager`. |
| **Durable queue / persistent message** | A durable queue survives a broker restart; a persistent message is written to disk. You need both for a message to survive a restart. | All exchanges and queues are durable; every message is published with `Persistent = true`. |
| **Publisher confirms** | The broker tells the publisher "I have stored this message". | The API waits for the confirm before returning `200`, so a `200` means the wager is safe. |
| **`mandatory: true`** | If no queue matches the routing key, the broker returns the message instead of silently dropping it. | Set on every publish, so a missing binding surfaces as an error rather than lost wagers. |
| **Manual acknowledgement** | The consumer tells the broker "done, you can delete it". Until then the message is *unacked* and will be redelivered if the consumer disconnects. | `autoAck: false`. The Consumer acks only after the SQL transaction commits. |
| **Nack with requeue** | "I could not process this; put it back." | Used when a batch fails for an unexpected reason. A database outage does not nack: the batch is held instead (see §7). |
| **Prefetch (QoS)** | The maximum number of unacked messages the broker pushes to a consumer at once. | 1,000 (two batches), so the next batch fills while the current one is being written. It also means a Consumer that is holding a batch during a database outage stops receiving more. |
| **Delivery tag** | A per-channel sequence number identifying one delivery, used when acking. | Each delivery is acked or nacked on its own tag; `multiple: true` is never used. |
| **Dead-letter exchange (DLX)** | Where a queue sends messages that are rejected without requeue (or expire). | `x-dead-letter-exchange = ot.casino.dlx` on the main queue, as a safety net. The Consumer also publishes poison messages there explicitly (see §5, step 7). |
| **Connection blocked** | Under a memory or disk alarm the broker keeps connections open but stops reading from publishers, and notifies them with `connection.blocked` (then `connection.unblocked`). | The API opens its publish circuit on "blocked" and closes it on "unblocked". |

**Delivery guarantee: at-least-once.** A message is only deleted from the queue after it is acked, and it is only acked after the database commit. So if anything crashes in between, the message comes back. The flip side is that a message can occasionally be delivered *twice*, which is why the database insert is idempotent on `WagerId` (see §5, step 6).

---

## 4. The topology this solution declares

`RabbitMqTopology.DeclareAsync` creates everything below. Declarations are idempotent (declaring something that already exists with the same settings does nothing), so **both** the API and the Consumer call it at start-up, and it doesn't matter which starts first.

```
                                     routing key: casino.wager
  API ── publish ──▶ [ot.casino]  (direct exchange) ───────────────▶ (ot.casino.wagers)   durable queue
                                                                        │  x-dead-letter-exchange = ot.casino.dlx
                                                                        │
  Consumer ── publish poison message ──┐                                │ rejected without requeue
                                       ▼                                ▼
                                [ot.casino.dlx] (direct exchange) ─────▶ (ot.casino.wagers.dead)   durable queue
                                                 routing key: casino.wager
```

> **If you change a queue's arguments** (for example add or remove `x-dead-letter-exchange`), RabbitMQ refuses to redeclare the existing queue with `PRECONDITION_FAILED - inequivalent arg`. Delete the queue in the management UI, or recreate the container, and start again.

---

## 5. Life of a wager, step by step

### Step 1: The request arrives at the API

`POST /api/player/casinowager` with a JSON body. ASP.NET Core binds it to `CasinoWagerRequest` and runs the DataAnnotations validation before the controller code runs. A failure returns `400` with a `ValidationProblemDetails` body listing the fields.

- All ids are `Guid?` with `[Required]`, so a missing id fails validation instead of silently becoming `00000000-…`. `WagerId` and `AccountId` additionally reject an explicit empty GUID (`[NotEmptyGuid]`).
- `Amount` is bound straight from the JSON number to `decimal`, so money never passes through `double`.
- `CountryCode` must be exactly two characters and is upper-cased when mapped.

**Code:** `src/OT.Assessment.App/Contracts/CasinoWagerRequest.cs`, `Controllers/PlayerController.cs`

### Step 2: The request is mapped to a bus message

`request.ToMessage()` produces a `CasinoWagerMessage` (in `OT.Assessment.Core`). This record is the contract between API and Consumer. It carries the **whole** event, including `sessionData`, which the database does not store, so a future consumer could still use it.

`MessageSerializer` is the single place that defines the JSON wire format (System.Text.Json with source generation, web casing). The API serialises with it and the Consumer deserialises with it, so they cannot drift apart.

**Code:** `src/OT.Assessment.Core/Messaging/`

### Step 3: The API publishes and waits for the confirm

`RabbitMqCasinoWagerPublisher.PublishAsync`:

1. **Checks the circuit.** If the publish circuit is open (RabbitMQ has recently been failing, or is blocking publishers), it refuses straight away, before renting a channel. See [§7](#circuit-breakers).
2. **Rents a channel** from the pool of 16. If all 16 are busy, the request waits for one to come back. If a pooled channel has been closed by the broker, it is replaced.
3. **Publishes** to exchange `ot.casino` with routing key `casino.wager`, `mandatory: true`, and properties `Persistent = true`, `MessageId = WagerId`, `ContentType = application/json`.
4. **Waits for the publisher confirm, for at most 5 seconds.** The channels are created with publisher confirmations and confirmation tracking enabled, so `BasicPublishAsync` does not complete until the broker has stored the message. The timeout matters because a blocked broker never refuses a publish; it just never answers.
5. **Returns the channel** to the pool. If the publish failed or timed out, the slot goes back empty, because that channel may still be waiting on a confirm.

Only then does the controller return `200 OK`.

If anything fails (broker down, channel error, unroutable message, timeout, open circuit), the publisher throws `MessagePublishException`, and the controller turns it into **`503 Service Unavailable`** with a `Retry-After` header, so the caller knows the wager was *not* accepted and should retry. The controller returns this itself rather than letting the exception reach ASP.NET Core's exception-handler middleware, which in .NET 8 logs every exception as an error with a stack trace; a request refused by an open circuit is logged only at debug level. `Retry-After` is the time left before the circuit lets a trial through, or 5 seconds when there is no open circuit.

At start-up, `RabbitMqPublisherWarmupService` opens the connection, declares the topology and fills all 16 channels, so the first burst of requests doesn't pay for that. If RabbitMQ is down at start-up, the API still starts and connects on first use.

**Code:** `src/OT.Assessment.Infrastructure/Messaging/RabbitMqCasinoWagerPublisher.cs`, `RabbitMqPublisherWarmupService.cs`, `src/OT.Assessment.App/ErrorHandling/MessageBusUnavailable.cs`

### Step 4: The Consumer receives deliveries into a buffer

`CasinoWagerConsumerWorker` is a `BackgroundService`. On start it:

1. Gets the shared connection and opens a channel.
2. Declares the topology (same as the API).
3. Sets **prefetch to 1,000** with `BasicQos`.
4. Starts consuming `ot.casino.wagers` with **`autoAck: false`**.

The RabbitMQ callback (`ReceivedAsync`) does almost nothing: it copies the delivery tag and body into an in-memory **bounded channel** (`System.Threading.Channels`, capacity 1,000) and returns. The body has to be copied because RabbitMQ reuses that buffer once the callback returns. Keeping the callback this small means the broker is never kept waiting by database work.

**Code:** `src/OT.Assessment.Consumer/CasinoWagerConsumerWorker.cs`, `Processing/InboundMessage.cs`

### Step 5: A batch is formed

A single loop calls `BatchReader.ReadBatchAsync`, which:

- waits for the first message, then
- keeps taking messages until it has **500**, or **200 ms** have passed since the first one arrived.

Under load batches fill to 500. When traffic is light, a lone wager is still written within 200 ms.

**Code:** `src/OT.Assessment.Consumer/Processing/BatchReader.cs`

### Step 6: The batch is validated and written in one call

`CasinoWagerBatchProcessor.ProcessAsync` has no RabbitMQ dependency (which is what makes it unit-testable). It:

1. **Deserialises and checks each message.** Anything that isn't valid JSON, or breaks a rule the database depends on (`CasinoWagerMessageRules`: empty ids, missing names, country code not two characters, negative amount, fewer than one bet), is marked for the dead-letter queue. The consumer doesn't assume every publisher validated properly.
2. **Writes the valid rest in one database call.** `SqlCasinoWagerWriter` turns the batch into a `DataTable` matching `dbo.CasinoWagerTableType` and passes it as a **table-valued parameter** (TVP) to `dbo.usp_CasinoWager_InsertBatch`. One round trip for up to 500 wagers.
3. **Retries transient errors** (deadlock, timeout, failover, connection lost) up to 5 times with exponential backoff and jitter, using Polly. `SqlTransientErrors` decides what counts as transient; when in doubt about the connection, it says "transient", so a database outage never dead-letters a good wager.
4. **Goes through a circuit breaker first.** The breaker wraps the retries. If batches keep failing even after their retries, it opens and refuses writes without touching SQL Server until its break has passed (see [§7](#circuit-breakers)).
5. **Isolates a bad row.** If SQL Server rejects the whole batch with a non-transient error, the processor writes each wager on its own. The one bad wager is dead-lettered and the other 499 are stored. An open circuit is explicitly *not* treated as a rejected row.

Inside `usp_CasinoWager_InsertBatch`, in one transaction:

1. De-duplicate within the batch on `WagerId`.
2. Drop wagers already in `dbo.CasinoWager` (this is what makes redelivery safe).
3. Insert any new `Provider`, `Game` (unique per provider + name) and `Player` rows, using `UPDLOCK, HOLDLOCK` so several consumers could run this concurrently.
4. Insert the wagers, capturing exactly which rows were written with `OUTPUT`. If a row failed to join, throw so the processor can isolate it.
5. Add the inserted amounts and counts to `Player.TotalAmountSpent` / `TotalWagerCount`.
6. Commit and return the number inserted.

Because the running totals are updated in the same transaction from the rows actually inserted, they cannot drift from the wager table, and duplicates are never counted twice.

**Code:** `src/OT.Assessment.Consumer/Processing/CasinoWagerBatchProcessor.cs`, `CasinoWagerMessageRules.cs`, `SqlTransientErrors.cs`, `src/OT.Assessment.Infrastructure/Persistence/SqlCasinoWagerWriter.cs`, `CasinoWagerTableType.cs`, `DatabaseGenerate.sql`

### Step 7: Each delivery is settled with the broker

`DeliverySettler.SettleAsync` uses the `BatchResult` from the processor:

- **Dead-lettered deliveries:** the original body is published to exchange `ot.casino.dlx` with a `dead-letter-reason` header (for example `malformed message`, `amount is negative`, `rejected by the database`). `RabbitMqDeadLetterPublisher` waits for the broker's confirm, and **only then** is the original delivery acked. If that publish fails, the original is not acked and will be redelivered, so a poison message is never lost.
- **Everything else:** acked one delivery tag at a time.

The worker then logs a line like:

```
Batch of 500: 497 stored, 3 already stored, 0 dead-lettered in 61 ms (6950 total)
```

`already stored` is the count of duplicates that the procedure skipped.

**Code:** `src/OT.Assessment.Consumer/Processing/DeliverySettler.cs`, `BatchResult.cs`, `src/OT.Assessment.Infrastructure/Messaging/RabbitMqDeadLetterPublisher.cs`

---

## 6. Reading the data back

| Endpoint | Procedure | How it stays fast |
|---|---|---|
| `GET api/player/{playerId}/casino?page=1&pageSize=10` | `usp_CasinoWager_GetByPlayer` | Returns two result sets: the total (read from `Player.TotalWagerCount`, no `COUNT(*)`) and the page. The covering index `IX_CasinoWager_PlayerId_CreatedDateTime` is already in "latest first" order, so no sort or key lookups. An unknown player returns an empty page, not a 404. |
| `GET api/player/topSpenders?count=10` | `usp_Player_GetTopSpenders` | An ordered scan of `IX_Player_TotalAmountSpent` on the running totals, instead of summing every wager. |

`READ_COMMITTED_SNAPSHOT` is switched on for the database, so these reads see the last committed data and are **never blocked** by a batch transaction that is in progress.

Validation: `page ≥ 1`, `1 ≤ pageSize ≤ 100`, `1 ≤ count ≤ 100`; anything else is a `400`.

---

## 7. What happens when things go wrong

| Situation | What the system does | Result |
|---|---|---|
| Invalid request body | API returns `400` with field errors | Nothing is published |
| RabbitMQ down when a wager is posted | Publish fails → `503` + `Retry-After: 5`. Once half of at least 20 publishes in 10 s have failed, the publish circuit opens for 15 s | While open, `503` comes back at once with `Retry-After` = time left on the break; no channels are tied up waiting |
| RabbitMQ blocking publishers (memory or disk alarm) | Publish circuit opens as soon as the broker says it is blocked; any publish already waiting gives up after 5 s and its channel is discarded in the background | `503` at once until the broker unblocks, or its connection is lost or recovered (for example after a restart), then publishing resumes |
| RabbitMQ restarts while the apps run | Automatic connection recovery; closed pool channels are replaced on next use | Durable queues and persistent messages survive the restart |
| API crashes after the confirm, before responding | The wager is already in the queue | Stored once; if the caller retries, the duplicate is skipped |
| Consumer is not running | Wagers accumulate in `ot.casino.wagers` | Drained when it starts (about 7,000 in ~2.5 s) |
| Consumer crashes mid-batch | Unacked deliveries return to the queue | Redelivered and stored; anything already committed is skipped |
| Same wager delivered twice | Procedure skips existing `WagerId`s | Stored and totalled once |
| Deadlock / timeout in SQL | Retried up to 5 times with backoff | Usually succeeds on retry |
| SQL Server down | Retries exhausted → the batch is **held**, unacknowledged, and tried again after 5 s. Two such failures open the SQL circuit for 30 s; the held batch is then the trial write | Nothing new is pulled, so the backlog waits in the queue; nothing is dead-lettered or requeued |
| Malformed or invalid message | Published to `ot.casino.wagers.dead` with a reason, then acked | Visible in the DLQ for inspection |
| One row rejected by the database | Batch retried one row at a time; that row is dead-lettered | The other rows are stored; the circuit is not affected |
| Dead-letter publish fails | Original is not acked | Redelivered later; never dropped |
| Batch fails for an unexpected reason (not a database outage) | Whole batch nacked with requeue; consumer pauses 5 s | Redelivered and tried again |
| Consumer loop throws unexpectedly | Logged; consumer restarts after 5 s | Unacked messages are redelivered |

### Circuit breakers

A circuit breaker sits in front of a dependency and tracks how calls to it are going. It has three states:

- **Closed:** calls go through as normal and failures are counted.
- **Open:** too many recent calls failed, so calls are refused immediately, without being attempted, for a *break duration*.
- **Half-open:** the break is over; the next call is let through as a trial. If it succeeds the circuit closes, and if it fails a new break starts.

Both breakers are Polly v8 circuit breakers, and both are singletons, so every request or batch in the process shares the same state.

| | API → RabbitMQ | Consumer → SQL Server |
|---|---|---|
| What counts as a failure | Any publish error or 5 s timeout. Caller cancellation does not count | Only transient SQL errors that outlasted the 5 retries. A rejected row does not count, or one poison wager could stop the pipeline |
| Opens when | Half of at least 20 publishes in 10 s fail, or the broker sends "connection blocked" | Both of at least 2 batches in 60 s fail |
| Break | 15 s (until "unblocked", or the connection being lost or recovered, when the broker blocked) | 30 s |
| While open | `503` at once, before a channel is rented, with `Retry-After` set to the time left | The batch is held unacknowledged and not retried until the break is over. Nothing new is read, and prefetch stops the broker delivering more. If the channel closes meanwhile, the hold ends at once and the deliveries are redelivered |
| Visible in | `/health/ready` reports `Degraded`; a warning in the API log when it opens | Consumer log: `SQL Server circuit Open: holding batch of 500 wagers unacknowledged; next attempt in 00:00:30` |

Two details that were easy to get wrong:

- **An open circuit is "try later", not "bad data".** The batch processor treats any non-transient error as a row the database rejected and dead-letters it. Polly's "circuit open" exception is not a SQL error, so without an explicit exclusion an outage would have dead-lettered every wager as `rejected by the database`.
- **Polly keeps reporting `Open` after the break is over,** until a call goes through. The API's early "is the circuit open?" check therefore asks whether the break is still running; checking the reported state would mean no trial call is ever made and the circuit never closes.

---

## 8. Configuration reference

Both processes read `appsettings.json`. Any value can be overridden with an environment variable using `__` for nesting, for example `RabbitMq__HostName`, `RabbitMq__PublishCircuitBreaker__BreakDurationSeconds` or `ConnectionStrings__DatabaseConnection`.

**`RabbitMq`** (API and Consumer; validated at start-up)

| Setting | Default | Meaning |
|---|---|---|
| `HostName`, `Port` | `localhost`, `5672` | Broker address |
| `UserName`, `Password`, `VirtualHost` | `guest`, `guest`, `/` | Credentials and vhost |
| `Exchange` | `ot.casino` | Where wagers are published |
| `RoutingKey` | `casino.wager` | Binding key for both queues |
| `Queue` | `ot.casino.wagers` | Main queue |
| `DeadLetterExchange` | `ot.casino.dlx` | Where poison messages go |
| `DeadLetterQueue` | `ot.casino.wagers.dead` | Where poison messages end up |
| `PublisherChannelPoolSize` | `16` | API only: concurrent confirmed publishes |
| `PublishTimeoutMilliseconds` | `5000` | API only: longest wait for one publish confirm |
| `PublishCircuitBreaker` | see below | API only: when to stop publishing and answer `503` at once |

**`Consumer`** (Consumer only; validated at start-up)

| Setting | Default | Meaning |
|---|---|---|
| `BatchSize` | `500` | Max wagers per stored-procedure call |
| `BatchWindowMilliseconds` | `200` | How long to wait for a batch to fill |
| `PrefetchCount` | `1000` | Unacked messages the broker may push; must be ≥ `BatchSize` and ≤ 65,535 |
| `MaxWriteRetries` | `5` | Retries for transient SQL errors |
| `RetryBaseDelayMilliseconds` | `200` | Base delay for exponential backoff |
| `RequeueDelayMilliseconds` | `5000` | Pause before trying again after a batch could not be stored (while the circuit is closed) |
| `CircuitBreaker` | see below | When to stop writing to SQL Server and hold the batch |

**Circuit breaker settings** (`RabbitMq:PublishCircuitBreaker` and `Consumer:CircuitBreaker`)

| Setting | API default | Consumer default | Meaning |
|---|---|---|---|
| `FailureRatio` | `0.5` | `1.0` | Share of calls in the window that must fail; greater than 0, at most 1 |
| `MinimumThroughput` | `20` | `2` | Calls needed in the window before the ratio counts; at least 2 |
| `SamplingDurationSeconds` | `10` | `60` | Length of the window |
| `BreakDurationSeconds` | `15` | `30` | How long the circuit stays open before a trial call |

**`ConnectionStrings:DatabaseConnection`** defaults to local SQL Server with Windows authentication and `TrustServerCertificate=True`.

---

## 9. Watching it work

**Management UI** (http://localhost:15672, `guest` / `guest`):

- **Queues › `ot.casino.wagers`**: *Ready* is waiting for the consumer; *Unacked* is with the consumer right now (up to the prefetch of 1,000). During a Tester run with the Consumer up, *Ready* should stay near zero.
- **Queues › `ot.casino.wagers.dead`**: should be empty. If not, open it, use *Get messages*, and read the `dead-letter-reason` header.
- **Connections / Channels**: the API shows one connection with its 16 publisher channels; the Consumer shows one connection.
- **Experiment:** stop the Consumer, run the Tester, and watch *Ready* climb to ~7,000. Start the Consumer and watch it drain in a couple of seconds.
- **Experiment: database outage.** Stop SQL Server during a Tester run. The Consumer logs the circuit opening, *Unacked* settles at about 1,000 and *Ready* climbs. The dead-letter queue stays empty. Start SQL Server again: within one break the held batch is written, the circuit closes and the backlog drains.
- **Experiment: broker outage.** Stop RabbitMQ (`docker compose stop`) and post a few wagers. The first ones take a moment to fail; once the circuit opens they come back `503` immediately with `Retry-After` counting down. Start RabbitMQ and, after the break, posts succeed again.

**Health:** `GET https://localhost:7120/health/ready` reports whether SQL Server and RabbitMQ are reachable, and reports `Degraded` while the publish circuit is not closed; `/health/live` only says the process is up.

**Consumer log:** one line per batch with stored / already stored / dead-lettered counts and timing, and a warning while a batch is held.

**SQL integrity checks after a run:**

```sql
USE OT_Assessment_DB;

-- Every wager stored once
SELECT COUNT(*) AS Wagers, COUNT(DISTINCT WagerId) AS DistinctWagers FROM dbo.CasinoWager;

-- Running totals match the wager table
SELECT (SELECT SUM(TotalAmountSpent) FROM dbo.Player) AS PlayerTotals,
       (SELECT SUM(Amount)           FROM dbo.CasinoWager) AS WagerTotal,
       (SELECT SUM(TotalWagerCount)  FROM dbo.Player) AS PlayerCounts,
       (SELECT COUNT(*)              FROM dbo.CasinoWager) AS WagerCount;
```

Expect slightly fewer than 7,000 rows after one Tester run. The Tester itself sends some wagers twice with the same `WagerId` (explained in [Notes.md §5](../Notes.md#5-things-i-found-in-the-boilerplate)); the system stores each one once.

---

## 10. Where to find things in the code

| Project | Folder / file | What's there |
|---|---|---|
| `OT.Assessment.Core` | `Messaging/CasinoWagerMessage.cs`, `MessageSerializer.cs` | The bus message and its JSON format |
| | `Abstractions/` | `ICasinoWagerPublisher`, `ICasinoWagerWriter`, `IPlayerReadRepository`, `IDeadLetterPublisher`, `MessagePublishException` (with `RetryAfter`) |
| | `Models/` | `PagedResult`, `PlayerWager`, `TopSpender` (GET response shapes) |
| `OT.Assessment.Infrastructure` | `Messaging/RabbitMqConnectionProvider.cs` | The single shared connection |
| | `Messaging/RabbitMqTopology.cs` | Exchanges, queues, bindings |
| | `Messaging/RabbitMqCasinoWagerPublisher.cs` | Pooled, confirmed publisher with timeout and circuit breaker |
| | `Messaging/RabbitMqDeadLetterPublisher.cs` | Confirmed DLQ publisher |
| | `Resilience/` | `CircuitBreakerSettings` and `CircuitBreakerTracker`, shared by both breakers |
| | `Persistence/` | Dapper writer (TVP) and read repository |
| | `HealthChecks/` | SQL Server and RabbitMQ readiness, publish circuit state |
| | `ServiceCollectionExtensions.cs` | DI registration used by both apps |
| `OT.Assessment.App` | `Controllers/PlayerController.cs` | The three endpoints |
| | `Contracts/` | Request model and validation |
| | `ErrorHandling/` | Bus failure → `503` with `Retry-After` (returned by the controller, logged once) |
| `OT.Assessment.Consumer` | `CasinoWagerConsumerWorker.cs` | Consume → buffer → batch → settle loop; holds a batch during a database outage |
| | `Processing/` | Batch reader, processor (retries and circuit breaker), rules, transient-error classification, settler |
| Root | `DatabaseGenerate.sql` | Tables, indexes, TVP type, stored procedures |
| | `docker-compose.yml` | RabbitMQ (and optional SQL Server) |
