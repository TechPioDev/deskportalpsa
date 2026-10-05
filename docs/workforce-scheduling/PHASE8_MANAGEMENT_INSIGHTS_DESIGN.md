# Phase 8 design report: management insights, capacity forecasting, quality signals and reports

Written **before any Phase 8 code**, from the repository as it stands at `2e5bd84` (Phase 7 live)
and from a read-only inventory of production taken on 5 October 2026. It says what exists, what
Phase 8 reuses, what it adds, what it leaves out and why, and it defines every new figure. Nothing
below is built on data the system does not hold.

Phase 8 is **operational forecasting and reporting**. It is not an AI layer and it produces no
employee score: every figure is a deterministic sum of records, every insight names the rule that
produced it and opens the records behind it, and everything is tenant scoped, staff only and limited
to the people the caller's `schedule.view` scope reaches.

## Overview

```
                    PIO MANAGE
                        │
            ┌───────────┴───────────┐
         INTERNAL                PSA WORK
          TICKETS          Autotask · ConnectWise · future PSA
            └───────────┬───────────┘
                 NORMALIZED WORK                      tickets (Phase 0)
                        │
                 WORK ALLOCATION                      work_allocations, work_planning (Phases 3, 5)
            ┌───────────┴────────────┐
         MY PLAN              TEAM SCHEDULER          (Phases 3, 4)
            └───────────┬────────────┘
                      MY DAY                          work_sessions (Phase 6)
                        │
                 ACTUAL WORK TIME                     ticket_time_entries
                        │
                 PLANNED vs ACTUAL
                        ▼
              WORKFORCE ANALYTICS                     Phase 7: one load, tallied in memory
                        ▼
              MANAGEMENT INSIGHTS                     Phase 8 (this report)
                 /             \
        HISTORICAL             FUTURE
        ANALYTICS             FORECAST
   Completed · Reactive     Capacity · Demand
   Client · Source          Shortage · Skill gaps
   Estimate variance        Unscheduled · Unestimated
                 \             /
                 REPORT CENTER                        preview → CSV / XLSX
```

## 1. Previous phases: validation

| Check | Result |
|---|---|
| Phases 1–7 reports (the workforce docs) | Re-read; no open Critical or High item |
| Regression on main `2e5bd84` | 1,249 unit tests pass, as built for CI and with real time zones (`-p:InvariantGlobalization=false`) |
| Main CI for `2e5bd84` | 6 of 6 green, including Firefox and Safari |
| Production | Healthy; Phase 7 routes refuse anonymous callers; no client role holds a workforce permission |
| Phase 7 reconciliation | Proven three ways (specification fixture, drill-down equals card, sums at 500 people) and unchanged |

Phase 7's analytics reconcile with their sources, so forecasts and trends may be built on them.

## 2. Current architecture and what Phase 8 stands on

| Layer | Component | Used by Phase 8 for |
|---|---|---|
| Capacity | `WorkforceCalendar` + `CapacityCalculator.ForDay` (Phase 2) | Future effective capacity per person-day |
| Planning | `work_allocations` (Planned = confirmed, Tentative, Cancelled) | Confirmed and tentative demand |
| Estimates | `work_planning` (`RequiredMinutes`, `EarliestStart`, `LatestEnd`, `RequiredSkillId`), written only by `SetRequirementAsync` | Unscheduled estimated demand, unestimated work, skill demand, schedule coverage |
| Queue | `WorkPlanService.QueueAsync` / `UnscheduledTeamAsync` | The definitions of *open*, *holder*, *unscheduled*, *remaining effort* (reused verbatim; the queue itself is capped at 100 items and 14 days, so the forecast reads the tables directly) |
| Skills | `skills`, `staff_skills` (presence only; level is never consulted anywhere) | Skill-aware capacity |
| Execution | `ticket_time_entries`, `work_sessions` | Historical actual time (through Phase 7) |
| Analytics | `WorkforceAnalyticsService`: `LoadAsync` (one load, fixed query count) + `Tallies.Over` (one pass) | Every historical figure: trends, comparisons, estimate variance, completed work |
| Tickets | `Ticket` (origin, connection, client, priority, category, due date, pause, holder, team) | Demand attribution, filters, quality signals |
| Recurring | `recurring_tickets` + pure `RecurrenceSchedule.Next` | Expected occurrences in a window (count only) |
| Integration | `psa_connections`, `Ticket.SyncStatus`, `TicketTimeEntry.SyncStatus`, `user_psa_identities` | Mapping and integration health |
| Export | `TechnicianReportRenderer.Cell` (formula-safe CSV), Phase 7 export + audit | Report export |
| Access | `WorkforceAccess` (scope), `ITicketScopeQuery` (ticket visibility), `IEffectivePermissionService` | Every query |

### Existing forecasting-related capability

- The planning queue already computes demand against free time for a group over at most 14 days,
  with a shortage figure. It lists only work with **no** plan ahead, at most 100 items.
- Phase 7's *capacity against demand* panel compares confirmed and tentative allocations with
  capacity for any period, future ones included.
- Neither includes the unallocated remainder of partly planned work, neither goes past 14 days with
  unscheduled demand, and neither is skill aware. Phase 8 adds those on the same definitions.

## 3. Data that actually exists (production, 5 Oct 2026, read-only)

| Area | Production today | Consequence for Phase 8 |
|---|---|---|
| Tickets | 151 (150 PSA, 1 internal); 31 open, 120 finished; 6 finished without a completion date | Counts work; the "no completion date" note stays |
| Due dates | `SlaDueAt` on 151 of 151 | Due-date figures have full coverage |
| Statuses, priorities | All inside the normalized sets | Mapping health reads 100 % |
| Recorded time | 17 entries (16 synced, 1 failed push), 19 Aug – 1 Oct | Trends are thin but real |
| Estimates | 0 planning rows | Every open item is **unestimated**; the forecast must say so rather than show 0 h as "no demand" |
| Plans, clocks | 0 allocations, 0 sessions | Confirmed and tentative demand are 0 until the team plans |
| Schedules | 3 of 41 staff | Capacity exists for three people; the rest are "no working schedule" |
| Teams, skills | 0 teams, 0 skills | Team and skill tables are empty states, not errors |
| Holders | 2 tickets held in the portal, 40 under a PSA login only | Most open work is held by nobody the forecast can attribute to; reported as a data-quality fact |
| Reopens, ratings, reviews, first-response promises | 0, 0, 0, 0 | Those signals read **Not available** today |
| Recurring tickets | 0 | The recurring section is an empty state |
| Connections | ConnectWise healthy (synced today); Autotask degraded, last success 23 Sep, error present | Integration health shows a real stale connection |

The forecast is correct on this data and nearly empty; its data-quality section is what management
can act on first. That is by design: the phase reports what the records support.

## 4. Availability of each optional signal (no data, no metric)

| Signal | Explicit source | Written for PSA tickets | Written for internal tickets | Decision |
|---|---|---|---|---|
| Resolved by its due date | `SlaDueAt`, `ResolvedAt ?? ClosedAt` | Yes: the provider's target, or a plain due date when it has none; dates can be missing when closed tickets are not imported | Yes: SLA plan, topic or a typed date | **IMPLEMENT**, named "Resolved by its due date", never "SLA compliance"; data-quality label from coverage |
| Open work past or near its due date | `SlaDueAt`, `SlaPausedAt` | Yes | Yes | **IMPLEMENT** (current state; paused work is not late, the boards' rule) |
| First response promise kept | `FirstResponseDueAt`, `FirstRespondedAt` | Never promised; a reply made in the PSA is never seen | Yes, with an SLA plan | **IMPLEMENT** with its sample; reads Not available where nothing was promised |
| Reopened | `ReopenCount` (one writer: the portal's status change) | Only when reopened through the portal | Yes | **IMPLEMENT**, labelled Partial whenever PSA work is in the set |
| Client satisfaction | `ticket_satisfaction` (client portal only) | Yes, where the client answered | No | **IMPLEMENT** with its sample size |
| Passed review first time | `ReviewedAt`, `ReviewSendBacks` | No | Boards and topics that require review | **IMPLEMENT** with its sample |
| Escalation | None. `escalation_levels` is a per-client contact list; `ticket_assignments` is a reassignment with a note, with no tier or direction | — | — | **DEFER**: no explicit escalation event exists. Reassignment is not escalation and is not presented as one |
| Repeat issue | `ticket_links` are manual, and "duplicate" is not "repeat" | — | — | **DEFER** |
| Status history | Audit rows for portal changes only | — | — | **NOT REQUIRED** by any Phase 8 figure |
| SLA pause history | The original due date is overwritten on resume | — | — | **DEFER**: time paused cannot be reconstructed |
| Complexity, profitability, billable revenue | No cost, contract or revenue data | — | — | **DEFER**: time alone is not profitability |
| Recurring work effort | `recurring_tickets` has no estimate; a raised ticket gets no planning row | — | — | Occurrence **count** only (IMPLEMENT); hours DEFER |

Every implemented signal is shown with its numerator, its denominator, the population it was drawn
from and a data-quality label (§9). The deferred ones appear in the framework as "Not available"
with the reason, so absence is stated instead of hidden.

## 5. Definitions reused verbatim

| Term | Definition (existing code) |
|---|---|
| Open | `TicketStatusRules.Open()`: the status contains neither RESOLV nor CLOSED |
| Holder | `Ticket.AssignedAppUserId`. Work routed to a team is `Ticket.AssignedTeamId`, independent of the holder |
| Unscheduled (a work item) | Open, with no Planned or Tentative allocation ending after now (anyone's) |
| Estimate | `WorkPlanning.RequiredMinutes`; a missing row or null is "no estimate" (zero cannot be stored) |
| Person-day | The shift date, as capacity and Phase 7 count it |
| Period boundaries | Whole dates in the organization's zone |
| Work the caller cannot open | Counts for its time; gives no reference, title, client, priority, status or due date; a filter on those matches only tickets the caller may open (Phase 7) |
| Overdue | Open, `SlaDueAt` < now and the SLA clock not paused (the boards' rule) |
| Due soon | Within `TicketStatusRules.DueSoonHours` (8) |

## 6. Forecast: definitions and formulas

**Window.** Whole dates in the organization's zone, from today forward: `next-7` (today and six
more days), `next-14`, `next-30`, `this-week` (today to Sunday), `next-week` (Monday to Sunday),
`this-month` (today to the last day), or `custom` (start today or later, at most 62 days, within a
year). **Today is counted from now**: capacity is the working time still ahead today, and planned
work is the part of today's allocations still ahead. A forecast opened at 17:00 does not offer the
morning.

**People.** Active staff the caller's `schedule.view` scope reaches, narrowed by team, department
and technician. Capacity is counted for people offered for planned work, as everywhere else.

| Figure | Formula | Source |
|---|---|---|
| Effective capacity | Σ usable minutes per person-day in the window (today: from now) | Phase 2 engine |
| Confirmed demand | Σ minutes of Planned allocations whose shift date is in the window (today: the part after now) | `work_allocations` |
| Tentative demand | The same for Tentative allocations. Never added to confirmed in a single figure | `work_allocations` |
| Remaining unallocated effort of a work item | `max(0, RequiredMinutes − Σ Planned minutes − Σ Tentative minutes)`, all time, cancelled excluded | `work_planning`, `work_allocations` |
| Estimated unscheduled demand | Σ remaining unallocated effort over **eligible** open estimated work | derived |
| Unestimated work | Count of open work items with no estimate and no plan ahead. Shown as a count, never as hours | derived |
| Projected demand | Confirmed + Tentative + Estimated unscheduled | derived |
| Confirmed remaining | Capacity − Confirmed | derived |
| Capacity gap | Capacity − Projected. Negative = potential shortage; positive = potential remaining capacity, never "underperformance" | derived |
| Projected load % | Projected ÷ Capacity × 100; N/A when capacity is 0 | derived |
| Schedule coverage | Σ min(RequiredMinutes, allocated) ÷ Σ RequiredMinutes over open estimated work; N/A when nothing is estimated | derived |

**Eligible work** is open work held by a person in scope, or held by nobody and routed to a team
in scope, whose planning window allows it to start by the window's last day (`EarliestStart` is
empty or not later). Work routed to a team with no holder is shown as its own row ("Not yet
assigned"), counted in its team and in the totals, and in no person's row.

**Which teams' unheld work.** Every team for a caller whose `schedule.view` reaches everyone; the
caller's **own** teams for a narrower scope (never the other teams a colleague happens to belong
to); none for someone who sees only themselves. A question about one person or one department is
about held work only, because unheld work has neither.

**Work filters.** A client, source or priority filter narrows the *work*. Capacity, the gap,
projected load, whether work fits before its due date and a skill's free time all depend on **all**
of a person's work, so under such a filter they are not stated (N/A, with a note) rather than
computed from a slice of it. (Found in review: the first build computed them from the filtered
allocations, which made a person look free while booked on other work.)

**Team rows** are the teams the caller's scope reaches (the same set as the unheld work below),
never the other teams a colleague belongs to.

**Why tentative is subtracted from remaining effort here** although the planning queue's own
*remaining* ignores it: pencilled-in work is already counted as tentative demand. Subtracting only
confirmed work would count those minutes twice in the projection. The queue's figure is unchanged.

**A known limit of the estimate model, stated rather than hidden.** Remaining effort is "estimated
minus allocated", as Phase 5 defined it, not "estimated minus worked". A planned hour that passed
without work still counts as allocated. The planning queue has the same rule; changing it is a
Phase 5 decision, not a reporting one.

**Per day.** Capacity, confirmed and tentative are per shift date. Unscheduled effort has no date of
its own; it is shown on the day its work is **due** (organization zone) when that day is in the
window. Work already past its due date **by the clock** (the overdue list's own test) is its own
figure, and the rest is "no due date in this window". It is never spread across days by guesswork,
and effort on work the caller cannot open is put on no day.

**Where the forecast meets existing figures**

| Existing figure | Relation |
|---|---|
| Phase 7 capacity against demand (confirmed, tentative, capacity) | Equal for a window that starts tomorrow or later; differs only by "today from now" |
| Planning queue demand | Queue demand ≤ forecast unscheduled demand: the queue lists only work with no plan ahead (first 100), the forecast adds the unallocated remainder of partly planned work and subtracts tentative minutes |

### Fixtures every implementation must reproduce

| | Fixture A (§81) | Fixture B (§82) |
|---|---|---|
| Capacity | 100 h | 100 h |
| Confirmed | 60 h | 80 h |
| Tentative | 10 h | 10 h |
| Estimated unscheduled | 20 h | 25 h |
| Unestimated | 5 items | — |
| **Confirmed remaining** | **40 h** | 20 h |
| **Projected demand** | **90 h** | **115 h** |
| **Capacity gap** | **+10 h** | **−15 h** |
| Attention list | Watch: "projected demand takes 90% of capacity; 10 h is left" (the 90 % threshold, reached exactly) | Attention: "projected demand is 15 h over capacity" |

## 7. Team, technician, client, source and skill views

- **Team forecast**: the figures of §6 per team of the people in scope. A person in two teams counts
  in both (said on screen); the organization total counts them once. Nothing recommends moving
  people between teams.
- **Technician forecast and workload balance**: the same per person, with projected load %. Name
  order; the reader sorts. No label, no rank: over 100 % is a scheduling condition.
- **Demand by client and by source**: confirmed, tentative, estimated unscheduled, unestimated and
  open items in the window, by the ticket's client and source (each PSA connection by name, Team
  boards, Monitoring: the normalized origin, nothing provider specific). Work the caller cannot open
  is one row.
- **Skill capacity**, only for skills that some open estimated work **requires**
  (`WorkPlanning.RequiredSkillId`). Skills are never inferred from ticket history.

| Figure | Formula |
|---|---|
| Skill demand | Σ remaining unallocated effort of eligible work requiring the skill |
| Skilled capacity | Σ over people in scope who hold the skill of their projected free time in the window: Σ per day max(0, capacity − confirmed − tentative) |
| Skill gap | Skilled capacity − skill demand |

  A holder is anyone with the skill at any level, because nothing in the product uses the level
  (the conflict check is presence only). A person holding two skills is counted under both; skill
  rows therefore do not add up, and the screen says so.
- **Recurring work**: active recurring tickets and how many times each is scheduled to be raised in
  the window (`RecurrenceSchedule.Next`, the organization's zone). Shown as counts with the caveat
  that an occurrence is skipped while the previous one is open, and that occurrences carry no
  estimate. Visible to callers who may manage boards (`boards.manage`), the existing gate for the
  recurring list.

## 8. History: trends, comparisons, estimate variance

All from **one** Phase 7 load over the range needed, tallied per sub-period in memory. No new
definition of actual, planned, reactive or completed.

- **Comparison**: a period against the one before it of the same length: last 7 days, last 30 days
  (default), last week, last month (against the calendar month before). For actual time, planned
  actual, reactive time, reactive share, completed work and work items: current, previous, absolute
  change, percentage change. **Percentage change is N/A when the previous value is 0**, and both
  values are always shown, so "+100 %" from 1 to 2 can never hide its denominator.
- **Weekly trend**: the last eight weeks (Monday to Sunday), the current one marked partial: actual,
  planned actual, reactive, reactive share, completed.
- **Client demand** and **source trend**: actual time, current against previous, with reactive time
  and completed work; increases are not called negative.
- **Estimate variance** by category, by client and by source, over the planned ticket-days of the
  current period: planned, time recorded on that planned work, signed variance and %, absolute
  estimate variance % and the number of ticket-days compared. Variance is never attributed to a
  technician.

**Category** is `Ticket.PortalCategory`: the mapped category, or the PSA's own label where no rule
maps it. It is free text, not a closed set, and is shown as stored. Monitoring tickets keep a device
name in that field, which is not a kind of work, so they are grouped as "Monitoring alerts". No
classification is derived from titles or text.

## 9. Quality signals and data-quality labels

Computed over the **work completed in the period** and credited to the people in scope: the same set
as Phase 7's Completed card, so the population reconciles with it. Aggregate only (organization,
team, client or source through the filters). **There is no per-technician quality table**, by
design: these are properties of work and data, not grades of people.

| Signal | Numerator ÷ denominator | Data-quality rule |
|---|---|---|
| Resolved by its due date | finished at or before `SlaDueAt` ÷ completed work that has a due date | **High** when ≥ 90 % of the completed work has a due date, **Partial** when some has, **Not available** when none |
| First response promise kept | `FirstRespondedAt ≤ FirstResponseDueAt` ÷ completed work that carried a promise | **Not available** when nothing carried a promise; **Partial** otherwise (PSA work carries none; only portal replies are seen) |
| Reopened | `ReopenCount > 0` ÷ completed work | **Partial** when the set contains PSA work (a reopen made in the PSA is not seen), **High** when it is internal only, **Not available** when the set is empty |
| Client satisfaction | ratings of 4 or 5 ÷ rated work | **Not available** with no rating; **Partial** below 50 % of completed client work rated; **High** from there |
| Passed review first time | approved with no send-back ÷ reviewed work | **Not available** with nothing reviewed; **Partial** otherwise (only boards and topics that require review) |
| Escalated work | — | **Not available**: no escalation is recorded anywhere |
| Repeat issues | — | **Not available**: links are manual and "duplicate" is not "repeat" |

Definitions match the productivity dashboard's, so the two cannot disagree about a ticket. The label
and its one-line reason travel with the number in the API, the screen and the reports.

**Enforced, not only intended.** When the people in scope are exactly one person and that person is
not the caller (a technician filter, or a team of one), every signal is returned Not available with
the reason, and the estimate-variance breakdown is empty. The two reports built on them take no
technician filter. A person's own view of their own completed work stays available to them.
(Found in review: the first build let a manager read one named person's signals through the
technician filter, which is exactly the table this section says does not exist.)

## 10. Operational attention

A list of statements, each produced by one fixed rule over figures already on the page. Each carries
its severity, the rule, the contributing numbers and where to look. Nothing is called an "AI
insight".

| Rule | Condition | Severity |
|---|---|---|
| Capacity shortage (organization, and each team in scope on the same thresholds) | Projected load ≥ 90 % · > 100 % · > 120 %; demand against no capacity at all is Attention | Watch · Attention · Critical |
| People over capacity | Number of people with projected load > 100 % | Attention |
| No capacity for demand | Demand attributed to people with no capacity in the window | Attention |
| Estimated work not scheduled | Estimated unscheduled demand > 0 | Watch |
| Unestimated work | Unestimated open items > 0 | Info, or Watch from 10 items |
| Skill gap | Skill demand > skilled capacity | Attention |
| Work at risk before its due date | Open due work whose remaining effort exceeds the holder's free time on the days up to the due date | Attention |
| Overdue work | Open work past its due date, not paused | Attention |
| Reactive share changed | Reactive share moved by ≥ 5 percentage points against the previous period, both values shown | Watch |
| Time not in the PSA | Portal time entries whose push failed | Watch |
| Stale or failing sync | A connection with no successful sync for 24 h, or in error (callers with `integration.health.view`) | Attention |

Thresholds are constants in one place (`InsightThresholds`). They are fixed in this phase;
making them configurable per organization waits for a business need, and would be audited.

"Free time on the days up to the due date" is the planning queue's existing rule for *not enough
capacity before the due date*, applied to all due work instead of the queue's first 100 items. It
is evaluated for work due **within the window**, and only when **the window starts today** and no
work filter is on: the holder's free time between now and the due date must all be inside what the
read knows. Otherwise nothing is claimed and a note says so (work already past its due date is
listed as overdue whatever the window).

## 11. Data quality, mapping health, integration health

- **Planning data quality** (everyone who sees the forecast): open work estimated against
  unestimated; open work with no holder in the portal; people with no working schedule; people not
  offered for planned work; estimated work that names a required skill; finished work with no
  completion date.
- **Mapping and integration health** (callers with the existing `integration.health.view`), per
  connection, computed from data at rest:

| Figure | How |
|---|---|
| State | `PsaConnection.Status`, enabled, last successful sync, last health check, whether an error is recorded (the text stays on the Integration health page), stale after 24 h |
| Status mapping | Share of the connection's tickets whose portal status is one of NEW, IN_PROGRESS, WAITING_CUSTOMER, ON_HOLD, RESOLVED, CLOSED; up to five unmapped values listed |
| Priority mapping | The same for CRITICAL, URGENT, HIGH, NORMAL, MEDIUM, LOW |
| Technician links | Distinct PSA logins holding the connection's tickets (not the integration account) that are linked to a portal user, against all such logins |
| Client mapping | Tickets pointing at a placeholder company created when the PSA sent none |
| Failed records | Tickets in sync error; time entries whose push failed or is pending |

  `PsaStatus == PortalStatus` is **not** used as the test: a pass-through mapping rule produces the
  same and would read as unmapped. No credentials, endpoints or error texts are returned.
- Two defects in the existing `api/admin/health` snapshot are noted and **left alone** (out of
  scope): job counts are organization-wide but repeated on each connection, and its failed-event
  count reads a column nothing writes.

## 12. Reports

A **report** is a definition (key, category, title, the filters it takes) and one builder that
returns columns and rows from the Phase 7 and Phase 8 services. Preview and export call the same
builder with the same filters, so an exported total cannot differ from the preview.

| Category | Report | Source |
|---|---|---|
| Workforce | Workforce utilization | Phase 7 people |
| Workforce | Technician work summary | Phase 7 people |
| Workforce | Team work summary | Phase 7 teams |
| Capacity | Capacity and demand (by day) | §6 |
| Capacity | Future capacity (by technician) | §6 |
| Clients | Client workload | Phase 7 clients + comparison |
| Delivery | Planned against actual (by day) | Phase 7 daily |
| Delivery | Reactive work (by week) | §8 |
| Delivery | Estimate variance (by category, client, source) | §8 |
| Delivery | Work sources | Phase 7 sources + comparison |
| Quality | Operational quality | §9 |
| Integrations | Mapping and integration health | §11 (needs `integration.health.view`) |

- **Preview** shows the applied filters, the period and zone, the generation time, PSA sync
  freshness, a summary, the table (first 500 rows, with the full count) and the notes on what the
  numbers cover.
- **Export**: CSV (the existing formula-safe cell, UTF-8 with a byte-order mark) and **XLSX**.
- **XLSX without a new dependency.** The repository has no spreadsheet library. A workbook is a zip
  of a few XML parts; a small writer on `System.IO.Compression` (part of .NET) produces one sheet
  with typed cells. Text is written as inline strings, which a spreadsheet never evaluates, so a
  value beginning `=` is text by construction; numbers are numeric cells. ClosedXML was considered
  and not added: one more package tree to keep patched for a single flat table.
- **PDF** is deferred. MigraDoc is in the project, but a professional layout for twelve reports is
  its own piece of work, and CSV and XLSX cover the data need.

## 13. Reporting, export, aggregation and job infrastructure as found

| Infrastructure | State | Decision |
|---|---|---|
| CSV | `TechnicianReportRenderer.Cell` neutralises formulas, tabs and carriage returns | REUSE |
| Phase 7 export | Its own permission, audited | REUSE (same permission and audit action family) |
| Scheduled staff reports | Recipients are any syntactically valid address (not limited to staff); the creator's permission is **not** re-checked when a run fires; files kept in the database with no retention | Does not meet §49–50 of this phase. **Scheduled workforce reports are DEFERRED.** The design they need is recorded in §17; the two gaps are flagged for a separate hardening change |
| Background jobs | A placeholder queue with one logging handler and no result or file storage | NOT REQUIRED: reports are generated in the request, inside the limits of §15. A job-based export waits for a measured need |
| Aggregation, caching | None in the product; Phase 7 measured raw reads as fast enough | NOT REQUIRED. Nothing is cached, so there is no cache key to get wrong and no cross-tenant cache to leak |
| Saved views | Filters live in the address, so a bookmarked or shared internal link *is* a saved view | DEFER a stored-view table: it adds an id to authorize for little gain |
| Dashboard customization | — | NOT REQUIRED: this is an operational page, not a dashboard builder |

## 14. Permission model

No new permission key.

| Capability | Permission | Notes |
|---|---|---|
| Management insights, forecast, trends, quality signals | `schedule.view` | The people are the ones the scope reaches. At Own, a person gets their own forecast and nobody else's |
| Report catalogue and preview | `schedule.view` | Same scope |
| Report export (CSV, XLSX) | `workforce.analytics.export` | Phase 7's key; required by the controller and checked again in the service; audited as `workforce.report.exported` |
| Mapping and integration health; the sync attention rule | `integration.health.view` | Existing key; Manager and Administrator hold it |
| Recurring work in the forecast | `boards.manage` | The existing gate for the recurring list |

Conceptual names in the brief map onto these: *insights.self / team / management* are the scopes of
`schedule.view`; *reports.view / create* are preview; *reports.export* is the export key;
*reports.schedule / manage* are deferred with scheduling.

Clients: every route is under `api/workforce`, whose controllers require `schedule.view` and a staff
user id. No client role or client login holds either key, and the existing test that walks every
`api/workforce` controller covers the two new ones automatically.

## 15. Security and performance risks, and how each is closed

| Risk | Control |
|---|---|
| Widening scope through a filter (team, person, client, connection, skill, report key) | Every id validated against tenant and scope as in Phase 7; an unknown report key is "not found"; a person outside the scope is "Person was not found." |
| A hidden ticket identified through a filter or a list | Phase 7's rule reused for every new list |
| Cross-tenant data | Tenant filter on every table; staff narrowed by organization; tested with a second organization and its own database context |
| Report or export enumeration | Reports are definitions in code, not stored rows: there is no saved-report, generated-report or export-job id to forge. Recorded so the IDOR tests in the brief are answered by absence, and tested for the report key |
| CSV or XLSX injection | Formula-safe CSV cell; XLSX text as inline strings; tested with `=HYPERLINK(...)` |
| Expensive requests | Forecast ≤ 62 days; history ≤ 366 days (Phase 7); ≤ 1,000 people; lists paged (≤ 200); preview ≤ 500 rows; export ≤ 50,000 rows; the API's per-user and per-organization rate limits |
| Secrets in health | No credential, endpoint or error text returned |
| A forecast that looks more certain than it is | Unestimated work is a count, never hours; every component of projected demand is shown; data-quality labels on optional signals |
| Query cost | One Phase 7 load per request plus a fixed handful of queries for open work, estimates and skills; pinned in a test at 50, 100 and 500 people and run on PostgreSQL |

## 16. Classification of every proposed feature

| Feature | Class |
|---|---|
| Management insights page | IMPLEMENT |
| Future effective capacity | REUSE (Phase 2 engine), EXTEND (today from now) |
| Confirmed and tentative demand | REUSE (Phase 7 load) |
| Estimated unscheduled demand, unestimated work, projected demand, capacity gap | IMPLEMENT |
| Capacity forecast by day | IMPLEMENT |
| Forecast windows | IMPLEMENT (six presets and custom) |
| Team and technician forecast, workload balance | IMPLEMENT |
| Skill-aware capacity and bottlenecks | IMPLEMENT (only for skills that work requires) |
| Reactive trend, planned against reactive | EXTEND (Phase 7 tallies per week) |
| Client demand trend and capacity impact | EXTEND |
| Client profitability | DEFER (no financial data) |
| Work source trend | EXTEND |
| Estimate variance trend, by category, client, source | EXTEND (adds category and signed variance on planned work) |
| Quality-signal framework and data-quality badge | IMPLEMENT |
| Due-date and first-response signals | IMPLEMENT |
| Work at risk before its due date | IMPLEMENT |
| Reopen signal | IMPLEMENT (portal reopens; labelled Partial with PSA work) |
| Escalation signal | DEFER (no data) |
| Repeat-issue signal | DEFER |
| Operational attention panel, severity, explanation | IMPLEMENT |
| Historical comparison and trend calculation | IMPLEMENT |
| Management reports and report center | IMPLEMENT (twelve reports) |
| Report filters and preview | IMPLEMENT |
| Export: CSV | REUSE |
| Export: XLSX | IMPLEMENT (no dependency) |
| Export: PDF | DEFER |
| Scheduled reports | DEFER (existing scheduler does not meet the recipient and re-authorization rules) |
| Management summary | IMPLEMENT (the cards and the attention list are the summary) |
| Executive and technical views | REUSE: one page, summary first, detail below, drill-down to records. A second engine is NOT REQUIRED |
| Saved and shared views | DEFER (the address carries the filters) |
| Dashboard configuration | NOT REQUIRED |
| A single productivity score, ranks, grades | NOT REQUIRED and not built |
| Schedule coverage, unestimated work | IMPLEMENT |
| Data-quality section | IMPLEMENT |
| PSA mapping health, integration health | IMPLEMENT (from data at rest) |
| Provider-neutral architecture | REUSE (normalized origin and connection; no provider conditions) |
| RMM connectors | NOT REQUIRED in this phase |
| Aggregation tables, caching | NOT REQUIRED at measured volumes |
| Background export jobs | NOT REQUIRED |
| Statistical or machine-learned forecasting | DEFER |
| New permission keys | NOT REQUIRED |
| Database changes | None |

## 17. What scheduled workforce reports would need (recorded, not built)

A definition (report key, filters, format, frequency, hour and zone, enabled), internal recipients
only (staff accounts of the same organization, chosen from a list, never free text), and before
**every** run: the creator still active, still holding `schedule.view` and the export key, the
filters still inside the creator's scope, each recipient still an active staff account; any failure
skips the run and is audited. Files kept with a retention period. None of this exists in the
current scheduler, which is why the feature is not added on top of it.

## 18. API (all GET, under `api/workforce`, staff only, behind the Workforce switch)

| Route | Permission | Answer |
|---|---|---|
| `insights/forecast?window&from&to&teamId&departmentId&appUserId&clientId&source&priority` | schedule.view | Totals, coverage, data quality, per day, teams, people, clients, sources, skills, recurring, attention, notes |
| `insights/forecast/work?list=…&skip&take&…` | schedule.view | The records behind a forecast figure: confirmed, tentative, unscheduled, unestimated, at-risk, overdue, skill:{id} |
| `insights/trends?compare&…` | schedule.view | Comparison, eight weeks, clients, sources, estimate variance, quality signals, attention |
| `insights/health` | + integration.health.view | Connections with mapping and failed-record figures |
| `reports` | schedule.view | The catalogue the caller may open |
| `reports/{key}?…` | schedule.view | Preview |
| `reports/{key}/export?format=csv\|xlsx&…` | workforce.analytics.export | The file |

## 19. Code plan

| Piece | Where |
|---|---|
| Contracts | `packages/application/Workforce/IWorkforceInsights.cs` |
| Forecast, trends, health | `WorkforceAnalyticsService.Insights.cs` (a partial of the Phase 7 service, so it uses the same private load and tallies rather than a copy) |
| Reports | `WorkforceAnalyticsService.Reports.cs` |
| XLSX writer | `packages/infrastructure/Reporting/XlsxWriter.cs` |
| Phase 7 extensions | tallies over a sub-range; ticket category; quality fields on completed work; capacity from now; future plans on finished work left out, as capacity already does |
| Controllers | `WorkforceInsightsController`, `WorkforceReportsController` |
| Web | `WorkforceInsights.tsx`, `WorkforceReports.tsx`; pages `workforce/insights`, `workforce/reports`, `workforce/reports/[key]`; two menu entries |
| Tests | `WorkforceInsightsTests.cs` (fixtures A and B, teams, people, skills, coverage, trends, comparison, quality availability, health, scope, tenant, zones), performance pins, authorization rows, `e2e/workforce-insights.spec.ts` |
| Docs | `insights.md`, `reports.md`, and the README, architecture, permissions, testing, troubleshooting and user guide updates |

No migration. No new dependency, backend or web.
