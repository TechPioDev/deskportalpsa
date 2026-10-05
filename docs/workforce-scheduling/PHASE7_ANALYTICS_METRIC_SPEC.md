# Phase 7 analytics: metric specification

Every figure the Workforce analytics dashboard shows is defined here before it is drawn. A number
that is not on this page is not on the dashboard. The definitions reuse what Phases 1 to 6 already
decided; nothing is re-derived, and nothing in the dashboard compensates for source data.

**What this is.** Operational workforce analytics: how work is distributed, planned, executed and
completed, as facts over a period. **What this is not:** attendance, payroll, presence, keystroke or
screenshot monitoring, surveillance, or an automated score of anyone. No figure here is a
performance score, nobody is ranked, and the dashboard never labels a person best, worst or top.

The existing Staff reports / productivity dashboard (`api/dashboard`, `productivity.*.view`) stays
as it is. It answers ticket-centred questions (SLA, first response, reopens, satisfaction, review).
This specification does not redefine any of those; where the two overlap (hours by day) the
differences are stated in [§ Source audit](#source-of-truth-audit).

## Conventions

| Term | Meaning here |
|---|---|
| Period | The selected date range, as whole calendar dates `[From, To]` in the **organization's time zone** |
| Person-day | One person on one **shift date**: the date a working window starts on, in the zone of that person's schedule version in force that day (the organization's zone when they have no schedule). The same rule as capacity (Phase 2), the plan (Phase 3) and My day (Phase 6) |
| Minutes | Capacity and planned time are whole minutes. Actual time is whole **seconds** in the API (the screen rounds), so a day reconciles to the second with My day |
| N/A | A ratio whose denominator is zero is **null** in the API and shown as "N/A", never 0 % and never an error |
| In scope | The people the caller's `schedule.view` scope reaches, narrowed by the filters |

## Source-of-truth audit

| Metric | Authoritative source | Where it is computed today |
|---|---|---|
| Working capacity | Working schedule versions, breaks, capacity exceptions (Phase 1, 2) → `CapacityCalculator.ForDay` over `WorkforceCalendar` inputs → `UsableMinutes` | `CapacityService`; the dashboard calls the same calendar and calculator |
| Planned time | `work_allocations` rows: Status Planned (1) = confirmed, Tentative (2) = tentative; `PlannedMinutes`; the person-day of `StartsAt` | `WorkPlanService` (My plan), `WorkTimeService.MyDayAsync` |
| Actual work time | `ticket_time_entries` rows by their portal author (`AppUserId`), **any** sync status, dated by `EntryDate`; plus live work sessions (Active / Paused, no entry yet) by their start | `WorkTimeService.MyDayAsync` ("the one rule for actual time") |
| Ticket count / work items | `tickets` (the unified work model: boards, Autotask, ConnectWise, monitoring) | — |
| Completed work | `tickets` with a finished status (`TicketStatusRules.Finished`: RESOLV / CLOSED) and `ResolvedAt ?? ClosedAt`; credit `ResolvedByAppUserId ?? AssignedAppUserId ?? the linked PSA login` | The same attribution rule as `TechnicianMetricsService` |
| Reactive work | Actual time on a person-ticket-day with **no** Planned or Tentative allocation of that person on that ticket starting that day | `MyDaySummaryDto.UnplannedActualSeconds` (Phase 6) |
| Work source | `Ticket.Origin` (Psa / Internal / Rmm) and `PsaConnectionId` → the connection's name, "Team boards", "Monitoring" | `TechnicianMetricsService.BySource`, `WorkPlanService.Source` |
| Client | `Ticket.ClientCompanyId` → `client_companies.Name` | — |
| Team / department | `user_teams` / `user_departments` of the person (a person may be in several) | `WorkforceAccess.Narrow` |
| Priority | `Ticket.PortalPriority` (normalized string: CRITICAL, HIGH, NORMAL, LOW; raw values tolerated) | — |
| Work type | `TicketTimeEntry.WorkTypeLabel` (the PSA work type the hour was logged under) | — |
| Billable | `TicketTimeEntry.Billable` (what the hour was marked when logged) | — |
| Due dates | `Ticket.SlaDueAt` (the resolve-by date the SLA mapping produced) | Planning queue, boards |
| Time zone | `MspOrganization.TimeZone` (periods); `WorkSchedule.TimeZone` of the version in force (person-days) | Staff reports; capacity |

No analytics table is created. Every read goes to the operational rows (see
[analytics.md](analytics.md#performance) for the measured cost and the range limit). Nothing is
cached, so there is no cache to leak between tenants or scopes.

**Known differences from the productivity dashboard**, stated rather than hidden:

| | Workforce analytics (this page) | Productivity dashboard (`api/dashboard`) |
|---|---|---|
| Day an hour belongs to | The person's shift date (their zone) | The UTC date |
| Which hours count | Every portal entry (Synced, Pending, Failed): rejected time is still work, marked "not in the PSA yet" | Synced only |
| Period boundaries | Organization zone | Caller-supplied instants (the browser's local calendar) |
| PSA-side time (entered in the PSA itself) | Not included: it has no portal row and no day. Shown as a note | Ticket totals only |

## 1. Period

**Definition.** The dates the dashboard covers, inclusive, in the organization's time zone.

**Presets** (resolved on the server from the server clock, so two people see the same week):

| Key | Dates |
|---|---|
| `today` | today |
| `yesterday` | today − 1 |
| `this-week` | Monday of this week … Sunday (weeks run Monday to Sunday, as staff reports do) |
| `last-week` | the Monday … Sunday before |
| `this-month` | 1st … last day of this month |
| `last-month` | the month before |
| `7d` | today − 6 … today (7 days) |
| `30d` | today − 29 … today (30 days) |
| `custom` | `from` and `to` as given |

**Limits.** At most **366** days; dates within one year before today and one year after (a future
range is allowed: "next week" is the capacity-against-demand view). `to` before `from` is refused.

**Display.** Every screen states the resolved dates and the zone: "Mon 5 Jan – Sun 11 Jan 2026
(Asia/Kolkata)". A running period (this week) is **not** cut at now: its later days simply have no
actual time yet, and the capacity still counts, so utilization of a running week reads low until
the week is over. The screen says so.

**Edge cases.** A clock change inside the period changes nothing: dates are calendar dates, instants
are converted per day. A period that ends before any schedule existed has zero capacity (N/A
utilization).

## 2. People in scope

**Definition.** Active staff accounts the caller's `schedule.view` scope reaches (Own: themselves;
Team / Department: the people they share one with; All: everyone in the organization), narrowed by
the team, department and technician filters. Client accounts are a different table and can never
appear, as callers or as rows.

**Rules.** A technician filter naming someone outside the scope is "Person was not found." (the
same words as for an id that names nobody). A team or department the caller's people are not in
yields nobody and empty figures, never an error that confirms the group exists. At most 1,000
people (the capacity engine's limit); more is "Choose a team or a department to narrow it down."

**Capacity is counted for people offered for planned work** (`IsSchedulable`), as Team capacity and
the team scheduler count it; a person who is not offered shows their actual time with capacity
"—" and N/A utilization. A person with no schedule has capacity 0 (N/A utilization) and is counted
in the "no working schedule" note.

## 3. Working capacity (effective capacity)

**Definition.** The minutes the people in scope were available for planned work over the period:
their working windows after breaks, time unavailable and extra availability, per person-day.

**Formula.** For each person-day `d` of each schedulable person `p`:

```
capacity(p, d) = UsableMinutes(p, d)
               = gross window − breaks − unavailable exceptions + additional availability
Capacity        = Σ capacity(p, d)   over people in scope, dates in the period
```

`UsableMinutes` is Phase 2's figure, computed by `CapacityCalculator.ForDay` from
`WorkforceCalendar` inputs: the same code path as My capacity, Team capacity and the scheduler.

**Inclusion.** The schedule version in force on each date (a version that starts mid-period applies
from its start). Full-day unavailable takes the day. Part-day unavailable takes only what lands on
working time. Additional availability adds only time outside every working window.

**Exclusion.** Holidays take nothing by themselves (Phase 2 decision: shown, not deducted).
Planned work does **not** reduce capacity (it uses it). People not offered for planned work.

**Time zone.** Per person-day, in the person's zone. A night shift 18:00–03:00 is its start date's
capacity.

**Permission.** Follows `schedule.view` scope.

**Example.** Jason, Mon–Fri 08:30–17:30 with a 1 h break (8 h usable), Wednesday off all day:
Mon 480 + Tue 480 + Wed 0 + Thu 480 + Fri 480 = **1,920 min = 32 h**, not 5 × 8 = 40 h.

## 4. Planned work (confirmed) and tentative work

**Definition.** Minutes of work allocations starting in the period for the people in scope, by
status. Confirmed (Status Planned) and tentative (Status Tentative) are **never added together** in
a single figure; "Planned" always means confirmed, and tentative is shown beside it.

**Formula.**

```
planned(p, d)   = Σ PlannedMinutes of allocations a: a.AppUserId = p, Status = Planned,  shiftDate(a.StartsAt, zone(p, d)) = d
tentative(p, d) = Σ PlannedMinutes of allocations a: a.AppUserId = p, Status = Tentative, shiftDate(a.StartsAt, zone(p, d)) = d
```

`PlannedMinutes` is the allocation's own length (what was planned), not the part that landed on
available time: work placed over capacity by override is planned work, and that is what makes
[over-capacity](#12-planning-over-capacity) visible.

**Inclusion.** An allocation belongs to the date it **starts** on, in the person's zone (the My plan
and My day rule). Allocations on finished tickets stay (they were planned).

**Exclusion.** Cancelled allocations (Status 5), including those the release worker cancelled when
the ticket finished early. There is no "completed" allocation status: finishing a ticket cancels its
future allocations and leaves the past ones.

**Filters.** Client, source, priority and origin filters apply through the allocation's ticket.

**Example.** Jason has 4 × 8 h confirmed and one 2 h tentative piece this week: Planned 32 h,
Tentative 2 h. Scheduled utilization uses the 32 h; the capacity-against-demand view shows 32 h
confirmed and 34 h projected.

## 5. Actual work time

**Definition.** Time recorded as work by the people in scope on their person-days in the period.
Phase 6's one rule, summed: time entries dated the day plus live clocks that started the day.

**Formula.**

```
entries(p, d)  = Σ round(Hours × 3600) of ticket_time_entries e: e.AppUserId = p, shiftDate(e.EntryDate, zone(p, d)) = d
live(p, d)     = Σ elapsed(s) of work_sessions s: s.AppUserId = p, Status ∈ {Active, Paused}, TimeEntryId = null,
                                                 shiftDate(s.StartedAt, zone(p, d)) = d
actual(p, d)   = entries(p, d) + live(p, d)          (seconds)
Actual         = Σ actual(p, d)
```

`elapsed(s)` = the session's closed seconds plus the open segment by the server's clock at the moment
of the answer.

**Inclusion.** Every portal time entry whatever its sync status (a rejected push is still work).
Entries typed in by hand, entries a clock wrote, and corrected entries at their corrected hours.

**Exclusion.** Time entered directly in the PSA (no portal row; it reaches the ticket's totals and
time notes only). Cancelled or discarded sessions. A stopped session's seconds are in its entry and
nowhere else, so **no minute is counted twice** (one entry per session is a database rule).

**Time zone.** The entry's `EntryDate` (for a clock, its start) in the person's zone. A clock across
midnight belongs to the day it started.

**Live time** is reported separately (`LiveSeconds`) so a reader knows how much of "actual" is a
clock still running; it is only ever non-zero for today.

**Example.** Monday: a 1 h entry from a stopped clock, a typed 30 min entry, a clock running since
15:00 read at 15:20 → actual = 3,600 + 1,800 + 1,200 = **6,600 s (1 h 50 m)**, of which 1,200 live.

## 6. Planned actual, reactive actual, reactive share

**Definition.** Actual time split by whether the work was planned. Reactive (unplanned) work is
actual time on a ticket the person had **nothing planned on that day**. It is a fact about
planning, not a judgement: urgent support, outages, incidents and alerts are reactive by nature.

**Formula.** For each (person p, ticket t, date d) with actual time:

```
planned?(p, t, d) = ∃ allocation a: a.AppUserId = p, a.TicketId = t, Status ∈ {Planned, Tentative}, shiftDate(a.StartsAt) = d
PlannedActual     = Σ actual(p, t, d) where planned?(p, t, d)
ReactiveActual    = Σ actual(p, t, d) where not planned?(p, t, d)
ReactiveShare %   = ReactiveActual / Actual × 100          (null when Actual = 0)
ReactiveWorkItems = count of distinct t with ReactiveActual > 0
```

This is exactly My day's "not planned" item rule (`UnplannedActualSeconds`), so a day drilled down
from the dashboard shows the same split as the person's My day. The classification is derived on
every read from the allocations and the time, never from a stored flag.

**Edge cases.** Time on a planned ticket outside the planned slot (started early, ran late) is still
planned actual: the day had a plan for that work. A tentative allocation counts as planned here
(the work was expected). A cancelled allocation does not.

**Example.** Actual 40 h, of which 31 h on tickets planned that day: Planned actual 31 h, Reactive
9 h, Reactive share **22.5 %**.

## 7. Scheduled utilization

**Definition.** How much of the available capacity was planned (confirmed) into.

**Formula.** `ScheduledUtilization % = Planned / Capacity × 100`; null (N/A) when Capacity = 0.
Can exceed 100 % (over-capacity planning).

**Example.** Capacity 40 h, Planned 36 h → **90 %**.

## 8. Capacity utilization

**Definition.** How much of the available capacity was spent on recorded work.

**Formula.** `CapacityUtilization % = Actual / Capacity × 100`; null when Capacity = 0. Can exceed
100 % (work outside the schedule, after hours). **An operational figure, not a performance score**:
a running week reads low, a week of outages reads high, and neither says anything about a person.

**Example.** Capacity 40 h, Actual 30 h → **75 %**.

## 9. Planned against actual (variance)

**Definition.** Actual time compared with planned time, for a person, a team, a day or a ticket.

**Formula.**

```
Variance        = Actual − Planned           (minutes; positive = more time than planned)
Variance %      = (Actual − Planned) / Planned × 100     only when Planned > 0, else null
```

Positive is not bad and negative is not good: an estimate can be wrong, a client can be slow, a
reboot can take an hour. The screen shows the sign and the value and nothing else.

**Example.** Jason: Planned 32 h, Actual 35 h → Variance **+3 h (+9.4 %)**.

## 10. Estimate variance (absolute)

**Definition.** How far planned estimates were from actual time, regardless of direction, over the
ticket-days that had a plan. Labelled **Estimate variance**, never a quality score.

**Formula.** Over person-ticket-days with planned(p, t, d) > 0:

```
AbsoluteVariance   = Σ | actual(p, t, d) − planned(p, t, d) |      (minutes)
EstimateVariance % = AbsoluteVariance / Σ planned(p, t, d) × 100   (null when nothing was planned)
PlannedItemsCompared = number of such person-ticket-days
```

**Exclusion.** Reactive work (nothing to compare with). Planned ticket-days with no actual time
count in full (planned 60, actual 0 → 60 of variance): a plan that produced no work is an estimate
that was off, and leaving it out would flatter the figure.

**Example.** Planned 60 m, actual 75 m → absolute variance 15 m, 25 %.

## 11. Completed work

**Definition.** Unique work items (tickets) that reached a finished status during the period and are
credited to a person in scope.

**Formula.**

```
finishedAt(t) = t.ResolvedAt ?? t.ClosedAt
credit(t)     = t.ResolvedByAppUserId ?? t.AssignedAppUserId ?? the portal user linked to t's PSA login
Completed     = count of distinct t: Finished(t.PortalStatus), finishedAt(t) ∈ [From 00:00, To 24:00) in the organization zone, credit(t) ∈ scope
```

One ticket is one completed item, however many sessions, entries or people it took; three worklogs
on one ticket are **one** completed ticket. Per person it is credited once, to one person (the
rule above), so the rows add up to the total.

**Exclusion.** A finished ticket with neither `ResolvedAt` nor `ClosedAt` (imported closed with no
date) is in no period; the count of these is in the notes. A ticket finished in the period and
reopened later is still finished-in-period if its status is finished now; if it is open now it is
not counted (the dashboard reads the current status, as the productivity report does). Work items
credited to nobody in scope (unassigned, or held by the integration account) are not in any row and
not in the total.

**Time zone.** The organization's zone (a completion is an organization event, not a person-day).

**Example.** Jason finished 28 tickets this week, one of them with 3 time entries: **28**.

## 12. Planning over-capacity

**Definition.** Confirmed planned time that exceeds a person-day's capacity. A fact about planned
time (an override put it there); never "overtime".

**Formula.** `OverCapacity = Σ max(0, planned(p, d) − capacity(p, d))` over schedulable person-days;
`OverCapacityPersonDays` = the number of such days.

**Example.** Capacity 8 h, Planned 10 h → **2 h over**, one person-day.

## 13. Work items touched

**Definition.** Unique tickets with any actual time by the people in scope in the period.

**Formula.** `WorkItems = count of distinct t with actual(p, t, d) > 0`.

## 14. Work right now

Snapshot figures as of the moment of the answer (not period-bound; the screen says "right now"):

| Figure | Definition |
|---|---|
| Open work | Open tickets (`TicketStatusRules.Open`) held by a person in scope (`AssignedAppUserId`) |
| Unscheduled | Open work with no Planned or Tentative allocation ending after now (the unscheduled-list rule) |
| Overdue | Open work with `SlaDueAt` < now |
| Due today | Open work due later today in the organization zone |
| Due soon | Open work due within the next 8 hours (`TicketStatusRules.DueSoonHours`, the boards' meaning of "soon") |
| Unscheduled due | Unscheduled open work that is overdue or due within 7 days |

Due dates and SLA mappings are read as they are; nothing recomputes an SLA.

## 15. Client, internal and monitoring work

**Definition.** Actual time split by what kind of work the ticket is: a client's (Origin Psa), the
team's own (Origin Internal, boards), or opened by monitoring (Origin Rmm). Internal work is work;
the split exists so utilization can be read without treating internal time as unproductive.

**Formula.** `ClientSeconds`, `InternalSeconds`, `MonitoringSeconds` partition `Actual`.

## 16. Marked-billable time

**Definition.** Actual time whose entries were marked billable when logged. Shown beside actual
time, never divided into it: the portal defines no "productivity = billable / actual".

**Formula.** `BillableSeconds = Σ round(Hours × 3600) of entries with Billable = true` (live clocks
are not yet marked, so they are not counted).

**Caveat on screen.** "As marked when logged; what the PSA invoices is the PSA's."

## 17. Breakdowns

Every breakdown is the same figures (§ 3–13 where they apply) grouped by one dimension, over the
same filtered set, so a table's rows add up to the cards.

| By | Grouping | Capacity | Notes |
|---|---|---|---|
| Technician | person | yes | sortable by any column; no default "best" order (name order) |
| Team | the person's teams | yes (members' capacity) | a person in two teams appears in both; the organization total counts them once (said on screen) |
| Client | the ticket's client company | no | tickets the caller may not open are one row, "Work you cannot open"; tickets with no client (boards) are "No client" |
| Source | the ticket's PSA connection name, "Team boards", "Monitoring" | no | |
| Priority | `PortalPriority`; blank is "Not set" | no | |
| Work type | the entry's `WorkTypeLabel`; blank is "Not set" | no | actual time only (allocations carry no work type) |
| Day | the person-day date | yes | the daily trend; completed work is dated in the organization zone |

## 18. Capacity against demand

For the selected period (choose a future range for "next week"):

```
Available capacity    = Capacity (§ 3)
Confirmed demand      = Planned (§ 4)
Tentative demand      = Tentative (§ 4)
Projected demand      = Confirmed + Tentative
Shortage              = max(0, Projected − Available)
Remaining (confirmed) = max(0, Available − Confirmed)
```

Confirmed and tentative are never merged; the shortage is the projected figure's, said as such.
Unscheduled work's estimated effort is the planning queue's figure and is linked, not repeated.

## 19. Heatmap

One cell per person per date, for periods of at most 31 days and at most 200 people (otherwise the
screen says how to narrow it). The metric is **explicit** and chosen by the reader:

- **Actual capacity utilization %** = actual / capacity (§ 8), or
- **Scheduled capacity %** = planned / capacity (§ 7).

Every cell's tooltip and table alternative carry the three facts (actual or planned, capacity, the
percentage). A day with no capacity is "—", not 0 %. 100 % is not ideal and 50 % is not poor; the
colour scale is a neutral single hue by intensity, so meaning is never carried by colour alone.

## 20. Drill-down (every figure explains itself)

| Card | Opens |
|---|---|
| Actual, Planned actual, Reactive | The time entries (and live clocks) that make the figure: date, person, work, minutes, billable, planned / reactive, sync state; the list's total equals the card |
| Planned, Tentative | The allocations: date, person, work, minutes, status |
| Completed | The unique finished work items: reference, title, client, source, when, credited to |
| Open, Unscheduled, Overdue | The open work items as of now |

Lists are paged on the server (50 a page, at most 200). The figure on the card and the list's total
are computed from the same loaded rows in one request path, so they cannot drift apart.

## 21. Freshness

Every answer carries `GeneratedAt` (the server clock when it was computed); the screen shows "as of
HH:MM" and refetches once a minute while open. For PSA tickets the dashboard adds the newest
successful sync time per connection, so a reader knows how old the ticket side of the figures is.
Nothing is cached.

## 22. Not implemented (deferred, with the reason)

| Signal | Why not here |
|---|---|
| Reopened work, escalations, SLA met / breached, first response, satisfaction, review pass rate | Already on the productivity dashboard, with their own definitions; duplicating them here under a workforce heading would create two numbers for one fact. Linked from the dashboard |
| Importing PSA-side time rows | A sync change (Phase 6 deferred item); until then a note states that PSA-entered time is not in any day |
| Aggregation tables / caching | Benchmarked raw first ([analytics.md](analytics.md#performance)); not needed at the measured volumes |
| Excel export | CSV only; the organization's spreadsheet opens it |
| A productivity score | The dashboard introduces none; the existing weighted score stays where it is and is not used here |

## 23. Fixture that every implementation must reproduce

Jason, zone Asia/Kolkata, Mon–Fri 08:30–17:30 with a 12:30–13:30 break (8 h a day). Week Mon 5 –
Sun 11 Jan 2026.

| Fact | Value |
|---|---|
| Capacity | 5 × 480 = 2,400 min (40 h) |
| Planned (confirmed) | 32 h (4 days × 8 h, Mon–Thu, on his ticket) |
| Actual | 30 h: 25 h on the planned ticket on Mon–Thu, 5 h on another ticket on Friday with nothing planned |
| Completed | 10 tickets credited to him finished in the week |
| Scheduled utilization | 32 / 40 = **80 %** |
| Capacity utilization | 30 / 40 = **75 %** |
| Reactive | 5 h, **16.67 %** of actual |
| Variance | 30 − 32 = **−2 h (−6.3 %)** |

`WorkforceAnalyticsTests.The_specification_fixture_reconciles_exactly` asserts every line.
