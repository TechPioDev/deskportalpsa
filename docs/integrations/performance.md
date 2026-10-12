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
| Mapping health | 12 (10 before the classification rules were counted in it) | 265 to 337 ms; 264 ms with the two more |
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
| Mapping health of the connection holding half (9; 11 with the classification rules counted) | 90 to 138 ms; 135 ms with them | 464 to 714 ms; 525 ms with them |
| Mapping sample (3) | 31 to 55 ms | 118 to 193 ms |
| Ticket list, first page (7) | 74 to 77 ms | 424 to 681 ms |
| Sync of 200 tickets, half changed and half new (1,837; 1,838 since the classification rules are read) | 1.8 to 3.4 s | 3.0 to 3.6 s |

## The three reads of the ticket list that took seconds

The dashboard summary, the ticket page's filter lists and the search by text were left at 3 to 6
seconds on half a million tickets when the rest of this page was written. The owner asked for
them to be put right before Phase 9 closes, profiled first. This is that work
(`feat/psa-phase9-ticket-reads`).

### What the profile showed

Each of the three was a pass over every ticket, once for each thing it shows, and each pass was
the same plan: one process reading the whole table.

- **The table is wide.** 505,000 tickets are 434 MB, because a ticket carries its description;
  PostgreSQL here holds 128 MB of it in memory at a time (production is set the same). A pass
  over the tickets took 0.4 to 0.8 s whatever it was counting.
- **The summary made five such passes** (open, waiting, by priority, by source, high priority) and
  eleven smaller queries: 16 in all.
- **The filter lists made five**, one for each of status, priority, queue, connection and who holds
  a ticket, and then joined every one of a million time entries to its ticket to find who had logged
  time: 1.4 of the 2.6 seconds.
- **The search made two**, one to count and one to list. Looking for a few letters anywhere in a
  subject is "contains", which an ordinary index cannot answer. And one of its five conditions,
  the client's name, was asked of each ticket's own client, which no index on tickets can answer
  either: with that one in it, none of the others could use an index.
- No query was repeated for each row (no N+1), nothing was tracked that did not need to be, and
  each list already asked for a page and not for everything.

### What was changed

| | Before | After |
|---|---|---|
| Search | The client's name matched ticket by ticket | The clients whose name matches are found first and given as a list of ids; a trigram index (`IX_tickets_search`) answers "contains" on the number, the subject, the requester and the PSA's own number |
| Summary | A count for each figure, each a pass | One pass, grouped by source, priority and holder, every figure added up from the groups; read from an index of the open work (`IX_tickets_open`) that carries everything the counting needs |
| Filter lists: status, priority, queue | Three lists of distinct values, three passes | One list of the distinct combinations, one pass |
| Filter lists: connections | "Which connections have a visible ticket" | Each connection asked for one visible ticket of its own, from an index |
| Filter lists: people | Every ticket read for its holder, and every time entry joined to its ticket | Each kind of person found by walking an index from one value to the next, so the work is in proportion to the number of people and not of tickets. A time entry now carries the PSA account of its ticket, so that this needs no join |

The search finds what it found before. The same five conditions are sent, in the same words; only
where the client's name is looked up has moved. A search of one or two letters cannot use a
trigram index and is still a pass over the tickets, and searching inside the conversation (off
unless asked for) is unchanged.

The people are found by index only for someone who may see every PSA ticket, which is who the
benchmark measures (an administrator). For someone whose sight is narrowed to their own tickets,
"is this login on a ticket I can see" is a question about each ticket, so their tickets are read
as before: one pass now where it was three, and the people as before. Both ways are held to the
same answer by the same tests.

### Before and after

The data set is the benchmark's: 100 connections, 500,000 tickets, 1,000,000 time entries, one
connection holding half. Five tickets in seven are open, which is far more than a real desk and
is the worst case for the summary. "Before" is four runs and "after" is three; each read is made
twice and the second is the one given.

| 500,000 tickets | Queries before | after | Time before | Time after |
|---|---|---|---|---|
| Search by text | 7 | 8 | 2.4 to 4.0 s | 28 to 140 ms |
| Dashboard summary | 16 | 8 | 2.4 to 6.4 s | 352 to 621 ms |
| Filter lists | 13 | 17 | 2.6 to 4.9 s | 382 to 588 ms |
| Ticket list, one connection | 7 | 7 | 230 to 263 ms | 17 to 40 ms |

| 100,000 tickets | Time before | Time after (four or five runs) |
|---|---|---|
| Search by text | 1.4 to 2.4 s | 12 to 58 ms |
| Dashboard summary | 0.44 to 0.56 s | 65 to 475 ms |
| Filter lists | 0.44 to 0.79 s | 84 to 369 ms |

The highest "after" figure in each row of the smaller table is from one run made while the machine
was still busy with the larger one; the others are 12 to 15 ms, 65 to 97 ms and 84 to 115 ms.

The filter lists make more queries than they did (17 for 13) and take a fraction of the time: five of
the 17 walk an index and the rest are lookups by key. What is left of the summary and the filter
lists is one pass each: over the index of open work, and over the tickets for their distinct
statuses, priorities and queues. On a desk where most tickets are closed, the first is a pass over
a small index.

### The indexes added

Measured on the 500,000-ticket database. The tickets were 434 MB with 130 MB of indexes before.

| Index | On | Size | What it is for |
|---|---|---|---|
| `IX_tickets_search` (trigram, GIN) | tickets | 46 MB | Search by a few letters of the number, subject, requester or PSA number |
| `IX_tickets_open` (partial, covering) | tickets | 29 MB | The dashboard summary |
| organization, PSA account, PSA login | tickets | 4 MB | The PSA logins that hold tickets; one connection's tickets |
| organization, who resolved it (where anyone did) | tickets | 8 kB | The people here who resolved a ticket |
| organization, PSA account, PSA login (where nobody here is on it) | time entries | 14 MB | The PSA logins that logged time |

### What it costs

- **Space.** 79 MB more of indexes on the tickets (209 MB where there were 130), and 14 MB on the
  time entries.
- **Writes.** Every new or changed ticket has more indexes to keep. A first import of 5,000 tickets
  into the 500,000-ticket database took 44 to 70 s over three runs, against 55 to 69 s before the
  indexes: no difference that can be told from the noise. Rewriting the status of 10,712 tickets
  ("apply to tickets already here") took 2.7 to 3.5 s over three runs, against 2.4 to 2.8 s before (a
  fourth, made in the middle of the full benchmark, took 14.2 s).
- **The migration** (`TicketListReads`) adds one nullable column to the time entries and gives
  every entry already there its ticket's PSA account: 26 to 33 s for a million entries on this
  machine, in one statement, and the table is about twice its size afterwards until the space is
  used again (301 MB where it was 142). It builds its indexes with the table locked against
  writes for as long as that takes: seconds at this size, nothing at production's 151 tickets. On
  a table too large to hold still, each can be built beforehand with `CREATE INDEX CONCURRENTLY`
  under the same name; the search index is made `IF NOT EXISTS` for that reason.
- **`pg_trgm`.** The search index needs this extension. It ships with PostgreSQL, production's
  server has it available, and the database's owner may add it; the migration does.
- **Two things have to stay the same.** The index of open work is used only while its condition
  is word for word what "open" is sent to the database as, and the search index only while the
  search goes on sending `lower(...) LIKE`. Neither can be seen to break from the results, so the
  benchmark asks PostgreSQL for the plan of each and fails if the index is not in it.
- **Nothing is cached.** Every figure is read when it is asked for.

### What holds it

- `TicketListReadsTests` (15, every run, on a SQL translator and through the real visibility
  rule): what the summary counts, what the filter lists offer, what a search finds, that a ticket
  on a board the caller is not on is in none of them, that the two ways of finding people agree,
  and that neither ever offers another organization's.
- The benchmark, on PostgreSQL: the counts and the lists against what was loaded, the two plans,
  the five index walks, and the query counts (8, 8 and 17).

## Classification rules at volume

Slice 4e ([classification-mapping.md](classification-mapping.md)) added three things an
administrator can ask of a connection's tickets, and one grouped count to the mapping health
above. They were measured in the same benchmark, on the connection holding half the tickets,
which files them under 306 different classifications. **One run at each size**, so these are
figures and not ranges.

| | Queries | 50,001 tickets on the connection | 250,001 tickets on the connection |
|---|---|---|---|
| The Classification page: rules, what tickets are filed under, the figures | 4 | 60 ms | 255 ms |
| Preview of two rules, with ten of the tickets that would change | 7 | 170 ms | 642 ms |
| Apply: one ticket in three of the connection rewritten | 107 and 119 | 16,666 tickets in 1.2 s | 83,333 tickets in 5.7 s |
| Apply again, with nothing left to do | 5 | 105 ms | 0.98 s |

- The page, the preview and the mapping health each make **one grouped pass** over the
  connection's tickets, by what they are filed under and what they hold. It took about 0.2 s of
  the 0.25 s at the larger size. Nothing is stored for it, so it cannot be out of date; it is one
  more pass on a page an administrator opens, not on anything a technician waits for.
- **Apply reads once and writes by id.** It reads what each classified ticket is filed under and
  holds (not the ticket), and then sends one statement for every thousand tickets of a kind that
  change. The first version loaded each ticket to change three words on it, five hundred at a
  time, as "apply to tickets already here" does for a status: at the rate measured for that
  (10,712 tickets in 2.7 to 3.5 s) the larger case here would have been over twenty seconds, and
  it was changed before it was measured. The number of statements is asserted to be no more than
  a handful plus one for each thousand tickets changed and one for each classification.
- A ticket as it arrives costs the sync nothing more. The connection's rules are read once for a
  run: the benchmark's sync of 200 tickets is 1,838 queries where it was 1,837, in 1.6 and 2.2 s.
- Nothing was indexed for this. The grouped pass reads the connection's tickets by the index on
  its connection; an index on the three levels would cost every ticket write for the sake of a
  page opened now and then.

## Time entered in the PSA, kept as worklogs

A later slice ([worklogs.md](worklogs.md)) keeps each time entry a PSA holds as a row. It is done
in the pass that already read a ticket's time for its totals, so the PSA is asked nothing more:
one request for a ticket's time in a run, as before, and asserted.

Measured with a PSA whose every ticket carries two time entries, read twice. One run at each size
on each database.

| | 200 tickets | 2,000 tickets |
|---|---|---|
| First import, PostgreSQL | 12.2 commands a ticket, 11.8 ms | 12.1 commands a ticket, 16.2 ms |
| Read again, nothing new, PostgreSQL | 10.1 commands a ticket, 7.6 ms | 10.1 commands a ticket, 6.6 ms |
| First import, SQLite (a row at a time) | 14.2 commands a ticket | 14.1 commands a ticket |
| Read again, SQLite | 10.1 commands a ticket | 10.1 commands a ticket |

The work for a ticket does not grow with the number of tickets, and is asserted (no more than 15
and 11 commands a ticket). Of those commands, keeping the entries is one read of the ticket's
worklogs and one write of the new rows; the rest is what a sync of a ticket with time already did.
A second read adds no row: 4,000 worklogs after two reads of 2,000 tickets, and none sent back.

The workforce screens each make one more query than they did (the people's PSA logins), and one
more again where any of them has a login (the time under it). The counts are the same at 50, 100
and 500 people and are asserted: a person's day and the team's day 28 where they were 27, the
overview 28 for 27, the forecast 55 for 53 (it reads two periods).

## What is still slow, and is not changed here

Also not changed:

- `GET /api/tickets` without a board returns every ticket. No screen calls it that way (the list
  uses `/api/tickets/page`), so it was not measured. It should take a page or go.
- Sync runs are kept for ever: 288 a day for each connection at the default five minutes. Reading
  them is indexed and took 4 to 5 ms with a year of them behind it, so this is a matter of disk
  and not of speed. There is no retention yet.
- Sync events are kept for ever too, one for each ticket change a sync brings in (R9 in the
  [audit](PHASE9_PSA_INTEGRATION_ARCHITECTURE_AUDIT.md)). The benchmark's database held only the
  few thousand its own syncs wrote, so a table of millions of them was not measured. Each is
  looked up by its connection and key, which is indexed.
- **Someone who sees only their own tickets** still has the filter lists found by a pass over the
  tickets: one now, where it was three, with the people found as before. It was not measured; the
  benchmark is an administrator's.
- **A search of one or two letters, and a search inside the conversation,** read every ticket (and
  every note) as before.
- **Deep pages of the ticket list.** Page 201 took 403 to 807 ms at half a million: the database
  counts past ten thousand tickets to reach it. Nobody pages that far by hand; an export should
  not go through it.
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
