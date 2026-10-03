# Advanced work planning (Phase 5)

Tentative work, what a piece of work needs (effort, window, splittable, skill), the planning queue
with why each item waits, effort-in-a-window planning with a preview you confirm, due-date
awareness and the full conflict taxonomy. Everything here is **internal only**: no route, shape or
screen in this phase is reachable by a client account, and a ticket a client can see tells them
nothing about any of it ([permissions-and-security.md](permissions-and-security.md)).

Planning never changes a due date. A due date is the ticket's own (`SlaDueAt`, from the PSA or the
board); planning reads it to warn and to order the queue, and writes nothing back.

- [Overview](#overview)
- [Tentative and confirmed work](#tentative-and-confirmed-work)
- [What the work needs: the planning requirement](#what-the-work-needs-the-planning-requirement)
- [The planning queue](#the-planning-queue)
- [Effort in a window: preview, then confirm](#effort-in-a-window-preview-then-confirm)
- [Stale plan protection](#stale-plan-protection)
- [Due dates](#due-dates)
- [Conflict taxonomy and override policy](#conflict-taxonomy-and-override-policy)
- [Over-capacity and shortage](#over-capacity-and-shortage)
- [Screens](#screens)
- [API](#api)
- [Storage](#storage)
- [Permissions, client isolation, tenant isolation](#permissions-client-isolation-tenant-isolation)
- [Concurrency](#concurrency)
- [Audit and notifications](#audit-and-notifications)
- [Performance](#performance)
- [Deferred](#deferred)
- [Troubleshooting](#troubleshooting)

## Overview

The Phase 3 picture ([planned-work.md](planned-work.md#overview)) with what this phase adds: the
queue in front of planning, and the two states a placed piece of work can be in.

```
   PIO internal      Autotask      ConnectWise      (future PSA / RMM)
        |                |              |                  |
        +----------------+--------------+------------------+
                                v
                      Unscheduled work  --->  Planning queue
                                |              why it waits, DUE TODAY / TOMORROW /
                                |              OVERDUE, what it needs, the shortage
                   +------------+------------+
                   v                         v
             SELF PLANNING            MANAGER PLANNING
             own work,                schedule.manage at Team,
             schedule.manage at Own   Department or All scope
                   |                         |
                   +------------+------------+
                                v
                 Capacity and conflict check, under the person's gate
                 (a preview computes the same thing and writes nothing)
                                v
                         Work allocation
                   +------------+------------+
                   v                         v
              CONFIRMED                 TENTATIVE
           takes capacity         projected only, takes none
                   ^                         |
                   +------ confirm, checked --+
                           again as confirmed
                   |
          each one is also FIXED or FLEXIBLE
          (who may move it; unchanged by confirming)
                                v
            My plan  .  Team scheduler  .  the ticket's Planned work
                                v
                   My day, actual time (Phase 6, not built)
```

Three things the picture leaves out, each covered below:

- **Fixed and flexible are not a third state.** They say who may move a piece of work
  ([planned-work.md](planned-work.md#fixed-and-flexible)); tentative and confirmed say whether it
  takes capacity. Every allocation is one of each, and confirming changes neither.
- **Confirmed is the usual path, tentative the detour.** Most work is confirmed at placement. Work
  pencilled in is checked with the tentative rules (overridable conflicts become warnings), and
  confirming it later runs every check again as committed work, under the gate, so what fitted when
  it was pencilled in may be refused then ([Tentative and confirmed work](#tentative-and-confirmed-work)).
- **Every screen reads both states.** The scheduler, My plan and the ticket panel show tentative
  work dashed and marked; the capacity figures keep it out of confirmed and remaining and show it as
  projected. The queue is the way in: Schedule, Plan with a window and Find technician all start
  from a row of it ([The planning queue](#the-planning-queue), [Effort in a window](#effort-in-a-window-preview-then-confirm)).

## Tentative and confirmed work

A work allocation now has three live states and one end state:

```
Tentative (2) ──confirm (checked again as committed work)──▶ Planned (1)
Planned (1)   ──pencil in (schedulers only)──────────────────▶ Tentative (2)
Tentative (2) / Planned (1) ──cancelled, or released when the ticket finished──▶ Cancelled (5)
```

| | Planned (confirmed) | Tentative (pencilled in) |
|---|---|---|
| Takes confirmed capacity | yes | **no** |
| Shown in the plan and on the board | yes | yes, dashed, marked *Tentative* |
| Counts in `ConfirmedMinutes` / `RemainingConfirmedMinutes` | yes | no |
| Counts in `TentativeMinutes` / `ProjectedRemainingMinutes` ("free if confirmed") | no | yes |
| Keeps the ticket off the unscheduled list and the queue | yes | yes |
| A clash with it, for someone else's confirmed work | `HardConflict` (overridable) | `TentativeConflict` (warning) |
| Conflicts when it is placed | the confirmed rules | overridable types become **warnings**; blocks stay blocks |
| Counts towards the requirement's allocated effort | yes (`ConfirmedMinutes`) | shown separately (`TentativeMinutes`); `RemainingMinutes` ignores it |
| Released by the worker when the ticket finishes | yes | yes |

The capacity engine already kept the two apart (Phase 2, [capacity.md](capacity.md)); Phase 5 lets
work actually be placed as tentative. `WorkAllocationReader` now feeds the engine both statuses with
`Confirmed = Status == Planned`.

**Placing tentative work.** Every write that places work takes `tentative` (`POST api/workforce/plan`,
the plan dialog's *Pencil in*, the preview confirmation). The same checks run, under the same gate,
with the tentative rules: a clash with confirmed work or a break is a warning kept in the audit
entry, not a refusal. The person is told "Work pencilled in for you".

**Confirming.** `POST api/workforce/plan/{id}/confirm` with `{ version, overrideReason? }`. Who may:
whoever may change the work (`canEdit`: the person themselves for flexible work or their own
self-planned work, or someone whose `schedule.manage` reaches them); fixed work scheduled for someone
is confirmed by whoever planned it ("This work was scheduled for you and is fixed in place. Ask
whoever planned it to confirm it."). Inside the gate the row is reloaded and **everything is checked
again as committed work**: what fitted when it was pencilled in may not fit now. A clash is the usual
409 with the conflicts, overridable with a reason by someone who holds `schedule.override`; nothing is
confirmed blindly. The version increments; the audit entry is `workforce.allocation.confirmed`.
Confirming work that is not tentative: "This work is not pencilled in."; on a finished ticket:
"This ticket is finished; there is nothing left to plan." (the worker releases it anyway). The DTO's
`canConfirm` says whether the asker may confirm this row, and is false on a finished ticket.

**Pencilling committed work back in.** `POST api/workforce/plan/{id}/tentative` with `{ version }`.
Committed work is a promise to the plan, so only someone who schedules others may take it back
("Only someone who schedules others can pencil committed work back in."); the person it is planned
for cannot. No conflict check is needed (it stops taking capacity). Audit `workforce.allocation.made_tentative`.
Committed work that is not committed: "This work is not committed."

## What the work needs: the planning requirement

The ticket says what the work is; planning needs to know how much effort it takes, when it may be
done and whether it may be split. That lives in **one internal row per ticket**, `work_planning`,
beside the ticket and never on it (`WorkPlanning`): `RequiredMinutes`, `EarliestStart`,
`LatestEnd`, `Splittable`, `RequiredSkillId`, `Note`, who last set it and when. **What is allocated
and what remains are derived** from the allocations every time they are read, never stored:
`ConfirmedMinutes`, `TentativeMinutes`, `RemainingMinutes = max(0, Required − Confirmed)`.

`GET api/workforce/plan/requirements/{ticketId}` reads it (a row with nulls when nobody set one),
for any ticket the caller may see. `PUT` writes it: `schedule.manage`, and the caller holds the
ticket, is in the team it sits with, or schedules others (`schedule.manage` wider than Own);
otherwise "Only whoever holds this work, their team, or someone who schedules others can say what
it needs." A finished ticket takes no requirement ("This ticket is finished; there is nothing left
to plan."). A ticket outside the caller's view is "Ticket was not found.", the same as a ticket in
another organization. The row has no version token: the last write wins, and the audit entry keeps
before and after. Validation:

| Rule | Message |
|---|---|
| 5 ≤ effort ≤ 6,000 minutes (100 hours), whole five-minute steps, or null | "Effort is between 5 minutes and 100 hours, in whole five-minute steps." |
| The window ends after it starts (when both are given) | "The window must end after it starts." |
| The window is at most 31 days | "The planning window is at most 31 days." |
| The skill exists in the organization's catalogue | "That skill is not in the skill catalogue." |
| Note ≤ 300 characters | the usual text rule |

A requirement is a planning aid, not a lock: work can still be placed outside its window or on
someone without the skill. The skill it asks for becomes a `SkillWarning` on every placement for a
person who does not hold it (kept in the audit entry), and the queue and the preview dialog show it.
Audit: `workforce.planning.requirement_set` (before/after).

Setting a skill on a ticket itself was considered and not done: a skill is a planning concern, and
the ticket shapes a client receives must stay free of it. The owner's decision on a ticket-level
required skill is still open; the planning row carries it for now.

## The planning queue

`GET api/workforce/plan/queue?teamId&departmentId&skills&matchAll&horizonDays` (horizon 1–14 days,
default 14, from today in the organization's zone; `from` and `to` are accepted and ignored, as the
team scheduler's unscheduled list ignores them). The queue starts from the group's
unscheduled work ([team-scheduler.md](team-scheduler.md#the-unscheduled-queue): open, held by or
routed to the people the caller may see, in nobody's live plan, confirmed **or tentative**) and adds,
per item, what planning knows:

| Field | Meaning |
|---|---|
| `requiredMinutes`, `splittable`, `earliestStart`, `latestEnd`, `requiredSkillName` | from the planning row, null when none |
| `confirmedMinutes`, `tentativeMinutes`, `remainingMinutes` | derived from the allocations |
| `reason` | why it waits (below) |
| `due` | 0 none, 1 **DUE TOMORROW**, 2 **DUE TODAY**, 3 **OVERDUE**, judged in the organization's zone against the ticket's due date |
| `freeBeforeDueMinutes` | the holder's remaining confirmed capacity on the days from today up to and including the due date, when the due date is inside the horizon and not already past; null otherwise (an overdue item is marked OVERDUE and waits for planning; there is no capacity "before" a date that has gone) |
| `ageDays` | whole days since the ticket was raised (the PSA's creation time for a synced ticket, the portal's for its own) |

**Why it waits** is derived, never stored:

| Reason | When |
|---|---|
| `NoTechnicianAssigned` (2) | nobody in the portal holds it (it sits with a team) |
| `InsufficientCapacityBeforeDue` (3) | it has a holder, an estimate and a due date inside the horizon, and the holder's free time before the due date is less than the remaining effort |
| `AwaitingPlanning` (1) | everything else: it has hands, it may fit, nobody has planned it |

A reason a person typed ("waiting for parts") is not modelled: that is a ticket status, not a
planning fact. **Order:** the most urgent first (overdue, then due today, then due tomorrow), then
by due date, the undated last, and older work before newer within the same urgency.

**Demand against capacity**, for the people shown, over the horizon:

- `demandMinutes`: the remaining effort of every item that has an estimate; `itemsWithoutEstimate`
  says how many have none (their demand is unknown, not zero).
- `availableMinutes`: the remaining confirmed capacity of the people offered for planned work over
  the horizon, as the team scheduler counts it (`peopleCounted`).
- `shortageMinutes = max(0, demand − available)`. A fact about the group, not a verdict: it says the
  estimated work does not fit in the time left, and nothing about who should work more.

The queue holds at most 100 items (the unscheduled list's cap, the soonest due first, then the
newest), and demand and shortage are summed over those; a group with more than 100 pieces of
unplanned work sees the figures for the 100 shown. It costs the same number of queries whatever its
size (26 at 50, 100 and 500 people).

## Effort in a window: preview, then confirm

"Four hours on this, some time between Monday morning and Wednesday evening, in one sitting" is a
different question from "place it at 09:00". The answer is computed, shown, reviewed by a person and
only then written. **Nothing persists until the confirmation.**

```
Calculate ──▶ Preview (pieces, what does not fit, warnings, a plan token) ──▶ Human review ──▶ Confirm (checked again under the gate)
```

`GET api/workforce/plan/preview?ticketId&appUserId&earliest&latest&minutes&splittable&tentative&minChunk`
answers a `PlanPreviewDto`: the person and their zone, the window, the effort, `pieces` (start, end,
minutes), `allocatedMinutes`, `unallocatedMinutes`, `warnings`, `freeMinutesInWindow`,
`longestFreeMinutes` and a `planToken`.

**The frame.** The window starts no earlier than now (rounded up to the next quarter hour): time
that has passed is never proposed, and a window that began earlier says so ("The window started
before now; proposing from 3 Oct 14:15."). The person's capacity is read over the window (plus a
day each side for zones), and the free slots and projected-free slots are clipped to it. The preview is computed from the
**confirmed** free time: tentative work does not hold capacity, so the preview may propose time
under it and says so ("… overlaps tentative work.").

**Continuous work** (the default): the earliest free stretch that holds the whole effort, placed from
its start. Two separate free hours are not a two-hour slot. If none is long enough nothing is
proposed and the warning names the longest: "No single free period of 5h in the window; the longest
is 4h."

**Splittable work**: the free stretches in order, each taken in full until the effort is met, skipping
scraps shorter than `minChunk` (default 30, at least 15 minutes) unless a scrap finishes the work. At
most 20 pieces. A break in the middle of the day splits the morning from the afternoon; a day with
work at 09:00–10:00 gives 08:30–09:00 (if ≥ the chunk or it finishes the work), 10:00–12:30,
13:30–… and so on.

**Partial fit** is reported, never silently rounded: when the window holds less than the effort,
what fits is proposed and "Only 7h of 10h fits in the window; 3h remains unallocated." is a
warning. The remainder is the planner's decision: a later window, another person, a smaller
estimate. **No piece is ever over-booked** to make the sum come out.

Other warnings on a preview: the due date ("Ends after the due date (… UTC)."), the skill the work
asks for when the person does not hold it ("… does not hold the skill …" as the evaluator words it).

**Confirming.** `POST api/workforce/plan/preview/confirm` (`schedule.manage`) with the same request,
the pieces as shown, the `planToken`, an optional note and an optional override reason. Before the
gate the pieces are validated: inside the window ("Every piece must lie inside the planning
window."), not overlapping each other ("The pieces overlap each other."), not adding up to more than
the effort ("The pieces add up to more than the required effort."), each at least 5 minutes ("Pieces
are at least 5 minutes."), between 1 and 20 of them. Then,
under the person's gate, the frame is computed again and compared with the token (next section), and
each piece is checked with the full rules and written as its own allocation (confirmed or tentative
as asked), in **one transaction**: either every piece is in the plan or none is. The holder bridge
runs once, on the first piece, exactly as for a single placement, and the person is told once
("Work planned for you: {reference} · 3 pieces, from {when}"), not once per piece. The answer lists the allocations,
`allocatedMinutes` and `remainingMinutes` against the requirement. Audit
`workforce.allocation.plan_confirmed` (the pieces, the token) beside one `created` per piece.

Limits, checked before the person or the ticket is read: effort 5–6,000 minutes ("Effort is between 5 minutes and
100 hours."); pieces at least 15 minutes ("Pieces are at least 15 minutes."); the window ends after
it starts, is at most 14 days ("A preview covers at most 14 days."), ends no earlier than yesterday
("The window is in the past.") and starts within a year ("Work can be planned at most a year
ahead."). Scope: the same as placing work: a technician previews for themselves only (a colleague is
outside what they see: "Person was not found."), and only for work they can see ("Ticket was not
found."). A person not offered for planned work, or inactive, gets no proposal ("This person is not
offered for planned work."): confirming would be blocked anyway.

## Stale plan protection

The `planToken` is a 32-hex-character SHA-256 of exactly what the preview was computed from: the
person, the ticket, the window, the effort and options, every free and projected-free stretch inside
the window and each day's usable / confirmed / tentative minutes. On confirmation the frame is
computed again **inside the gate**; if the token differs, nothing is written and the answer is
**409** `conflict`, detail "The plan changed since the preview. Review the new proposal.", with a
`payload` of `{ stale: true, preview: <a fresh PlanPreviewDto> }`. The screen shows the new proposal
in place with "The plan changed since the preview. This is the new proposal; confirm it again if it
still suits." The same token twice is refused the second time: the plan is no longer what it saw.

This is the same principle as every other write ([conflicts.md](conflicts.md#availability-is-not-a-reservation)):
what a screen showed is not a reservation. Two managers confirming previews for the same person and
time end with one plan, the other told to review (`Two_managers_confirming_previews_for_the_same_time_end_with_one_plan`,
on the in-memory provider through the in-process gate; the PostgreSQL row lock under it is the one
Phase 3 proved in `Two_requests_for_the_same_hour_on_a_real_database_end_with_one_booking`).

## Due dates

Planning reads the ticket's due date and never writes it.

- Every placement, move, confirmation and preview compares the proposed end with the due date. Work
  that would end after it is **placed with a warning**, never refused and never moved: the warning
  is in the 409 payload as a `DueDateRisk` (9) conflict of severity Warning when something else
  refuses, in `warnings` on a preview, and in the audit entry's `warnings` when the work is written.
  The message: "Ends after the due date (4 Oct 2026 17:00 UTC)." Over-capacity before the due date is a
  fact the queue reports (`InsufficientCapacityBeforeDue`); it is not a block.
- The queue marks **DUE TODAY**, **DUE TOMORROW** and **OVERDUE** in the organization's zone, orders
  by urgency and shows the holder's free time before the due date.
- Nothing extends, shortens or sets an SLA or a due date, anywhere in the module.

## Conflict taxonomy and override policy

The full set after Phase 5. Severity: Warning never refuses; Overridable refuses unless the caller
holds `schedule.override` and gives a reason of 5–300 characters; Block refuses everyone.

| Type | When | Confirmed proposal | Tentative proposal |
|---|---|---|---|
| `NotSchedulable` (8) | The person is not offered for planned work, or inactive | **Block** | **Block** |
| `UnavailableConflict` (4) | Overlaps time away, or a day taken off | **Block** | **Block** |
| `HardConflict` (1) | Overlaps existing **confirmed** work | Overridable | Warning |
| `OutsideWorkingWindow` (5) | Outside the working window and any additional availability | Overridable | Warning |
| `OverCapacity` (6) | Longer than the capacity left on the day(s) it touches | Overridable | Warning |
| `BreakConflict` (3) | Overlaps a planned break | Overridable | Warning |
| `TentativeConflict` (2) | Overlaps **tentative** work | Warning | Warning |
| `SkillWarning` (7) | A skill the work asks for, or was searched with, is not one the person holds | Warning | Warning |
| `DueDateRisk` (9) | The work would end after the ticket is due | Warning | Warning |

**Override policy.**

- A block means "change the cause": the time away, or the *offered for planned work* switch. Nobody
  overrides a block, in any screen, on any route.
- An override is a deliberate decision by someone who holds `schedule.override` (Administrator and
  Manager by default), with a reason kept with the work (`OverrideReason`, `OverriddenConflicts`,
  `OverriddenByUserId`, `OverriddenAt`) and in the audit entry. The screens offer the reason box only
  when the server says the caller may (`overrideAllowedForCaller`); otherwise "Someone who can
  override scheduling conflicts can place it anyway, with a reason."
- Confirming tentative work is a fresh confirmed placement: the overridable conflicts apply again
  and need the same reason. Placing tentative work needs no override: nothing is taken.
- Warnings are never silent: they are shown before the write and written to the audit entry with it.
- A later move to a clean time clears the override record.

## Over-capacity and shortage

- A **person** is over capacity on a day when confirmed work exceeds the usable time (an override
  put it there). The row says "… over", the bar turns red, the summary counts it. Tentative work
  never makes a day over capacity.
- A **group** is short when the queue's demand exceeds its remaining capacity over the horizon
  (`shortageMinutes`). The queue page shows it as a figure beside *Demand* and *Free*. It is
  reported, not resolved: the module proposes no overtime and reassigns nobody.
- Both are **facts about planned time**, not judgements about people, and neither reaches a client.

## Screens

**Planning queue** (`/dashboard/workforce/queue`, "Planning queue" in the Workforce navigation for
anyone who sees others). Filters by team, department and skills; a search box over reference, title,
client and holder; a *Show* filter (due soon or overdue, not enough capacity before due, nobody holds
it, with / without an estimate). The summary: *Waiting*, *Demand* (and how many have no estimate),
*Free, next N days* (and how many people are counted), *Shortage*, the window. The table: work (with
the source and the skill it asks for), client, priority, holder, what it needs ("2h of 3h, may
split"), due (with the DUE TODAY / DUE TOMORROW / OVERDUE chip), why it waits (with the free time
before the due date when that is the reason), age, and the actions **Schedule** (the plan dialog for
the holder, the remaining effort as the duration), **Plan with a window** (the preview dialog),
**Find technician** (the search with the remaining effort as the duration) and a disclosure that
opens the requirement editor in place ("Set what it needs" / "Change").

**Plan with a window** (the preview dialog; from the queue, the ticket's *Planned work* panel):
whose time, earliest start, latest finish, effort, *May be split*, *Pencil in*; **Calculate**; the
proposed plan ("8h of 10h placed · 2h unallocated · 8h free in the window, longest 4h"), the pieces
by day, the warnings, an optional note; **Confirm plan** / **Pencil in** / **Override and confirm**
(with the reason box when the server allows). A stale confirmation shows the fresh proposal with the
notice above it. Changing any input clears the preview.

**The plan dialog** gains *Pencil in* (new work only); the button reads **Pencil in** when it is on.

**My plan / a person's Plan tab**: tentative work carries a dashed *Tentative* chip and, for whoever
may confirm it, a **Confirm** button (the confirm dialog: the time checked again, conflicts and the
override reason as elsewhere). The day's figures show "+2h tentative" under *Planned* and "6h if
confirmed" under *Free* when there is tentative work.

**Team schedule**: tentative blocks are dashed and sky-coloured, named "…, tentative"; the capacity
bar draws a tentative segment inside the free time and the text adds "· 2h tentative"; the summary
gains *Tentative* and *Free if confirmed*; the drawer shows the chip, **Confirm** for tentative work
the viewer may confirm, and **Pencil in** on committed work for schedulers; the week view marks
tentative items; a **Planning queue** link sits in the toolbar.

**On a ticket**: the *Planned work* panel lists tentative work with the chip, carries the
requirement editor ("No estimate · Set what it needs", or "2h needed · 1h planned (+30m tentative),
1h to plan · may be split · needs SonicWall") and offers **Plan with a window** beside *Plan this
work*.

## API

All under `api/workforce` behind `Features:Workforce`; every action needs `schedule.view`; the
module refuses client accounts before any of these run.

| Route | Permission | Does |
|---|---|---|
| `POST plan` (`tentative` on the body) | `schedule.manage` | places confirmed or tentative work |
| `POST plan/{id}/confirm` `{ version, overrideReason? }` | `schedule.manage` | tentative → planned, checked again as confirmed work |
| `POST plan/{id}/tentative` `{ version }` | `schedule.manage` (schedulers of the person) | planned → tentative |
| `GET plan/requirements/{ticketId}` | `schedule.view` | what the work needs and what is allocated |
| `PUT plan/requirements/{ticketId}` | `schedule.manage` | sets it |
| `GET plan/queue?…&horizonDays` | `schedule.view` | the planning queue with reasons, urgency and the shortage |
| `GET plan/preview?ticketId&appUserId&earliest&latest&minutes&splittable&tentative&minChunk` | `schedule.view` (and the right to plan for the person) | a proposal; writes nothing |
| `POST plan/preview/confirm` | `schedule.manage` | writes the pieces as shown, or 409 with a fresh preview |

`WorkAllocationDto` gains `canConfirm`; `TeamPlanDto` gains `tentativeMinutes` and
`projectedRemainingMinutes`; `TeamUnscheduledWorkDto` gains `createdAt` (when the ticket was raised).

## Storage

One new table, one migration (`20261003074611_WorkforceWorkPlanning`, additive; `Down` drops exactly
it):

```
WorkPlanning   work_planning   tenant | TicketId (-> tickets, cascade, unique) | RequiredMinutes | EarliestStart | LatestEnd (instants)
                               | Splittable | RequiredSkillId | Note (300) | UpdatedByUserId | CreatedAt | UpdatedAt
```

Indexes: `TicketId` unique, `MspOrganizationId`. `WorkAllocation.Status` gains the value 2
(Tentative) with no schema change. Nothing is added to `tickets`.

## Permissions, client isolation, tenant isolation

- Same scoped permissions as Phase 3 and 4: `schedule.view` to see, `schedule.manage` at a scope that
  reaches the person to plan (Own: yourself), `schedule.override` to override. Pencilling committed
  work back in and setting a requirement need `schedule.manage`; the requirement is readable by
  anyone who may see the ticket and holds `schedule.view`.
- A person outside the caller's `schedule.view` scope is "Person was not found."; a ticket outside
  their ticket scope is "Ticket was not found."; both say nothing about whether the thing exists.
- **Client isolation.** `EndpointAuthorizationTests.Nothing_a_client_can_receive_carries_workforce_planning`
  now also forbids `RequiredMinutes`, `WaitingReason`, `PlanToken` and `Shortage` in any ticket,
  control-panel, knowledge or attachment shape; the golden permission rows cover the seven new
  actions; the client-role walk refuses every one. `WorkPlanTests` proves a client-portal account
  gets "Only staff accounts can use the workforce module." on all seven.
- **Tenant isolation.** `WorkPlanning` is a `TenantEntity` under the global filter; the queue for
  another organization's administrator is empty, and a requirement, a preview and a confirmation
  for a ticket in another organization are "not found" (`Another_organizations_people_and_work_are_not_found`).
- A requirement, a queue item and a preview carry a ticket's reference, title and client only to an
  asker who may see that ticket, exactly as allocations do.

## Concurrency

- Confirming and pencilling in take the person's `PlanningGate` (a row lock on PostgreSQL), reload
  the row and refuse a stale `version` ("This plan changed since you opened it. Reload and try
  again.").
- Confirming a preview takes the gate once for all its pieces, re-computes the frame under it and
  compares the token; two confirmations for the same time end with one plan written and one 409 with
  a fresh preview (`Two_managers_confirming_previews_for_the_same_time_end_with_one_plan`, in-memory,
  through the same gate the PostgreSQL row lock backs).
- Every piece of a confirmed preview is checked with `ConflictEvaluator` under the gate; a refusal
  rolls the whole transaction back, so no piece of a refused plan is ever in the database. This is
  proved on a relational provider (SQLite, and PostgreSQL when `DESK_TEST_POSTGRES` is set) in
  `Every_capacity_query_runs_on_a_real_database`: a hand-crafted second piece over existing confirmed
  work is refused and the ticket has no rows afterwards; the same test runs confirm, pencil in, the
  requirement and the queue on the real engine.

## Audit and notifications

| Event | When | Detail |
|---|---|---|
| `workforce.allocation.created` | every placement, now with `status` (Planned / Tentative) and `warnings` (tentative, skill, due date) | as before |
| `workforce.allocation.confirmed` | tentative → planned | person, reference, when, from/to, override reason and types, warnings |
| `workforce.allocation.made_tentative` | planned → tentative | person, reference, when, from/to |
| `workforce.planning.requirement_set` | the requirement written | reference, before/after |
| `workforce.allocation.plan_confirmed` | a preview confirmed | person, reference, tentative, required, splittable, the pieces with their ids, allocated, the token |

Notifications: "Work pencilled in for you: {reference}" when someone else pencils work into your
plan; "Planned work confirmed: {reference}" when someone else confirms it; the existing "Work
planned for you" for confirmed placements; a confirmed preview sends one notification for all its
pieces ("… · 3 pieces, from {when}").

## Performance

Measured in `CapacityPerformanceTests` (SQLite, in the unit suite) at 50 / 100 / 500 people with
1,000 / 4,000 / 30,000 allocations; the counts are pinned and must not grow with the data:

| Read | Queries | 50 people | 100 | 500 |
|---|---|---|---|---|
| Planning queue, 14 days, everyone | 26 | 38 ms | 60 ms | 589 ms |
| Split preview, 10 hours across a week, one person | 29 | 8 ms | 7 ms | 32 ms |
| Placing one piece of work | 51 (was 50: the planning row is read once) | | | |

The relational test (`Every_capacity_query_runs_on_a_real_database`, SQLite or PostgreSQL) runs
confirm, pencil in, the requirement, the queue and a preview confirmation, including a refused one,
on the real engine, so no provider-only behaviour hides behind the in-memory suite.

The queue is the unscheduled list (8) plus the planning rows, the effort sums, the skill names and
the group's fortnight of capacity (the team scheduler's own reads). A preview is one person's capacity
over the window and the ticket's planning row; it is pure after that. Limits that keep both bounded:
a horizon of at most 14 days, a window of at most 14 days, effort of at most 100 hours, at most 20
pieces, and the existing 1,000-people ceiling on group reads.

## Deferred

| Not in this phase | Why | Where it would go |
|---|---|---|
| Recurring planned work | Recurring **tickets** already exist (the recurrence engine raises the ticket); planning each occurrence is a placement like any other, and a recurring allocation would need a second recurrence model to keep in step with the first | A later phase, if planning the series ahead proves necessary |
| Bulk planning (many tickets in one go) | Each placement needs a human decision about when; the preview covers the one case that needs computation (effort in a window) | Phase 7+ |
| Splittable work across several people | Changes who is accountable for a piece of work; the preview is for one person | Not planned |
| A separate scheduling priority | The ticket's own priority and due date are enough to order the queue; a second priority would drift from them | Not planned |
| A stored "waiting because" reason typed by a person | That is a ticket status (the ticket already has those); the queue's reason is a planning fact derived every time | Not planned |
| A skill required on the ticket itself | A planning concern kept off the client-facing ticket shapes; it lives on the planning row | Owner's decision pending |
| Month view, utilization analytics, planned-vs-actual | Later phases | Phases 7–9 |

## Troubleshooting

| Symptom | Cause | Do |
|---|---|---|
| "This work is not pencilled in." | Confirm on committed or cancelled work | Nothing to confirm |
| "This work is not committed." | Pencil in on tentative or cancelled work | Nothing to pencil in |
| "Only someone who schedules others can pencil committed work back in." | The person it is planned for, or an Own-scope planner, tried to take committed work back to tentative | Ask a scheduler, or take it out of the plan |
| "This work was scheduled for you and is fixed in place. Ask whoever planned it to confirm it." | A technician confirming fixed tentative work a scheduler placed | Ask whoever planned it |
| "The plan changed since the preview. Review the new proposal." | Something in the person's time changed between Calculate and Confirm (a placement, a move, time away) | Look at the new proposal shown and confirm it, or change the window |
| "Only 7h of 10h fits in the window; 3h remains unallocated." | The window has less confirmed free time than the effort | Widen the window, split the rest to another window or person, or correct the estimate; nothing is over-booked for you |
| "No single free period of 5h in the window; the longest is 4h." | Continuous work and no free stretch long enough | Tick *May be split*, widen the window, or pick someone else |
| "Every piece must lie inside the planning window." / "The pieces overlap each other." / "The pieces add up to more than the required effort." | A confirmation sent pieces that are not the preview's | Calculate again and confirm what is shown |
| "A preview covers at most 14 days." / "The planning window is at most 31 days." | A window beyond the limit (preview / requirement) | Narrow it |
| "The window is in the past." | The latest finish is more than a day ago | Choose a window from yesterday on |
| "The window started before now; proposing from …" | The window began earlier today (or earlier); nothing is proposed before now | Expected; widen the end if more is needed |
| "This person is not offered for planned work." | A preview for someone switched out of planned work, or inactive | Switch them back on, or choose someone else |
| "Only whoever holds this work, their team, or someone who schedules others can say what it needs." | A technician setting the requirement on work they neither hold nor share a team with | Ask the holder or a scheduler |
| "Effort is between 5 minutes and 100 hours, in whole five-minute steps." | The estimate is out of range or not a multiple of five | Round it |
| "That skill is not in the skill catalogue." | A skill id that is not one of the organization's active or retired skills | Pick from the list |
| The queue says *Not enough capacity before the due date* but the person looks free | Capacity counts whole days up to and including the due date, confirmed work only; tentative work is not deducted and days after the due date do not count | Expected: the figure beside the reason says how much is free before it is due |
| *Shortage* shows a figure though every person has free time | Demand is the remaining estimated effort of everything waiting, against the free time of the people offered for work over the horizon; items without an estimate add nothing | Estimate the rest, widen the group, or accept the fact |
| A tentative block has no Confirm | `canConfirm` is false: fixed work scheduled for the viewer, or a person outside their `schedule.manage` scope | Ask whoever planned it |
| A due date in the queue differs from the ticket by a day | The chip is judged in the organization's zone; the column shows the browser's | Expected |

More in [planned-work.md](planned-work.md#troubleshooting) and
[team-scheduler.md](team-scheduler.md#troubleshooting).
