# Performance at volume

What Phase 9's screens and its sync cost when a connection holds a great many tickets, measured,
and what the measuring found. Every figure here was read off a run; nothing is an estimate.
Production today holds about 150 tickets on two connections, so none of this is felt there yet.
It is what stands between that and a desk with a hundred PSA accounts.

## How it was measured

`tests/unit/ConnectionVolumeTests.cs`. Every database command is counted, and each measurement is
split into the time the database took to answer and the rest (the work done in memory).

| Test | Runs | What it holds |
|---|---|---|
| `What_an_administrator_opens_costs_the_same_number_of_queries_whatever_the_connection_holds` | Always (SQLite); PostgreSQL when asked | 1,000 and 20,000 tickets on one connection |
| `A_sync_does_the_same_work_for_each_ticket_however_many_it_reads` | Always (SQLite); PostgreSQL when asked | A first import of 500 and of 5,000 tickets, a run with nothing changed, and a full second read |
| `The_benchmark_at_ten_and_a_hundred_connections` | PostgreSQL only, and only when asked | 10 connections and 100,000 tickets; 100 connections, 500,000 tickets, 1,000,000 time entries, a year of sync runs |

```powershell
$env:DESK_TEST_POSTGRES = "Host=localhost;Port=15439;Username=desk;Password=..."
$env:DESK_TEST_VOLUME = "1"     # the benchmark; it takes about six minutes
dotnet test tests/unit --filter "FullyQualifiedName~ConnectionVolumeTests" --logger "console;verbosity=detailed"
```

On PostgreSQL each test makes a database of its own from the real migrations and drops it
afterwards. The benchmark loads its rows inside the database (one row copied half a million
times, with the columns that matter worked out from the copy's number), then runs
`VACUUM ANALYZE`, which is what autovacuum does to a table that has been there a while. One
connection holds half of all the tickets, as the largest customer of a desk does.

**The machine.** A developer PC, with PostgreSQL 17 in Docker under WSL2. A round trip to the
database costs about a millisecond there, which a server with the database beside it does not
pay, and the same measurement moved a great deal from one run to the next: the same import of
5,000 tickets took between 49 and 151 seconds over six runs. So where a figure was measured more
than once the table gives the lowest and the highest, the times are a guide, and the counts are
the proof. A count does not depend on the machine, and was the same on every run.

**What the PSA costs is not in here.** The PSA in these tests answers at once. A real one takes
its own time for every call and is paced ([provider-calls.md](provider-calls.md)), and a sync
asks it for each ticket's notes and time. That, not the portal, sets how long a first import
takes, and it has to be measured against the real thing: the owner's Autotask sandbox and
ConnectWise test environment.

## What the measuring found

Four things, all fixed here. The first three are held by an assertion that fails on the code as
it was; the fourth by the counts.

### 1. A sync kept every ticket it had read

A run saved each page and went on holding all of it. Every save looks through everything the
unit of work holds, so each ticket cost more than the one before. Nothing was wrong at the sizes
every other test uses.

| The same test: import 5,000 tickets, then read them all again | Before | After |
|---|---|---|
| PostgreSQL 17 (each includes about a minute building the database) | 9 min 1 s | 3 min 1 s |
| SQLite | 5 min 29 s | 24 s |

A run now lets go of a page once it is saved (`ConnectionSyncRunner`: one line, and the reload
it already had for a ticket that failed). What a run still holds when it ends is asserted: 3
rows, where it was 5,043.

### 2. "Apply to tickets already here" went client by client, board by board

A rule can be written for one client or one board, so tickets were rewritten a client and a
board at a time, for every value. With no such rule, which is the usual case, that is one
answer asked for hundreds of times.

| Applying three new rules to 10,712 of 20,000 tickets, 40 clients on 4 boards | Before | After |
|---|---|---|
| PostgreSQL 17 | 965 queries, 4.8 s | 49 queries, 2.4 to 2.8 s |
| SQLite (it writes a row at a time) | 11,197 queries, 30 s | 10,739 queries, 1.5 to 2.3 s |

It now takes one pass for each value, unless a rule of that field really is for one client or
one board; then that field alone goes place by place. A new test holds that second case
(`A_rule_written_for_one_board_is_applied_to_that_boards_tickets_and_no_others`). Each batch of
500 is also let go of once saved: a ticket carries its whole description.

### 3. The Connections page counted the tickets once for every connection

Each card shows how many tickets, customers and contacts the connection holds. Those were counts
inside the list's own query: a pass over the tickets for every connection in the list.

| The Connections list | Before | After |
|---|---|---|
| 10 connections, 100,000 tickets | 2 queries, 364 ms | 5 queries, 28 to 31 ms |
| 100 connections, 500,000 tickets | 2 queries, 14.4 s | 5 queries, 111 to 132 ms |

They are now counted for all connections at once: five queries whatever the number of
connections. Nothing tested these three counts before. The same test now holds them, including
that one account's customers and contacts are not counted on another's card, and it passes on
the old query too: the numbers on the cards have not changed.

### 4. The ticket list read every ticket three times

A page of the list comes with a total and two sums of hours. They were three queries, and so
three passes over every ticket the person may see. They are one, and a page is seven queries
where it was nine.

| A page of the ticket list | 100,000 tickets, before | after | 500,000 tickets, before | after |
|---|---|---|---|---|
| First page | 160 ms | 74 to 77 ms | 717 ms | 424 to 681 ms |
| Page 201 | 281 ms | 179 to 185 ms | 1,153 ms | 572 to 692 ms |
| One status | 70 ms | 29 ms | 679 ms | 235 to 296 ms |
| One connection | 166 ms | 54 to 60 ms | 709 ms | 230 to 263 ms |

"Before" is one run and "after" two, so the first page at half a million shows a saving in one
run and next to none in the other. The totals are held to the data: every ticket in the
benchmark carries an hour worked, half of it billable, and the page says so of all of them.

## What it costs now

### What an administrator opens

The number of queries is the same at 1,000 tickets and at 20,000, and is asserted. It is the
same on SQLite and on PostgreSQL for every read; a save is two more on SQLite, which writes a
row at a time. The times are each screen's first call in a new process, on PostgreSQL, over two
to five runs.

| | Queries | 20,000 tickets on the connection |
|---|---|---|
| Connections list | 5 | 248 to 300 ms |
| Mapping health | 10 | 265 to 337 ms |
| Sample of ten tickets | 3 | 21 to 40 ms |
| Mapping coverage (the wizard) | 3 | 5 to 7 ms |
| Preview before switching on | 5 | 10 to 17 ms |
| Checks before switching on | 6 | 19 to 25 ms |
| Saving three inbound rules | 7 (9 on SQLite) | 121 to 181 ms |
| Mapping health, opened again | 9 | 55 to 113 ms |

### A sync

Nine database commands for each ticket on a first import and seven when a ticket is read again
and found unchanged, at 500 tickets and at 5,000 alike. A run with nothing to read is 13
commands. All three are asserted.

| First import of 5,000 tickets, PostgreSQL | Runs | Time | A ticket |
|---|---|---|---|
| Into an empty database | 6 | 49 to 151 s | 10 to 30 ms |
| Into one holding 100,000 tickets | 4 | 38 to 101 s | 8 to 20 ms |
| Into one holding 500,000 tickets and 1,000,000 time entries | 3 | 55 to 69 s | 11 to 14 ms |

The work for a ticket does not grow with what is already there: a ticket is found by its
connection and its id in the PSA, which is indexed, and the spread between runs is wider than
the difference between an empty database and a full one. About a quarter of the time is not the
database (15 of 55 seconds in a typical run).

### The benchmark

Each read is made twice and the second is the one given, so the time is not the first call's.
Three runs at the larger size and four at the smaller, except the connections list and the
ticket list: those changed after the first runs, and have two.

| | 10 connections, 100,000 tickets | 100 connections, 500,000 tickets |
|---|---|---|
| Loaded in | 7 to 12 s | 36 to 59 s |
| Connections list (5 queries) | 28 to 31 ms | 111 to 132 ms |
| Which connections are due a sync, as the worker asks (1) | under 1 ms | 1 ms |
| Sync health, a year of runs behind it: 105,000 (4) | 2 to 8 ms | 4 to 5 ms |
| Mapping health of the connection holding half (9) | 90 to 138 ms | 464 to 714 ms |
| Mapping sample (3) | 31 to 55 ms | 118 to 193 ms |
| Ticket list, first page (7) | 74 to 77 ms | 424 to 681 ms |
| Sync of 200 tickets, half changed and half new (1,837) | 1.8 to 3.4 s | 3.0 to 3.6 s |

## What is still slow, and is not changed here

Three reads of the ticket list are a pass over every ticket, and take seconds at half a million.
They are older than Phase 9, they are instant at today's size, and each needs more than a line;
one needs a migration. They are measured here so that nobody finds them by waiting.

| | 100,000 tickets | 500,000 tickets | Why | What it needs |
|---|---|---|---|---|
| Dashboard summary (16 queries) | 0.44 to 0.56 s | 2.9 to 6.4 s | Sixteen counts, each over every open ticket | One pass with all the counts in it |
| Filter lists on the ticket page (13) | 0.44 to 0.79 s | 2.8 to 4.9 s | To list everyone who logged time, every time entry is joined to its ticket: 3 of the 4.9 seconds | The people found without that join, or kept for a minute |
| Search by text (7) | 1.4 to 2.4 s | 2.9 to 4.0 s | It reads the text of every ticket | A trigram index: a migration, and an extension PostgreSQL has had since 13 |

Also not changed:

- `GET /api/tickets` without a board returns every ticket. No screen calls it that way (the list
  uses `/api/tickets/page`), so it was not measured. It should take a page or go.
- Sync runs are kept for ever: 288 a day for each connection at the default five minutes. Reading
  them is indexed and took 4 to 5 ms with a year of them behind it, so this is a matter of disk
  and not of speed. There is no retention yet.
- Applying a mapping loads each ticket it rewrites, description and all, 500 at a time. That is
  bounded, and slower than one UPDATE would be. Nothing in the portal updates rows in bulk,
  because that passes by the checks every save makes (the tenant, the time stamp).
- The mapping sample sorts the connection's tickets by when they were last synced: 118 to 193 ms
  on 250,000. An index would make it instant; nobody waits on it today.
- A million time entries are in the benchmark's database for everything above. No time report
  was measured against them here; the workforce reports have their own figures in
  [../workforce-scheduling/analytics.md](../workforce-scheduling/analytics.md).
- A hundred connections were measured as rows in one database. A hundred syncing at once (the
  worker's own concurrency, and a hundred PSAs' rate limits) was not.
