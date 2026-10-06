# How the portal calls a PSA

Every HTTP call a connector makes goes through one layer that neither connector has to think about.
Built in Phase 9 (slice 2b); the reasons are findings D4, D8 and R1 in
[PHASE9_PSA_INTEGRATION_ARCHITECTURE_AUDIT.md](PHASE9_PSA_INTEGRATION_ARCHITECTURE_AUDIT.md).
What the sync does with the answers is in [sync-engine.md](sync-engine.md).

```
connector (Autotask, ConnectWise, a later one)
   │  HttpClient for THIS connection          ProviderHttpClients.For(name, connectionId, …)
   ▼
ProviderCallHandler        paces the connection, bounds each attempt, repeats what is safe to repeat
   ▼
EgressGuard                refuses a private or reserved address, with a readable message
   ▼
pinned transport           connects to the address it checked; follows no redirect
   ▼
the PSA
```

The lower two are shared and pooled per provider. The top one is the connection's own: one
connection's budget is never spent by another.

## Pace

Each connection has a budget of requests: a burst of two minutes' allowance is free, and beyond it
calls are let through at the sustained rate. An ordinary sync never notices. A large import paces
itself instead of running into the provider's limit and being told to stop.

| Provider | Sustained rate |
|---|---|
| Autotask | 120 a minute |
| ConnectWise | 120 a minute |

These are the connectors' own figures (`RequestsPerMinute`). Whatever limit the PSA actually
enforces still arrives as a 429 and is obeyed. The budget is per process: the API and the worker
each keep one, and the worker makes nearly all the calls.

`psa_connections.RateLimitPerMinute`, `MaxRetries` and `RetryBaseDelaySeconds` are still not read.
They were defaults nobody chose (60, 5, 2 on every row), and applying 60 a minute would have made a
full re-sync several times slower for no reason anyone had given. A per-connection override comes
with the connection settings screen.

## One attempt, bounded

An attempt may take 60 seconds. A PSA that did not answer used to hold the caller for 100. A
timeout reaches the connector as a cancellation the caller did not ask for, which is how both
connectors already recognise one.

## What is tried again

Up to three attempts in all.

| What happened | A read (GET, or a POST the connector marks as a read) | A write |
|---|---|---|
| 429 | Waited out and repeated | Waited out and repeated: a 429 was not processed |
| Timeout, connection failure, 500, 502, 503, 504 | Repeated after half a second, then one second, give or take a fifth | **Never repeated** |
| 400, 401, 403, 404 and the rest | Returned once | Returned once |

A write is not repeated on a fault because it may have been carried out before the answer was lost,
and because Autotask answers a write it rejects with a 500. Sent again, either is a second ticket,
note or time entry.

Autotask asks its questions with POST (`…/query`). The connector marks those as reads
(`request.AsRead()`), so they are repeated like a GET; nothing else sent with POST is.

**Retry-After** is read in both its forms, a number of seconds or a time. Only the first used to
be. If the PSA asks for longer than 20 seconds this layer does not sit and wait: the 429 goes back
as it is, with its Retry-After, and the sync - which can afford to - returns to that record when
the time has passed. A person may be waiting on the call.

## Lists, every page

Every list a connector reads is now read to its end. Before, only tickets were paged: companies,
contacts, technicians, a ticket's notes, its time entries and its files were each their first page
(500 in Autotask, 1,000 or 100 in ConnectWise), and nothing said when there was more.

A list too long to finish (50,000 records) is an **error, not a shorter list**. A caller handed
part of a ticket's notes as though it were all of them deletes the ones it was not shown.

## A time entry is not sent twice

A push whose answer was lost may have been carried out. The portal marks it failed; a retry used to
post it again, and the customer was billed the hour twice.

Now a retry asks the PSA first (`FindTimeEntryAsync`): is there an entry on this ticket, created
since the portal first tried, not already linked to another portal hour, that is exactly what this
request would have created? Only the connector can answer, because it knows what it sends: which
technician stands in when none is named, what it writes for empty notes, how it dates an entry.

| The PSA says | The retry |
|---|---|
| It is there | Linked to it, marked synced, audited `ticket.time.reconciled`. Nothing is sent |
| It is not there | Sent |
| Cannot be asked | **Not sent.** The entry stays failed and says why. A duplicate on an invoice is worse than an hour that waits |

A first push is sent without asking: nothing has been tried, so there is nothing it could already
be. A connector that has not implemented the question answers "cannot tell" and is retried as
before.

## What is logged

One line when a call is repeated, fails, or is answered with an error: the provider, the
connection id, the method, the **path**, the attempt, the status. Never a header (every credential
a connector has travels as one), never a query string (a filter can hold a customer's name), never
a body.

## For a new connector

- Take your HttpClient from `ProviderHttpClients.For(name, connectionId, baseUrl, requestsPerMinute)`
  in the factory. Do not build one.
- Register the provider's named client with the egress guard (`DependencyInjection`).
- Mark a POST that only reads with `request.AsRead()`.
- Map a 429 to `RateLimited` with `ProviderRequest.Wait(response.Headers.RetryAfter, now)`.
- Page every list to its end, and throw when it cannot be finished.
- Implement `FindTimeEntryAsync` if the provider takes time entries.
