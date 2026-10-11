# The inbound sync engine

How a PSA connection's tickets, notes, time totals and files get from the PSA into the portal, and
how the sync keeps its place, shares its connection and deals with what it could not read.
Built in Phase 9 (slice 2); the reasons are in
[PHASE9_PSA_INTEGRATION_ARCHITECTURE_AUDIT.md](PHASE9_PSA_INTEGRATION_ARCHITECTURE_AUDIT.md),
findings D1, D2, D5, D6, D7, D10 and D11. Which side owns which field is in
[PHASE9_SYNC_OWNERSHIP_MATRIX.md](PHASE9_SYNC_OWNERSHIP_MATRIX.md).

Nothing here is specific to a provider. A connector answers "tickets changed since X, page N";
everything below is the same for Autotask, ConnectWise and any connector added later.

## The four rules

| Rule | What it replaced |
|---|---|
| **One run per connection at a time** | The scheduled sync and "Sync now" could both work one connection, each importing the same notes and files |
| **The cursor moves to when the read started, and only when the whole read has finished** | The cursor was the moment a run ended, so a ticket changed while the run was reading was never asked for again |
| **A read that runs out of pages is continued, not abandoned** | A run stopped at 50 pages, logged a warning and moved the cursor anyway: an import over 5,000 tickets never completed |
| **A record that cannot be read or applied costs that record, and is never silent** | One ticket that could not be saved stopped every run; a rate limit on a ticket's notes was swallowed and the run reported healthy |

## What is stored

| Table | One row per | Holds |
|---|---|---|
| `sync_cursors` | connection and kind of data (`tickets`) | The watermark, and the position of a read still in progress |
| `sync_runs` | run | When, how long, who asked, how many, how it ended. While `Running` it is the connection's lock |
| `sync_failures` | record and operation that failed | What failed, why, how often, when it is next tried |

`psa_connections.LastSuccessfulSyncAt` is still written, when a read completes. It is what the
screens show as "last synced", and it is what the previous version of the code used as its cursor,
so a rollback finds it where it expects it.

## A run, step by step

Which connections the scheduler runs at all (not one in setup, paused, archived, or whose
credentials the PSA has rejected) is in [connections.md](connections.md).

1. **Take the lock.** A `sync_runs` row with status `Running` is written. A unique index allows one
   per connection, so a second run cannot be recorded and does not start: the scheduler skips the
   connection for this cycle, and "Sync now" answers *409, a sync is already running*. A run whose
   lease (10 minutes, pushed forward as it works) has lapsed is taken to have died with its
   process; the next run marks it `Abandoned` and takes over.
2. **Decide what to read.**
   - A read that ran out of pages last time is carried on from its saved position, with the filter
     it had.
   - A full run ("Re-sync all") starts from nothing and replaces any read in progress.
   - Otherwise: everything changed since the watermark. A connection with no watermark yet starts
     an hour before its old `LastSuccessfulSyncAt`.
3. **Retry what is owed.** Failed records whose time has come are read fresh from the PSA and put
   through the same path as any other ticket (up to 50 a run).
4. **Read pages** (100 tickets a page, up to 50 pages a run). After each page the position and the
   run's counts are saved and the lease is extended, so a process that stops mid-run loses at most
   one page of work, and that page is simply read again. What the page read is then let go of: a
   run that held every ticket it had read took longer for each one
   ([performance.md](performance.md)).
5. **For each ticket:** save it, then read its notes, its assignee's name and its time. A failure
   in any of those is recorded against that ticket and the run moves on.
6. **Attachments.** A provider with a tenant-wide sweep is swept once the ticket read has finished;
   a provider without one has the tickets this run touched read one by one.
7. **Finish.** If the read is complete the watermark becomes *the start of the read's first run,
   less two minutes*, and the saved position is cleared. If there is more, the run ends `Partial`
   and the next one continues. Either way the connection is `Healthy`: having more to read is not a
   fault.

Reading a ticket twice is free: an unchanged ticket is a no-op (its update hash matches). That is
what makes the overlap, the re-read page and the restarted read safe.

## When something fails

| What failed | What happens |
|---|---|
| Saving one ticket | Recorded (`apply`). The unit of work is cleared first, because it may be holding the refused change. The run continues |
| Reading one ticket's notes, time or files, for a passing reason (rate limit, timeout, provider error) | Recorded against that ticket and operation; retried after 5 minutes, doubling to 6 hours, never sooner than a `Retry-After` the PSA sent |
| The same passing failure 5 times in a row | The PSA is not answering. The run stops where it is, ends `Partial` with a notice, and carries on from the same page next time |
| A refusal (no permission, a request the PSA rejects) | Recorded as `NeedsReview`: it is the same refusal next time, so it waits for a person. After 5 in a row that kind of read is switched off for the rest of the run and the run says so once, in place of a failure against every ticket |
| Rejected credentials | The run fails and the connection is `Degraded` with the reason. No ticket would fare better |
| A file that could not be downloaded | Recorded (`attachments`); the ticket's files are read again when the failure is retried. A dated sweep would not have offered the file again |
| A saved position the PSA no longer honours | The read starts again from its first page with the same filter. Nothing applied is lost, only re-read |
| The run itself (connector cannot be built, the PSA is down on page one) | Run `Failed`, connection `Degraded`, both recorded on a clean unit of work and regardless of cancellation |
| The process stops | The run is `Abandoned`, the connection is not marked degraded, and the next process continues |

After 6 attempts a failure becomes `NeedsReview` and is no longer retried by itself. A ticket that
has since gone from the PSA resolves its own failures: there is nothing left to read.

A failure's message is the PSA's own answer where there is one. Anything else is reduced to its
kind ("The record could not be saved."): an unexpected exception's text is written for a developer
and can quote a query. No credential, header or payload is ever stored.

## What an administrator can see and do

All four need `connections.manage`, and all are under the caller's own tenant: another
organization's connection, run or failure is "not found".

| Route | For |
|---|---|
| `GET api/admin/connections/{id}/sync-state` | The watermark, whether a read is in progress and how far, whether a run is in progress, open failures, the last runs |
| `GET api/admin/connections/{id}/sync-failures` | What is still owed |
| `POST …/sync-failures/{failureId}/retry` | Put one at the front of the next run (one more try, not six). Audited `sync.failure.retried` |
| `POST …/sync-failures/{failureId}/dismiss` | Stop trying one. It is kept, marked dismissed. Audited `sync.failure.dismissed` |

"Sync now" is audited as `connection.sync.requested`, records who asked on the run, and reports
"more to read" and "could not be read" in its answer.

Since slice 3a these are on the connection's card as **Sync activity**: how far the sync has read,
its last runs with who started each, and each failed record with *Try on next sync* and
*Stop trying*.

## One person per PSA login

A link between a person and a PSA login says *this login is this person*: tickets assigned to it
are theirs to see and work done under it is credited to them. The database now allows one person
per login on a connection, and saving a second link says who already holds it. The same id on
another connection is another login.

## Settings

`SyncOptions` (code defaults; a test shrinks every number):

| Setting | Default |
|---|---|
| Page size | 100 |
| Pages per run | 50 |
| Overlap behind a run's start | 2 minutes |
| Overlap for a connection's first run under cursors | 1 hour |
| Lease | 10 minutes |
| Earlier failures retried per run | 50 |
| Failures in a row before a run stops asking | 5 |

## What this does not do yet

- The HTTP calls themselves are paced, bounded and repeated one layer down, and every list is read
  to its end: see [provider-calls.md](provider-calls.md). What reaches a run as a failure is what
  that layer could not get through.
- A screen for runs and failures: the routes are here; the screen comes with the connections work.
- Old runs are not removed. A connection records 288 a day at the default five minutes. Reading
  them is indexed and does not slow down ([performance.md](performance.md)); it is disk, and it
  wants a retention period.
- Notes and attachments have no unique index on the PSA's id. A reply written in the portal and a
  sync reading the same ticket can legitimately race, and an index would turn a rare duplicate into
  a failed reply the PSA had already accepted. The lock closes the case that mattered, two syncs.
- `app_users.ExternalTechnicianId` is no longer read by anything but is not dropped in this
  release: the previous version of the code still selects it, and a deploy runs both for a moment.
