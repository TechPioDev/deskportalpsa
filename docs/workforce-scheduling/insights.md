# Management insights (Phase 8)

What is ahead, what changed, what the optional quality data supports, and what needs attention, for
the people the viewer may see. Deterministic sums over records: nothing is predicted statistically,
every statement shows its rule and its numbers, and nobody is scored or ranked.

Definitions, formulas, thresholds, the availability of every optional signal and the classification
of everything that was considered (built, reused, deferred, not required) are in
[PHASE8_MANAGEMENT_INSIGHTS_DESIGN.md](PHASE8_MANAGEMENT_INSIGHTS_DESIGN.md), written before the
code. This page says how it works as built. Reports are in [reports.md](reports.md).

```
PIO MANAGE
  Internal tickets ─┐
  PSA work ─────────┴─> Normalized work
                           │
                           v
                    Work allocation ──> My plan / Team scheduler
                           │
                           v
                        My day ──> Actual work time ──> Worklog / PSA time
                           │
                           v
                  Planned against actual
                           │
                           v
                  Workforce analytics            (Phase 7)
                           │
                           v
                  Management insights            (Phase 8)
                     │              │
                     v              v
           Historical analytics   Future forecast
                     └──────┬───────┘
                            v
                      Report center
```

**Internal only.** Every route is under `api/workforce`, behind the Workforce switch, for staff.
No client account can reach any of it.

## Where it is

Workforce → **Management insights** (`/dashboard/workforce/insights`), shown in the sub-navigation
to someone who can see more than themselves. The page works for anyone with `schedule.view`: at
scope Own it is the person's own forecast and history, and nobody else's.

The filters (window, team, department, technician, client, source, priority, comparison) and the
open tab live in the address, so a view can be bookmarked and shared inside the team.

| Part | What it shows |
|---|---|
| **Needs attention** | A list of statements, each from one fixed rule over figures on the page, most severe first. Each has a severity as a word and an icon (Info, Watch, Attention, Critical), **Why this is shown** (the rule and the numbers that produced it), and where it leads: the records, the team, or the tab |
| **Forecast summary** | Capacity ahead · Confirmed · Tentative · Estimated unscheduled · Unestimated (a count) · Projected demand · Capacity gap · Projected load. Confirmed, tentative, unscheduled and unestimated each open their records |
| **Forecast** tab | Capacity and demand by day; schedule coverage; work at risk; recurring work; workload balance; forecast by technician and by team; skill capacity; demand by client and by source |
| **What changed** tab | A period against the one before; planned and reactive work over the last eight weeks; client demand; work sources; estimate variance by category, client or source |
| **Quality signals** tab | The signals the records support, each with its data-quality label and what it rests on |
| **Data and integrations** tab | The planning data the forecast rests on; per PSA connection, sync state and mapping coverage (with `integration.health.view`) |

## The forecast

| Figure | Formula |
|---|---|
| Capacity ahead | Σ usable minutes per person-day in the window, for people offered for planned work. **Today counts from now** |
| Confirmed | Σ minutes of Planned allocations whose shift date is in the window; today, the part still ahead |
| Tentative | The same for Tentative allocations. Never merged with confirmed |
| Estimated unscheduled | Σ over eligible open estimated work of `max(0, estimate − confirmed − tentative)` (allocations of all time) |
| Unestimated | Count of open work with no estimate and no plan ahead. **Never turned into hours** |
| Projected demand | Confirmed + Tentative + Estimated unscheduled |
| Capacity gap | Capacity − Projected. Shown as "left" or "short" |
| Projected load | Projected ÷ Capacity. N/A when capacity is 0 |
| Schedule coverage | Σ min(estimate, allocated) ÷ Σ estimate over open estimated work. N/A when nothing is estimated |

- **Window**: next 7 (default), 14 or 30 days; the rest of this week; next week; the rest of this
  month; or a custom range starting today or later, at most 62 days. Dates are the organization's.
- **Whose work**: open work held by a person in scope. Work held by nobody and routed to a team is
  counted for a caller who sees others: every team for a caller who sees everyone, the caller's own
  teams otherwise. It is its own line ("Not yet assigned"), in its team and in the totals, and in
  no person's row. A question about one person or one department is about held work only.
- **Eligible**: work whose planning window lets it start by the window's last day. A project that
  may not start for a month is not this week's demand.
- **By day**: capacity, confirmed and tentative are per shift date. Unscheduled effort has no day of
  its own: it is shown on the day its work is **due**, with two figures for the rest (already past
  its due date; no due date in the window). It is never spread across days.
- **Remaining effort is "estimated minus allocated"**, as the planning queue counts it. A planned
  hour that passed without work still counts as allocated. This is Phase 5's rule, said on screen.

The two fixtures of the brief are tests: 100 h capacity, 60 h confirmed, 10 h tentative, 20 h
unscheduled and five unestimated items give 40 h left after confirmed, 90 h projected, 10 h left and
a Watch at exactly 90 %; 100 / 80 / 10 / 25 gives 115 h projected and "Projected demand is 15h over
capacity in this window."

### Skill capacity

Only for skills that some eligible estimated work **requires** (the planning requirement's skill).
Demand is that work's unallocated effort; capacity is the free time, after confirmed and tentative
work, of the people in scope who hold the skill at any level (nothing in the product reads the
level). A person with two skills counts under both, so the rows do not add up; the screen says so.

### Work at risk

- **Past its due date**: open, due date passed, SLA clock not paused (the boards' rule).
- **Not enough free time**: due within the window, with more unallocated effort than its holder has
  free (after confirmed work) on the days up to the due date. This is the planning queue's own rule,
  applied to all due work rather than the queue's first hundred items.
- Work due before the window opens is not judged for capacity: the time that matters is outside the
  window.

### Recurring work

Active recurring tickets and how many times each is scheduled to be raised in the window, by the
same schedule function the worker uses. Counts only: an occurrence carries no estimate, and one is
skipped while the previous is still open. Shown to callers who hold `boards.manage`, the existing
gate for the recurring list.

## What needs attention

| Rule | Condition | Severity |
|---|---|---|
| Capacity (everyone, a team) | Projected load ≥ 90 % · > 100 % · > 120 % | Watch · Attention · Critical |
| People over capacity | People with projected load > 100 % | Attention |
| No capacity for demand | Demand on people whose capacity in the window is 0 | Attention |
| Estimated work not scheduled | Estimated unscheduled > 0 | Watch |
| Unestimated work | Unestimated items > 0 | Info; Watch from 10 |
| Skill gap | Skill demand > skilled free time | Attention |
| Work at risk | Any item without enough free time before its due date | Attention |
| Overdue work | Any open item past its due date | Attention |
| Reactive share changed | Moved by ≥ 5 percentage points against the previous period | Watch |
| Time not in the PSA | Portal time entries of the people in scope whose push failed | Watch |
| Stale or failing sync | An enabled connection degraded, failed, or with no successful sync for 24 h (with `integration.health.view`) | Attention |
| Unmapped values | A connection with a status or priority no rule maps (with `integration.health.view`) | Watch |

The thresholds are constants in one place (`WorkforceAnalyticsService.InsightThresholds`). Nothing
is called an "AI insight" and nothing is: a statement can always be traced to its rule.

## What changed

One Phase 7 load over the range needed, tallied per sub-period; the definitions of actual, planned,
reactive and completed are Phase 7's, so the two screens cannot disagree.

- **Comparison**: last 30 days (default), last 7 days, last week or last month against the period
  immediately before (a calendar month against the calendar month before). Current, previous,
  change and change %. **The percentage is N/A when the previous value is 0**; both values are
  always shown. Reactive share changes in percentage points.
- **By week**: the last eight weeks, Monday to Sunday; the one in progress is marked.
- **Estimate variance** by category, client or source: planned time against the time recorded on
  that planned work, over the planned ticket-days of the period. Category is the ticket's category
  as stored; monitoring alerts are one group. It describes estimates per kind of work and is never
  attributed to a person.

## Quality signals

Over the work completed in the period and credited to the people in scope: the same set as the
Phase 7 Completed card. Each signal is a numerator over a denominator, out of a population, with a
**data-quality label** set by fixed rules and the reason in a sentence.

| Signal | Counted | Label |
|---|---|---|
| Resolved by its due date | Finished at or before the due date ÷ completed work that has one | High when ≥ 90 % of the completed work has a due date; Partial below; Not available with none |
| First response promise kept | Answered by the promised time ÷ work that carried a promise | Partial at best: PSA tickets carry no promise here, and only portal replies are seen |
| Reopened | Brought back to work ÷ completed work | Partial when the set includes PSA work (a reopen made in the PSA is not seen); High for board work |
| Client satisfaction | Ratings of 4 or 5 ÷ rated work | High when at least half of the completed client work was rated |
| Passed review first time | Approved with no send-back ÷ reviewed work | Partial: only boards and topics that require review |
| Escalated work | — | **Not available**: nothing records an escalation; a reassignment is not one |
| Repeat issues | — | **Not available**: ticket links are manual, and a duplicate is not a repeat |

There is no per-technician quality table, by design. A signal with no reliable source is shown as
Not available with the reason, and is not estimated from something else.

## Data and integrations

- **Planning data** (everyone who sees the forecast): open work held, and how much more nobody holds
  in the portal (that work is in nobody's forecast); how much is estimated; people with a working
  schedule and people not offered for planned work; estimates that name a skill; finished work with
  no completion date.
- **Mapping and integration health** (`integration.health.view`), per connection: state, last
  successful sync, stale after 24 h; the share of tickets whose status and priority are one of the
  portal's normalized values, with up to five unmapped values; the PSA logins holding tickets that
  are linked to a portal user (the integration account is not a technician); tickets on a
  placeholder client; tickets in sync error; time entries failed or pending. **No credential,
  address or error text is returned**: the error text stays on the Integration health page.

## API

All GET, under `api/workforce`, `schedule.view` unless stated.

| Route | Returns |
|---|---|
| `insights/forecast?window&from&to&teamId&departmentId&appUserId&clientId&source&priority` | The forecast: totals, by day, teams, people, not yet assigned, clients, sources, skills, recurring, coverage, planning data, work at risk, the attention list, notes |
| `insights/forecast/work?list=confirmed\|tentative\|unscheduled\|unestimated\|at-risk\|overdue\|skill&skillId&skip&take&…` | The records behind a figure, paged (≤ 200), with the whole set's count and minutes |
| `insights/trends?compare=last-30\|last-7\|last-week\|last-month&…` | Both periods, the totals compared, eight weeks, clients, sources, estimate variance three ways, quality signals, attention, sync freshness, notes |
| `insights/health` (`integration.health.view`) | Per connection: state and mapping coverage; attention |

An unknown window, list or comparison is a 400. A person outside the caller's scope and a client or
connection that is not the organization's are "not found". A team outside the scope is nobody.

## Permissions, client isolation, tenant isolation

No new permission key.

| Capability | Permission |
|---|---|
| Insights, forecast, comparison, quality signals | `schedule.view`; its scope decides whose |
| Mapping and integration health; the sync and mapping statements | `integration.health.view`, required by the controller and checked again in the service |
| Recurring work in the forecast | `boards.manage` |

- A ticket the caller cannot open counts for its minutes and gives nothing else away: no reference,
  title, client, priority, status, due date or skill. It is one "Work you cannot open" row in the
  client and source tables, and a client, connection or priority filter matches only tickets the
  caller may open, so a filter cannot be used to find out whose it is.
- Another organization has its own database context and its own tenant filter: none of this
  organization's people, work, teams, clients or connections appear, and its ids are "not found".
- Nothing is cached, so there is no cache to leak across tenants or to go stale.

## Performance

One load per request, then memory. Query counts are constant whatever the number of people, days or
hours, and are pinned in `CapacityPerformanceTests` through a real SQL translator.

| Read | Queries | 500 people, 30,000 allocations, 30,000 time entries (SQLite, development machine) |
|---|---|---|
| Forecast, a week or four weeks | 51 | 0.55 – 0.62 s |
| Forecast, one team | 50 | 0.13 s |
| Drill-down | 51 | 0.41 s |
| Comparison (last week, last 30 days) | 28 | 0.92 – 1.14 s |
| Mapping and integration health | 12 | under 0.1 s |
| Report preview / XLSX | 53 / 54 | 0.62 / 0.69 s |

Limits: a forecast window is at most 62 days; history at most 366; at most 1,000 people; lists
paged at 200. No aggregation table, background job or cache was added: the measured cost does not
ask for one.

## Reconciliation

| This | Equals |
|---|---|
| Projected demand | Confirmed + Tentative + Estimated unscheduled, always |
| Σ people's projected + not yet assigned | The total |
| Σ days' capacity, confirmed, tentative | The totals |
| Σ unscheduled due by day + past due + no due date | Estimated unscheduled |
| A drill-down's count and minutes | The figure it was opened from |
| Capacity and confirmed, for a window starting tomorrow or later | Phase 7's capacity against demand for the same days |
| The comparison's current period | Phase 7's dashboard for the same period |
| The population of every quality signal | Phase 7's Completed card |
| The Future capacity report's rows | The forecast's people |

Each is asserted in tests.

## Audit

Reading insights is not audited (reading the dashboards never was). An export is: see
[reports.md](reports.md#audit).

## Deferred

| Item | Why |
|---|---|
| Scheduled and emailed reports | The existing scheduler does not limit recipients to staff and does not re-check the creator's permission when a run fires. What a safe version needs is recorded in the design report, §17 |
| Escalation and repeat-issue signals | No explicit source data. Nothing is inferred from reassignments or titles |
| Configurable thresholds | Fixed constants until a business need appears; a change would be audited |
| Saved views | The address is the view; a stored-view table adds an id to authorize for little gain |
| Statistical forecasting | Out of scope by design: the forecast is what the records say |
| PDF | CSV and XLSX cover the data need |

## Troubleshooting

| Seen | Why | Do |
|---|---|---|
| Capacity ahead is 0 or low | People have no working schedule, or are not offered for planned work (the notes and the Data tab say how many) | Set the schedules |
| "Unestimated" is high and "Estimated unscheduled" is 0 | Nobody has sized the work | Set effort from the planning queue or the ticket's Planned work panel |
| Open work is missing from the forecast | Nobody holds it in the portal (PSA tickets held only by a PSA login). The Data tab counts it | Link the PSA login to the person under Users, or assign the work |
| A figure for today is lower than the plan | Today counts from now | Expected |
| "Work you cannot open" | The viewer may not open that ticket | Expected: the minutes count, the identity does not travel |
| The Data tab has no connection table | The viewer lacks `integration.health.view` | An administrator grants it |
| A quality signal says Not available | The completed work in the period carries no such data, or nothing records it at all | Read "What it rests on" |
| A custom window is refused | It starts before today, or is longer than 62 days | Choose dates from today |
