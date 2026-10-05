# Workforce analytics (Phase 7)

How work is distributed, planned, executed and completed across the desk, as facts over a period:
who is handling work, how much was planned, how much was recorded, how much capacity there was and
how much of it was used, how much was reactive, what was completed, where demand comes from (sources,
clients), where estimates differed from actual work, where capacity is over- or under-allocated, and
how all of that changes over time. The management dashboard, a person's detail, My analytics for
the person themselves, drill-downs behind every figure, and a CSV export.

**Every figure is defined once**, in [PHASE7_ANALYTICS_METRIC_SPEC.md](PHASE7_ANALYTICS_METRIC_SPEC.md):
name, formula, source, inclusion and exclusion rules, zone behaviour, permission boundary, edge
cases and an example. This page is the architecture, the screens, the API, the security model and
the measured cost. Nothing here redefines a metric.

**What it is not.** Attendance, payroll, presence, keystroke or screenshot monitoring, surveillance,
or an automated score. Nothing is ranked, nobody is labelled best or worst, a ratio with nothing to
divide by is N/A, and every screen says what the numbers do and do not cover.

## Overview

```
                         one request, one load, one filter context
  ┌───────────────────────────────────────────────────────────────────────────┐
  │ people in scope (schedule.view) → narrowed by team / department / person  │
  │ capacity per person-day     WorkforceCalendar + CapacityCalculator (Phase 2)│
  │ planned per person-day      work_allocations (Planned ≠ Tentative; Cancelled out)│
  │ actual per person-ticket-day ticket_time_entries + live work_sessions (Phase 6)│
  │ completed in the period     tickets finished (ResolvedAt ?? ClosedAt), credited once│
  │ open work right now         open tickets held by the people in scope       │
  │ the tickets behind it all   what they are, who may open them               │
  └───────────────────────────────────────────────────────────────────────────┘
                                         │ tallied in memory, once
       ┌───────────────┬─────────────────┼────────────────┬────────────────┐
     cards         technicians        teams / clients /    daily trend     heatmap
   (totals)     (sortable table)    sources / priorities   (per date)    (person × day)
                                     / work types
       └───────────────┴─────────────────┴────────────────┴────────────────┘
                          every card opens the SAME rows as a paged list
                          every table exports as CSV (its own permission)
```

The dashboard reads the operational tables; it keeps no table of its own and caches nothing. A
card and the list it opens are the same loaded rows counted twice, so they cannot disagree, and the
rows of any breakdown add up to the cards because they are those rows grouped.

## Screens

- **Workforce analytics** (`/dashboard/workforce/analytics`, in the Workforce sub-navigation for
  anyone whose `schedule.view` reaches more than themselves): the filter bar (period, team,
  department, technician, client, source, priority, kind of work; the lists hold only what the
  viewer may see; filters live in the query string so a view is bookmarkable), the period line with
  the zone, the people count, "as of" and each PSA connection's newest sync; eight cards (capacity,
  planned, actual, scheduled utilization, capacity utilization, completed, reactive, open work now)
  with a tooltip saying what each is; capacity against demand; client / internal / monitoring work
  and marked-billable time; planned against actual (variance, estimate variance, planned actual,
  reactive actual); the daily trend; planned, planned actual and reactive by technician; the
  Technicians table (sortable by any column, name order by default, never pre-ranked); Teams;
  Clients, Work sources, Priorities, Work types; the capacity heatmap (the metric is chosen and
  named: actual capacity utilization % or scheduled capacity %); and "What these numbers do and do
  not cover". A card opens a drill-down dialog listing its records with the whole set's total.
- **Technician work analytics** (`/dashboard/workforce/analytics/{appUserId}`): one person over the
  period: twelve figures, the daily trend, the work items in the period (each ticket once, with
  planned against actual, filterable to finished / open / reactive), by client, by source, by work
  type, by priority. Opened from a name in the table, the heatmap or a drill-down row.
- **My analytics** (`/dashboard/workforce/my-analytics`, for everyone with `schedule.view`): the
  same detail for the signed-in person. No technician filter, no peer, no comparison.
- **Phones**: the cards stack, the technician and item tables become cards, the other tables scroll
  inside their own container; the page never scrolls sideways.
- Charts are inline SVG (no dependency was added; the project had none and the Team scheduler set
  the precedent). Each chart has a `role="img"` label, a title on every bar, a legend, and a
  **Show as table** alternative with the same numbers. The heatmap *is* a table: every cell carries
  the three facts (actual or planned, capacity, the percentage) in its title and the row and column
  headers name the person and the day. Colour is a single hue by intensity and never the only
  carrier of meaning; a day with no capacity is "—", not 0 %.

## API

All under `api/workforce` (`WorkforceAnalyticsController`), staff only, behind the Workforce switch
(404 when off), reads only (every action is a GET, so they stay usable while an administrator views
the portal as someone). Class-level `schedule.view`; the people in every answer are the ones the
caller's scope reaches.

| Route | Permission | Answer |
|---|---|---|
| `GET analytics/filters` | schedule.view | The filter lists the caller may use (`AnalyticsFilterOptionsDto`): teams and departments with someone visible in them, visible people, clients and priorities of tickets the caller may open, sources (each PSA connection, "Team boards", "Monitoring"), `seesOthers`, `canExport`, the organization zone |
| `GET analytics/overview?period&from&to&teamId&departmentId&appUserId&clientId&source&priority&kind` | schedule.view | `AnalyticsOverviewDto`: the period, `generatedAt`, totals, open work now, capacity against demand, people, teams, clients, sources, priorities, work types, daily, heatmap, sync freshness, notes |
| `GET analytics/people/{id}?…` | schedule.view | `TechnicianAnalyticsDto`: one person (in scope, else "Person was not found."): figures, breakdowns, daily, the work items (≤ 200, `itemsTruncated`) |
| `GET analytics/work?list=…&skip&take&…` | schedule.view | `AnalyticsWorkPageDto`: the records behind a card, paged (50, at most 200), with the WHOLE set's `total`, `totalSeconds`, `totalMinutes`. `list` is actual, planned-actual, reactive, planned, tentative, completed, open, unscheduled or overdue (`kind` is the planned / reactive filter, so the two cannot share a name) |
| `GET analytics/export?report=…&…` | **workforce.analytics.export** | A CSV (`technicians`, `teams`, `clients`, `sources`, `daily`, `work`) under the same filters; audited |

Query parameters: `period` is `today`, `yesterday`, `this-week`, `last-week`, `this-month`,
`last-month`, `7d`, `30d` or `custom` (then `from` and `to`, at most 366 days, within a year of
today; the default is this week). `source` is `psa:{connectionId}`, `client`, `internal` or
`monitoring`. `kind` is `all`, `planned` or `reactive` (narrows the recorded time only; planned
minutes, capacity and variance are unchanged). A filter
value that is not a valid id is refused; a client or connection id that is not this organization's
is "not found"; a team or department the caller's people are not in yields nobody.

## Where each figure comes from

The source-of-truth mapping is in the specification ([§ Source audit](PHASE7_ANALYTICS_METRIC_SPEC.md#source-of-truth-audit)).
In code (`WorkforceAnalyticsService`, `packages/infrastructure/Workforce`):

| Step | Query | Notes |
|---|---|---|
| People | `WorkforceAccess.VisibleStaffAsync` + `Narrow` | active staff, name order, at most 1,000 |
| Teams of those people | one join | a person may be in several |
| Capacity | `WorkforceCalendar.LoadAsync` (versions, exceptions, holidays) + `CapacityCalculator.ForDay` per person-day | the same calendar and calculator as My capacity and the scheduler; allocations are not read here (the dashboard takes planned minutes from the allocations themselves) |
| Allocations | one query over a UTC window a day wider than the period | placed on the person-day of their start: the date in the person's zone, or the shift's date when the moment is inside a night shift that began the day before; Cancelled left out |
| Time entries | one query, every sync status, portal author only | placed on the person-day of `EntryDate` |
| Live clocks | one query (Active / Paused, no entry yet, with segments) | the day they started; seconds by the server's clock |
| Completed | one query: finished status, `ResolvedAt ?? ClosedAt` inside the period's UTC bounds in the organization zone, credit in scope | credit = `ResolvedByAppUserId ?? AssignedAppUserId ?? linked PSA login`; the link is part of the query (`UserPsaIdentities`), so a desk's PSA-side closures under unlinked logins are never loaded |
| Open work now | one query (only the overview and the open / unscheduled / overdue lists) | held by a person in scope; unscheduled = no Planned / Tentative allocation ending after now; the due figures leave out tickets whose SLA clock is paused |
| Tickets | one query per 2,000 ids, plus visibility through `ITicketScopeQuery` | reference, title, client, source, priority, origin, status, due date; invisible tickets are "Work you cannot open" |

Then `Tallies.Over(facts)` makes one pass: capacity and planned per person-day into the total, the
person, their teams, the day and the heatmap cell; actual per person-ticket-day, classified planned
or reactive by whether that person had a Planned or Tentative allocation on that ticket starting that
day (the My day rule), into the same tallies plus the client, source and priority rows; work types
from the entries; completed once per ticket into the credited person's rows. `ToDto()` derives the
ratios at the end, once.

## Periods and time zones

A period is whole calendar dates in the **organization's zone** (`MspOrganization.TimeZone`, the
same setting staff reports use), resolved on the server from the server's today, so two people
asking for "this week" get the same Monday–Sunday. Inside the period a person's day is their **shift
date** in their own schedule's zone, as capacity counts it: the calendar date, except that a moment
inside a night shift that began the day before belongs to that shift (so its planned and recorded
time is compared with the shift it was worked in, never with a day off). For anyone who does not work
across midnight that is the date My plan and My day show; for a night shift My day lists work after
midnight under the calendar date, and the totals over a period agree. Completed work is dated in the
organization zone (a completion is an organization event). A clock change inside the period changes
nothing: dates are dates, and each day's instants are converted for that day. A running period is
not cut at now: its later days hold capacity and no recorded time, and the screen says so; variance
and estimate variance compare the recorded time only with what was planned up to today.

The productivity dashboard (`api/dashboard`) buckets hours by the UTC date and counts only synced
entries; the workforce dashboard uses the person's day and every portal entry. Both are stated in
the specification, and neither was changed.

## Permissions, client isolation, tenant isolation

- **Reading** follows `schedule.view` and its scope, the rule the whole module uses: Own is one's
  own figures (My analytics), Team / Department is the people one shares a group with, All is
  everyone. No new key for reading: nothing the dashboard shows about a person is anything the
  caller could not already read for that person through capacity, the plan or My day.
- **Exporting** needs **`workforce.analytics.export`** (new; Manager and Administrator by default,
  Technician none): named people's figures leave the system in a file, which is a separate right
  from reading a screen. The controller requires the claim and the service checks it again; every
  export is audited as `workforce.analytics.exported` with the report, the period, the filters, the
  row count and the people count. A refused export is not audited (it did not happen).
- **Clients never reach any of it.** No client role or client login holds `schedule.view`
  (`No_client_role_or_client_login_holds_a_workforce_permission`); the client walk
  (`No_client_account_can_reach_any_workforce_endpoint`) covers `WorkforceAnalyticsController` and
  asserts it is among the controllers checked; a sign-in that is not a staff account is refused by
  the controller before any action; the client-shape guard now also forbids `Heatmap`, `Reactive`
  and `Analytics` in any ticket, control-panel, knowledge or attachment DTO.
- **Scope cannot be widened by a filter.** A technician id outside the scope is "Person was not
  found."; a team or department id the caller's people are not in yields nobody and zero figures
  (never an error that confirms the group exists); a client or PSA connection id is checked against
  the organization and is "not found" otherwise; a source or period that is not one of the known
  values is refused. The filter lists are built from the same scope, so a technician is offered
  themselves and the clients of tickets they may open, nobody else's.
- **Tenant isolation.** Every table is a tenant entity with the global filter; staff accounts, which
  carry no filter, are narrowed by organization explicitly in `WorkforceAccess`; another
  organization's person, client or connection is "not found"; its allocations, entries, clocks and
  tickets never load. `Breakdowns_add_up_to_the_totals_filters_combine_and_nothing_leaks_across_scope_or_organization`
  proves each of these with a second organization and a second database context.
- **A ticket the caller cannot open gives nothing of its own away.** Its time still counts (it is
  the person's time), but its reference, title, client, priority, status, due date and completion
  are not returned; in the client, source and priority tables it is one row, "Work you cannot open",
  and a list row says only the kind of provider, as My day does. A filter on what a ticket says
  about itself (client, PSA connection, priority) matches only tickets the caller may open, so
  filtering by a client can never show that a hidden ticket is theirs. Notes and descriptions are
  never in an answer or an export.
- **Nothing is cached**, so there is no cache to leak between tenants or scopes.

## Export

Server-side CSV (`text/csv`, UTF-8 with a byte-order mark so a spreadsheet reads names correctly),
through the same `TechnicianReportRenderer.Cell` the staff reports use: RFC-4180 quoting, and a
leading `=`, `+`, `-` or `@` neutralised with an apostrophe so a title like
`=HYPERLINK("http://evil")` opens as text (tested). Header lines carry the report, the period, the
zone, the dates, the generation time and the sentence "Facts about planned and recorded work; not a
performance score." Hours have two decimals; ratios say `N/A` when undefined. The recorded-work
report holds at most 50,000 rows ("Narrow the period or the filters."). File names:
`workforce-analytics-{report}-{from}-{to}.csv`. Excel is not produced.

## Performance

`CapacityPerformanceTests.Workforce_analytics_cost_the_same_number_of_queries_whatever_the_number_of_people_days_or_hours`
seeds 50 / 100 / 500 technicians with 1 / 2 / 3 allocations and 1 / 2 / 3 time entries of 45 minutes
every weekday for four weeks (1,000 / 4,000 / 30,000 allocations and as many entries), through a real
SQL translator with every command counted, and pins the counts. Measured (SQLite, a development
laptop; the counts are what matter):

| Read | Queries | 50 people | 100 | 500 |
|---|---|---|---|---|
| Overview, one week | 27 | 30 ms | 311 ms (first call, cold) | 449 ms |
| Overview, four weeks | 27 | 70 ms | 151 ms | 710 ms |
| Overview, one team, one week | 27 | 10 ms | 35 ms | 110 ms |
| One person, four weeks | 26 | 5 ms | 18 ms | 51 ms |
| Drill-down (recorded work), one week | 26 | 41 ms (250 rows) | 137 ms (1,000) | 416 ms (7,500) |
| Export, technicians, four weeks | 29 | 46 ms | 238 ms | 596 ms |
| Filter lists | 18 | 2 ms | 30 ms | 6 ms |

Constant whatever the number of people, days, allocations and hours: one load, then memory. Two
things do add queries, and both are bounded: the tickets behind the figures are read 2,000 at a time
(two queries per 2,000 distinct tickets), and a drill-down page or an export re-runs the load (it is
the same rows, which is what keeps a list equal to its card). The cost that does grow is the number of rows
loaded (entries, allocations, clocks) for the period and the people, which is why a period is at
most 366 days and a request at most 1,000 people, and why the heatmap is offered for at most 31 days
and 200 people. The test also proves the figures at scale (planned, actual, planned actual, the
drill-down's total and the breakdowns' sums against the cards), and runs the client, connection,
priority and kind filters through the real translator, for an administrator and for a technician
whose ticket scope is "assigned" (33 queries: the existence checks and the caller's ticket scope).
The same theory was run on **PostgreSQL 17** (`DESK_TEST_POSTGRES`, each test in a database built
by the real migrations): the same counts (27 / 27 / 27 / 26 / 26 / 29 / 18, filtered 33, a technician's
own view by client 40), and at 500 people with 30,000 allocations and 30,000 entries: overview week
575 ms, four weeks 890 ms, one team 125 ms, one person 34 ms, drill-down 204 ms (7,500 rows), export
427 ms, filtered overview 1.6 s while other tests shared the machine (0.8 s alone)
(a container on a development laptop; first-call compilation included).

**Not benchmarked here:** a year of 500 people with 1,000,000 entries. The 500-people / 30,000-entry
run above is the largest a unit test can seed in reasonable time; the next step, if a desk reaches
that volume, is a daily rollup per person (deterministic, rebuildable, tenant-scoped, never the only
source of truth), which is deferred until a measurement asks for it rather than built in advance.

## Reconciliation

Three places check the same figures against their sources, and all must pass before the phase is
complete:

1. `WorkforceAnalyticsTests.The_specification_fixture_reconciles_exactly` reproduces the
   specification's fixture (capacity 40 h, planned 32 h, actual 30 h, completed 10, reactive 5 h →
   80 %, 75 %, 16.67 %, −2 h / −6.25 %, estimate variance 21.88 % over 4 compared days) line by line
   from real schedules, allocations, entries and tickets, for the person and for the organization.
2. `Every_card_opens_the_records_that_make_it_and_hides_work_the_caller_cannot_open` asserts the
   drill-down's totals equal the cards (actual, reactive, planned, completed) and the paging keeps
   the whole set's total.
3. The performance theory asserts, at 500 people, that the people rows, the sources, the work types
   and the days each sum to the totals, and that the drill-down's total equals the card.

In the browser (`e2e/workforce-analytics.spec.ts`) the cards are compared with what the API itself
answers for the same period and filters, and the drill-down lists the seeded tickets, each said
planned or reactive as it was.

## Audit

| Event | When | Carries |
|---|---|---|
| `workforce.analytics.exported` | every successful export | report, period (key, from, to, zone), the filters, rows, people |

Dashboard views are not audited (no existing policy asks for it); reading is `schedule.view`, like
every other workforce read.

## Deferred (with the reason)

| Not in this phase | Why |
|---|---|
| Quality signals (reopened, escalations, SLA met / breached, first response, satisfaction, review) | Already on the productivity dashboard with their own definitions; two numbers for one fact would be worse than a link |
| A productivity score | None introduced; the existing weighted score stays on its own page and is not used here |
| Importing PSA-side time rows | A sync change (Phase 6 deferred item); a note says that time is in no day |
| Aggregation tables, caching, materialized summaries | Raw reads were benchmarked first (above); not needed at the measured volumes |
| Excel export | CSV opens in every spreadsheet; one format to keep safe |
| Email of the dashboard, scheduled analytics reports | The staff-report scheduler exists; wiring these tables into it is a later, separate change |

## Troubleshooting

| Symptom | Cause | What to do |
|---|---|---|
| Capacity utilization reads low this week | The period has not ended: later days hold capacity and no time yet (the note says so) | Choose last week, or wait |
| A person's capacity is 0 and utilization N/A | No working schedule in force on those dates | Set their schedule (Workforce → person → Work schedule) |
| A person shows "not offered" for capacity | They are not offered for planned work (`IsSchedulable` off), so their capacity does not count, as Team capacity counts it | Workforce → person → offered for planned work |
| Recorded time is lower than the PSA's figure for the same person | Time entered directly in the PSA has no portal row and is not in any day | Expected; the note states it. PSA-side rows are a deferred sync change |
| "Work you cannot open" in a client, source or priority table | The caller may not open that ticket (ticket scope) | Expected: the time counts, the ticket's identity does not travel |
| A client or priority filter shows less time than the person recorded | The filter matches only tickets the caller may open; time on tickets they cannot open is left out rather than attributed | Expected; an administrator's view has it all |
| Variance reads N/A early in the week although work is planned | Nothing was planned up to today yet: later days are not compared until they arrive | Expected; the panel says how much was planned so far |
| A night worker's hours after midnight are on the previous day | They are inside the shift that began that evening, and are compared with that shift's capacity | Expected; My day lists them under the calendar date |
| Overdue here is lower than a count of past due dates | Tickets whose SLA clock is paused (waiting on someone) are not late, as on the boards | Expected |
| Completed is lower than the tickets closed | A finished ticket with no `ResolvedAt` or `ClosedAt` is in no period (the note counts them); a ticket reopened since is not finished now; a ticket credited to nobody in scope is not a row | As designed; see the specification §11 |
| "Person was not found." for a colleague | Outside the caller's `schedule.view` scope | Ask for a wider scope, or filter by team |
| The heatmap says to choose a shorter period | More than 31 days | Choose a month or less |
| Export refused with 403 | The caller lacks `workforce.analytics.export` | An administrator grants it on the role |
| The export is cut at 50,000 rows | The recorded-work report's limit | Narrow the period or the filters |
