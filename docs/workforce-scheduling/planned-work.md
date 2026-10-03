# Planned work (Phase 3)

Putting work into people's time: planning your own, scheduling others, moving it, giving it to
someone else and taking it out. A piece of planned work is a **work allocation**: who is planned to
do which work, when, and for how long.

Planned time is a **capacity boundary**. It is what the capacity engine counts as confirmed work
([capacity.md](capacity.md)), so free windows, remaining capacity, team capacity and the technician
search all follow it from the moment it is placed. It is not time logging (actual time is the
ticket's time entries, which nothing here reads or writes), not attendance, and not a record of
what was done. The My plan page says so: "Planned time, not actual time: log your time on the
ticket as always."

**Internal only.** Nothing here is ever part of what a client receives. A ticket a client can see
does not make who is planned on it, or when, theirs ([permissions-and-security.md](permissions-and-security.md)
and [Tenant isolation and client security](#tenant-isolation-and-client-security) below).

From Phase 4 the same model has a **second screen**: the team scheduler
([team-scheduler.md](team-scheduler.md)) shows everyone a scheduler may see on one board and lets
them drag, drop and resize, but every one of those gestures ends in the writes described here
(`PUT`, `reassign`, `POST plan`, `DELETE`), under the same rules; it adds two reads and no table.

## Overview

```
     Internal        Autotask        ConnectWise
        |               |                |
        +---------------+----------------+
                        v
                 Unified work item  (the ticket row, whatever its origin)
                        |
             +----------+----------+
             v                     v
       SELF PLANNING        AUTHORIZED SCHEDULING
       schedule.manage      schedule.manage at Team,
       at Own scope         Department or All scope
             |                     |
             +----------+----------+
                        v
                  Work allocation
                        |
            Capacity and conflict validation,
            under the person's gate
                        |
                        v
                 Technician plan
                        |
             +----------+----------+
             v                     v
           My plan            My day (later)
```

Three things the picture leaves out, each covered below:

- **"Authorized scheduling" is a permission scope, not a role name.** Whoever holds `schedule.manage`
  at Team, Department or All may schedule the people that scope reaches, so a team lead qualifies for
  their team. Administrator and Manager hold it at All by default; Technician holds it at Own, which
  is the self-planning path ([Self-planning and authorized scheduling](#self-planning-and-authorized-scheduling)).
- **Capacity and conflict validation are one pass, with one extra exit.** Working window, time away,
  "not offered for work", breaks, overlaps and over-capacity come back from a single evaluation
  against what is really in the plan at that moment, under the person's gate. Blocks stop there;
  overridable conflicts can be placed by someone holding `schedule.override` who gives a reason,
  kept on the work ([Conflicts and overrides](#conflicts-and-overrides), [Concurrency](#concurrency)).
- **Two arrows run the other way.** Scheduling someone on a ticket nobody in the portal holds makes
  them its portal holder, the only link from allocation back to assignment, and the PSA is never
  told ([Allocation is not assignment](#allocation-is-not-assignment)). A ticket that finishes
  releases its future allocations from the worker ([What finishes leaves the plan](#what-finishes-leaves-the-plan)).

"My day" is a later phase: starting and completing work from the plan, and actual time against
planned. Nothing of it exists yet.

Phase 5 put a planning queue in front of this picture and gave a placed piece of work two states,
confirmed or tentative; the picture with those in it is in
[advanced-planning.md](advanced-planning.md#overview).

## One work model

An allocation points at the existing `Ticket` row, whatever the ticket's origin: the team's own
boards (`INT-000123`), Autotask (`Autotask 43829`), ConnectWise (`ConnectWise 9923`), monitoring
alerts. It carries no title, client, status or provider of its own; those stay with the ticket,
which stays with its system of record. Nothing is duplicated, and planning never writes to a PSA:
the provider-side assignee, due date, SLA, sync status and update hash are unchanged after a ticket
is planned, and no background job is queued
(`An_Autotask_ticket_is_planned_as_itself_with_nothing_copied_and_nothing_sent`).

There is no such thing as planned time without work behind it. A small piece of internal work
that has no ticket yet gets one, raised on a board in the same step ([Internal work](#internal-work)).

The reference shown everywhere is the board number for the team's own work (`INT-000123`) and
`{Provider} {external id}` for a provider's ("Autotask 43829", "ConnectWise 9923"). The source on the
unscheduled list is "Team board", "Monitoring", or the provider's name.

## Allocation is not assignment

The one rule: **planning never changes what the PSA says.** An allocation records who is planned to
do the work and when. Cancelling one never touches the ticket either
(`Taking_work_out_of_the_plan_leaves_the_ticket_exactly_as_it_was`).

The one bridge, **holder bridging**: when someone is scheduled *by someone else* on a ticket that
nobody in the portal holds, they become its portal holder. That is the same portal-only fact that
"Take it" records, so the ticket shows in their work and opens for them; the PSA is not told.

| When work is placed for a person by someone else | What happens to the ticket's portal holder |
|---|---|
| Nobody holds it in the portal (and it is not the PSA case below) | The person becomes the holder. A `TicketAssignment` row is written with the note "Planned into their time", and `ticket.assigned.portal` is audited with `viaPlanning: true` |
| Nobody holds it in the portal, it is a PSA ticket the provider has assigned to someone, and the person can already see it | Left alone: the provider's choice is not second-guessed |
| The person already holds it | Nothing |
| Someone else holds it and the person can see it (an open team board, or a ticket in their scope) | Left alone: a second person is planned on it without changing who holds it |
| Someone else holds it and the person **cannot** see it | Refused (400): "**{Person} cannot open this ticket: it is held by {Holder}. Hand it over to {Person} first, or plan it for {Holder}.**" Work planned for someone who cannot open it is work that will not happen |
| The person plans their own work | Never bridges. Planning an unheld ticket from an open team board for yourself does not make you its holder |
| Work is given to someone else (reassign) | If the person it is taken from holds the ticket, it goes with the work (`TicketAssignment` note "Planned work given to them"). Otherwise the rows above apply to the new person |

"Can see" is the person's own ticket scope (`tickets.view.all` or `tickets.view.assigned`, boards
included), the same rule as the ticket list. Proven by
`Work_held_by_someone_else_is_not_quietly_moved_and_a_person_who_could_not_open_it_is_not_planned_on_it`,
`A_lead_schedules_a_ConnectWise_ticket_for_a_technician_who_then_sees_it_in_their_plan` and
`A_lead_gives_work_to_someone_else_and_their_capacity_is_checked_first`.

The holder change is decided before the gate is taken but written only with the plan itself, in
the same transaction, after the conflict check: a refused placement leaves the ticket's holder
exactly as it was (see [Placing work: the order of checks](#placing-work-the-order-of-checks);
proven by `A_refused_placement_changes_nothing_about_the_ticket_and_a_refused_internal_task_raises_none`).

## Self-planning and authorized scheduling

Who may do what follows the scope of `schedule.manage` ([permissions-and-security.md](permissions-and-security.md)).

| Caller's `schedule.manage` | Their own plan | Other people's plans |
|---|---|---|
| none | Read only (through `schedule.view`). `plan/unscheduled` answers an empty list; "Add to plan" and "Plan work" are not offered | None |
| **Own** (Technician default) | Place work they can already see into their own time; move, resize and take out their own self-planned work; move (not take out) flexible work scheduled for them | None: another person is "Person was not found." |
| **Team** / **Department** | As a scheduler (below) | As a scheduler, for the people they share a team / department with |
| **All** (Administrator and Manager defaults) | As a scheduler | As a scheduler, for everyone in the organization |

A **scheduler** is someone whose `schedule.manage` scope is wider than Own *and* reaches the person
(`WorkforceAccess.CanScheduleOthersAsync`). They place, fix, move, resize, give away and take out
that person's work. The authority is theirs on their own plan too.

Every allocation records how it got there:

| Field | Meaning |
|---|---|
| `Method` | `Self` (1): the person planned their own work. `AuthorizedUser` (2): a scheduler placed it, or gave it to them. `Automation` (3): reserved for the system; nothing writes it today |
| `ScheduledByUserId` | Who decided. Shown as "Planned by you" / "Planned by Jason Carter" for self-planned work and "Scheduled by you" / "Scheduled by Lena Lead" for scheduled work |

Giving work away always makes it `AuthorizedUser`, scheduled by the caller.

Self-planning reaches only work the person may already see: a ticket outside their scope and a
guessed id both answer "Ticket was not found.", and another person's plan answers "Person was not
found." (`Self_planning_reaches_only_work_the_person_may_already_see`). The people a caller may plan
for (`plan/people`) are exactly those their scope reaches: a technician sees themselves, a team
lead their team, an administrator everyone
(`The_people_someone_may_plan_for_follow_their_scope`).

## Fixed and flexible

`IsFixed` is set by a scheduler when placing or updating work for someone else. It means nothing on
self-planned work: a scheduler fixing a person's own self-planned work is ignored (`IsFixed` stays
false), and a person asking to fix their own work is refused. The screen explains it as "They
cannot move it; only someone who schedules others can. Leave off for work they may rearrange."

Who may do what to a piece of planned work:

| Action by the person it is planned for | Self-planned | Scheduled, flexible | Scheduled, **fixed** |
|---|---|---|---|
| Move, resize, change the note | Yes | Yes, within their capacity (the same conflict rules apply) | No: "This work was scheduled for you and is fixed in place. Ask whoever planned it to move it." |
| Take out of the plan | Yes | No: "This work was scheduled for you. Ask whoever planned it to take it out, or move it if it is not fixed." | No (same message) |
| Fix or free | No: "Only someone who schedules others can fix work in place or free it." | No | No |
| Give to someone else | No: "Only someone who schedules others can give work to someone else." | No | No |

A scheduler whose scope reaches the person may do all of it. Someone who can see the person but is
not a scheduler and not the person is refused with "You can't change this person's plan."; someone
who cannot see the person gets "Person was not found."

The API says what the asker may do with each piece (`canEdit`, `canCancel`, `canReassign`), and the
screens offer only those actions; a person who may do nothing with a piece sees "Ask whoever planned
it to change it". Proven by
`Fixed_work_stays_where_the_scheduler_put_it_and_flexible_work_may_be_moved_but_not_removed`.

## Status lifecycle

```
Tentative (2) ──confirmed (checked again as committed work)──▶  Planned (1)
Planned (1)   ──pencilled in by a scheduler──▶  Tentative (2)
Planned (1) / Tentative (2)  ──cancelled by a person──▶  Cancelled (5)
                             ──released: the ticket finished before the planned time──▶  Cancelled (5)
```

Two live states and one end state. Only `Planned` takes confirmed capacity; `Tentative` (Phase 5,
[advanced-planning.md](advanced-planning.md#tentative-and-confirmed-work)) is in the plan, keeps the
ticket off the unscheduled list, is shown and projected, and takes nothing until it is confirmed.

- A cancelled allocation is kept, with when, by whom and why (`CancelledAt`, `CancelledByUserId`,
  `CancelReason`, up to 200 characters). Nothing is deleted except with the ticket or the person
  (both foreign keys cascade).
- Cancelling never changes the ticket: taking work out of a plan is not closing it. The ticket goes
  straight back onto the unscheduled list if it is still the person's.
- Cancelled work cannot be moved or cancelled again: "This work is no longer in the plan." /
  "This work is already out of the plan."
- A **finished ticket** (status containing `RESOLV` or `CLOSED`, however the provider spells it)
  cannot be planned: "This ticket is finished; there is nothing left to plan." Work already planned
  on it is released ([What finishes leaves the plan](#what-finishes-leaves-the-plan)).

## Placing work: the order of checks

`POST api/workforce/plan`, in order:

1. The person: visible to the caller's `schedule.view` scope, or 404 "Person was not found."
2. The right to plan: a scheduler for that person, or the person themselves with `schedule.manage`;
   otherwise 403 "You can't plan your own work." / "You can't plan work for this person."
3. The period ([Validation](#validation)): every problem at once, 400.
4. The note: at most 300 characters, no control characters, 400.
5. Fixed work from someone who is not a scheduler: 400 "Only someone who schedules others can fix
   work in place."
6. The ticket: visible to the caller's own ticket scope, or 404 "Ticket was not found." (the same
   words as for an id that names nothing).
7. A finished ticket: 400.
8. Holder bridging is decided, when the work is for someone else ([above](#allocation-is-not-assignment)).
   The refusal is a 400. Nothing is written yet: the holder change, its `TicketAssignment` row and
   its audit entry are saved with the allocation in step 10, inside the gate's transaction.
9. The person's **gate** is taken ([Concurrency](#concurrency)) and the conflicts are evaluated
   against what is really in the plan now ([Conflicts and overrides](#conflicts-and-overrides)):
   409, or the override is recorded.
10. The allocation is written, audited and (for someone else's plan) notified, in one transaction.

Moving (`PUT`) and giving away (`POST .../reassign`) check the version before the period, take the
gate for the person whose time is being used, and evaluate conflicts with the work's own current
place ignored (`ProposedWork.IgnoreAllocationId`), so moving work later within the same window is
not a clash with itself. An update that does not change the time skips the conflict check.
Cancelling (`DELETE`) takes no gate and no version.

## Conflicts and overrides

The evaluation is Phase 2's `ConflictEvaluator` ([conflicts.md](conflicts.md)), run inside the
gate on fresh data: what a screen showed as free a moment ago is not a reservation.

| Severity | Effect when placing work |
|---|---|
| **Block** (`UnavailableConflict`, `NotSchedulable`) | Refused. Nobody can override: change the time away, or switch "offered for planned work" back on |
| **Overridable** (`HardConflict`, `OutsideWorkingWindow`, `OverCapacity`, `BreakConflict`) | Refused unless the caller holds `schedule.override` **and** gives a reason of at least 5 characters |
| **Warning** (`TentativeConflict`, `SkillWarning`, `DueDateRisk` from Phase 5) | Never refuses. The messages are kept in the audit entry (`warnings`); a `DueDateRisk` also travels in the 409 payload when something else refuses, so the screen names it |

`BreakConflict` was a warning in Phase 2 and is **overridable** from Phase 3: work placed over a
break takes capacity the break does not offer, so the planned duration and the capacity taken
would silently disagree. Working through lunch is a decision with a reason, like overtime
(`Work_outside_the_working_window_over_a_break_or_over_other_work_is_refused_unless_overridden`).

The refusal is **409** `conflict` with a problem body. The `detail` is one of three sentences,
built from the most severe conflict's own message, and the `payload` says what is in the way and
whether the caller can do anything about it:

| Case | `detail` | `payload.canOverride` | `payload.overrideAllowedForCaller` |
|---|---|---|---|
| Only overridable conflicts; the caller may override but gave no reason (or one under 5 characters) | "This time has a conflict: *{message}* Give a reason to override it." | true | true |
| Only overridable conflicts; the caller may not override | "This time is no longer available: *{message}*" | true | false |
| At least one block | "This time cannot be used: *{message}*" | false | false |

```json
HTTP 409  application/problem+json
{
  "type": "https://desk.portal/errors/conflict",
  "title": "conflict",
  "status": 409,
  "detail": "This time has a conflict: Already has confirmed work during this period. Give a reason to override it.",
  "correlationId": "…",
  "payload": {
    "canOverride": true,
    "overrideAllowedForCaller": true,
    "stale": false,
    "conflicts": [
      { "type": 1, "severity": 2, "start": "2026-10-05T08:30:00+00:00", "end": "2026-10-05T09:30:00+00:00",
        "message": "Already has confirmed work during this period.", "blockingWorkId": "…" }
    ]
  }
}
```

Every conflict is listed, each with the exact overlapping stretch. `blockingWorkId` names the work
only when the caller may see that ticket; otherwise it is null and the message is the same generic
sentence. The screens show the list as "*Already planned*: Already has confirmed work during this
period." (the names: Already planned, Tentative work, Break, Unavailable, Outside working hours,
Over capacity, Skill, Not offered for work) and, when `overrideAllowedForCaller` is true, a box
"Reason to override (kept with the work)"; the button becomes "Override and save" (or "Override and
give", "Override and plan") once the reason has 5 characters. When `canOverride` is true but the
caller may not: "Someone who can override scheduling conflicts can place it anyway, with a reason."

**Override rules**

- `schedule.override` (Administrator and Manager by default; technicians cannot). Blocks are never
  overridable, by anyone (`Time_away_blocks_everyone_and_exact_boundaries_are_fine`).
- The reason is trimmed, 5 to 300 characters, plain text.
- What was overridden is kept with the work: `OverrideReason`, `OverriddenConflicts` (the conflict
  types, comma separated), `OverriddenByUserId`, `OverriddenAt`; the audit entry carries the reason
  and the types. The plan shows "Override: *{reason}*".
- A later move to a clean time clears the override record; a move that needs one records it anew.
- A reason given when nothing needs overriding is ignored.
- Stretches are half-open: work that starts as other work ends is not a clash.

## Concurrency

Two planners can both be shown 15:00–16:00 free and both try to take it. Two mechanisms:

| Mechanism | What it protects | How |
|---|---|---|
| **Planning gate** (`PlanningGate`) | One person's plan is changed by one request at a time, so conflicts are checked against what is really there and exactly one of two simultaneous bookings wins | On PostgreSQL: `SELECT "Id" FROM app_users WHERE "Id" = {person} FOR UPDATE` inside the request's transaction, so the lock holds across every API container (two serve during a deploy). On SQLite and the in-memory provider (one process): a per-person semaphore, plus a transaction where there is one. Committed only when the write succeeds; a refusal rolls back |
| **Version** (optimistic) | Two people editing the same allocation from stale screens cannot both win | Every allocation carries an integer `Version` (also an EF concurrency token). `PUT` and `.../reassign` must send the version the screen showed; a mismatch is 409 "This plan changed since you opened it. Reload and try again." with `payload.stale: true` and no conflicts. The row is re-read under the gate before the check, and a token refusal at save time is turned into the same 409, so a change made while the screen was open is always a stale screen, never a 500. A cancel re-reads too: work already taken out answers 400 "This work is already out of the plan." Every change increments it |

Proven by `Two_planners_taking_the_same_free_hour_at_once_end_with_one_booking` (two contexts, the
in-process gate), `Two_requests_for_the_same_hour_on_a_real_database_end_with_one_booking`
(PostgreSQL, two connections, the row lock; runs only with `DESK_TEST_POSTGRES`) and
`A_stale_screen_cannot_overwrite_a_change_made_since` and
`A_change_made_while_the_screen_was_open_is_a_stale_screen_not_a_crash`.

## Validation

Every problem with the period is reported in one answer (400 `validation_failed`):

| Rule | Message |
|---|---|
| Ends after it starts | "The work must end after it starts." |
| At most 24 hours | "One piece of planned work can be at most 24 hours." |
| Whole minutes | "Times must be whole minutes." |
| Starts no earlier than 24 hours ago | "Work can be planned from yesterday onwards, not earlier." |
| Starts at most a year ahead | "Work can be planned at most a year ahead." |
| Note at most 300 characters, no control characters | "Keep the note to 300 characters." / "The note contains characters that can't be shown." |
| Override reason at most 300 characters | "Keep the override reason to 300 characters." |
| Cancel reason at most 200 characters | "Keep the reason to 200 characters." |
| Giving work to the person who already has it | "That is who already has it." |

Instants are taken as UTC; the screens convert the wall-clock time typed in the person's zone. Notes
and reasons are plain text, stored as typed and shown as text, never as markup
(`A_note_is_plain_text_and_a_finished_ticket_cannot_be_planned`). The dialogs also check on the
client: "Choose a date.", "The work must take between 5 minutes and 24 hours.", "Choose a start time
or one of the free windows." (the API accepts any whole number of minutes from one).

The stand-alone conflict check (`GET people/{id}/conflicts`, Phase 2) accepts a start up to a month
back; placing work is stricter (yesterday onwards).

## What finishes leaves the plan

A ticket that is resolved or closed, in the portal or in the PSA, comes out of people's **future**
plans, so nobody's day shows an hour reserved for work that is already done.

- The capacity engine stops counting a future allocation on a finished ticket **at once**
  (`WorkAllocationReader` leaves it out), whether or not the worker has run yet.
- The worker's `WorkAllocationReleaseBackgroundService` runs every **5 minutes**. For every
  organization with planned work in the future it cancels allocations that are `Planned`, start in
  the future and whose ticket is finished: status `Cancelled`, `CancelReason` "The work finished
  before its planned time.", `Version` incremented, audit `workforce.allocation.released`. At most
  500 per organization per pass. `CancelledByUserId` stays null (nobody did it).
- Planned time that has already started or passed is **left as it was**: it is history, and the
  planned-versus-actual figures of a later phase want it. The plan shows such work with a "Ticket
  finished" chip.
- A deleted ticket takes its allocations with it (cascade); nothing to release.

Proven by `Work_that_finishes_leaves_future_plans_and_stays_in_past_ones`.

## Screens

All under **Workforce** (menu entry for staff with `schedule.view`, module on). The sub-navigation
is People · **My plan** · My capacity, plus Team schedule ([team-scheduler.md](team-scheduler.md),
Phase 4), Team capacity and Find available technician for anyone who can see more than themselves.
Every time is shown in the person's zone ("Times in Asia/Kolkata"); the team scheduler's axis is the
one place drawn in the organization's zone, with each person's own zone named beside it.

### My plan (`/dashboard/workforce/my-plan?date=`)

"What you are planning to work on, when, and what is still unscheduled." A sign-in that is not a
staff account is told "This sign-in is not a staff account, so it has no plan."

- **The day**: Previous day · Today · Next day, a date field, the day's name. A notification opens
  the page on its date.
- **Figures** for the day: **Capacity** (usable minutes), **Planned** (confirmed work), **Free**
  (remaining capacity). They are the same figures as My capacity, with planned work counted.
- **The agenda**: one list, in time order, of planned work, "Break" and "Available · 2h 30m" free
  windows. Empty states: "Away all day.", "Not a working day.", "Nothing here yet."
- **A piece of work** shows the ticket title (a link; or "Work you cannot open" when the ticket is
  outside the viewer's ticket scope), reference, client, duration, who planned it ("Planned by you",
  "Scheduled by Lena Lead"), **Fixed**, "Override: *{reason}*", "Ticket finished", the note, and the
  actions the viewer may take: Move (pencil), Give to someone else (arrows), Remove from the plan
  (bin, after "Take INT-000123 out of the plan? The ticket itself is not changed.").
- **Plan work** (for anyone with `schedule.manage`): opens the ticket picker, then the plan dialog.
- **Unscheduled work of mine (N)** ([below](#unscheduled-work)): each row has **Add to plan**;
  **Internal work** raises and plans a small piece of work in one go.

### A person's Plan tab (`/dashboard/workforce/people/{id}?tab=plan&date=`)

The first tab on a person (then Work schedule, Availability, Skills): the same agenda, figures and
actions for that person, as far as the viewer may act. A scheduler sees **Plan work**; the dialog
is locked to that person ("Plan INT-000123 · for Jason Carter"). Team capacity, the technician
search and the ticket panel link here on a date.

### Planned work on a ticket (`/dashboard/tickets/{id}`)

A **Planned work** panel for staff who hold `schedule.view`, with the module on: who has it planned,
when, for how long, who planned it, Fixed, the note; each person links to their Plan tab on that
date. "Not in anyone's plan yet." when nobody has. **Plan this work** (with `schedule.manage`, and
not on a finished ticket) opens the plan dialog for the viewer's own time, or anyone they may plan
for. Only people the viewer's `schedule.view` scope reaches are listed
(`What_is_planned_on_a_ticket_shows_only_people_the_asker_may_see`). A client's ticket page never
renders the panel, and the API would refuse it anyway.

### The dialogs

| Dialog | What it asks | Buttons |
|---|---|---|
| **Choose work to plan** ("Plan work for Jason Carter" / "for you") | A search over open tickets the viewer can see ("Number, title or client"), 15 at a time, each with its holder or "unheld". "Open tickets you can see. Planning it puts it in their time; if nobody holds it yet, they will." | pick a row |
| **Plan work** / **Move planned work** | Whose time (when the viewer may plan for more than one person; "(you)", "· not offered for work"), Date, Planned duration (minutes; 15 to 240 suggested, 5 to 1440 accepted), Start, "Times in {zone} · ends 16:30", **Free windows that hold 1h 30m** as chips (click one to set the start; when moving, the work's own place counts as free), "Find who is free on this date" (the Phase 2 search, first fit per person), **Fixed** (not for your own work), Note (optional). "No working time on this day." / "No single free window is long enough. Try a shorter duration or another day." | Cancel · **Add to plan** / **Move** / **Override and save** |
| **Give work to someone else** | "Now planned for Jason Carter, 09:00–10:00 (1h). Their capacity is checked before it moves." To (people the viewer may plan for), Date, Start; the length is kept | Cancel · **Give the work** / **Override and give** |
| **Plan internal work** | "A ticket is raised on the board in your name and placed in your time, so the work can be measured like any other." What (title, up to 500), Board (active internal boards; "There is no internal board to raise it on yet. Ask whoever manages Internal boards to create one."), Date, Start, Duration | Cancel · **Raise and plan** / **Override and plan** |

The server checks everything again when it saves; what a dialog shows as free is what was free a
moment ago.

## Unscheduled work

`GET api/workforce/plan/unscheduled`: the caller's own open work that is not yet in their plan. It
is empty for anyone without `schedule.manage`.

"Mine" means what My work means: tickets the caller **holds**, or that sit with a **team the caller
is in** (`assignedToMe` false, `teamName` set, shown as "with NOC"), narrowed to what the caller may
see, exactly as the ticket list is. **Open** means not finished. **Not yet in my plan** means the
caller has no `Planned` allocation on it that ends in the future; work planned only in the past is
still unscheduled and says how much was planned before (`plannedMinutesSoFar`, every planned minute
on the ticket by anyone, so "unscheduled" is not mistaken for "untouched"). Work planned for someone
else only is still on the holder's list.

Ordered by SLA due time (those with one first), then newest; at most 100. Each row: reference,
title, client, priority, status, source, due time, and "Add to plan". When there is nothing:
"Everything you hold is planned, or there is nothing open. Work that sits with your team counts as
yours to plan." Proven by `Unscheduled_work_is_mine_open_and_not_yet_in_my_future_plan`.

## Internal work

`POST api/workforce/plan/internal-work` raises a ticket on one of the team's own boards **in the
caller's name** (created by and assigned to them) and plans it in the caller's own time, in one
step. It needs `schedule.manage` (any scope). The ticket is real work on a real board: the board
service checks the board (active; its membership if it is limited to members), the client and the
title (1 to 500 characters) exactly as "New ticket" does, and the ticket gets the board's number
(`INT-000124`). The allocation is self-planned and flexible, and the same period and conflict rules
apply - checked **before** the ticket is raised, under the caller's gate, so a refused time raises
nothing and "Override and plan" never leaves a second ticket behind; the ticket and the plan are
then written in one transaction. Proven by
`A_small_piece_of_internal_work_is_raised_on_the_board_and_planned_in_one_step` and
`A_refused_placement_changes_nothing_about_the_ticket_and_a_refused_internal_task_raises_none`.

## Notifications

When **someone else** changes a person's plan, the person is told through the push notifications
they already have, kind `WorkPlanned`:

| Event | Title | Body |
|---|---|---|
| Work placed for them, or given to them | "Work planned for you: INT-000123" | "{title} · 5 Oct 2026 14:00–15:30 (UTC)" |
| Their work moved | "Planned work moved: INT-000123" | "{title} · now {when}" |
| Their work given to someone else | "Work taken out of your plan: INT-000123" | "{title} is now planned for Abbie Noor." |
| Their work taken out | "Planned work taken out: INT-000123" | "{title} · was {when}" |

Only when they have a device signed up, and only if they want this kind of news: `WorkPlanned`
follows the same choice as "work assigned to you" (`PushPreference.Assigned`). Nobody is told about
their own planning. The notification opens My plan on the work's date
(`/dashboard/workforce/my-plan?date=yyyy-MM-dd`). The row is written with the change; the worker's
push pass sends it.

## Audit

Every entry carries the request's correlation id. Times in entries are written in the planned
person's own zone: "5 Oct 2026 14:00–15:30 (Europe/London)".

| Action | When | Detail |
|---|---|---|
| `workforce.allocation.created` | Work placed (incl. internal work) | person, ticketId, reference, method, when, isFixed, overrideReason, overridden (types), warnings, holderSet |
| `workforce.allocation.moved` | A `PUT` that changes the start (with or without the end): a move | person, reference, before, after, isFixed, overrideReason, overridden, warnings |
| `workforce.allocation.resized` | A `PUT` that keeps the start and changes only the end: a resize (Phase 4; the team scheduler's edge handle, or a longer duration in Move planned work) | the same fields as `moved` |
| `workforce.allocation.changed` | A `PUT` that leaves the time alone (note-only or fixed-only) | the same fields; overrideReason and overridden are null |
| `workforce.allocation.reassigned` | Work given to someone else | reference, from, to, before, after, overrideReason, overridden, warnings, holderSet |
| `workforce.allocation.cancelled` | Work taken out by a person | person, reference, was, reason |
| `workforce.allocation.released` | The worker took finished work out of future plans: one entry per pass, no entity id | count, released (id, ticketId, reference, status, appUserId, was) for each |
| `ticket.assigned.portal` (existing) | Holder bridging | externalTicketId, appUserId, displayName, `viaPlanning: true` |

Notes are not written to the audit log; override and cancel reasons are.

## API

All under `api/workforce` (`WorkforcePlanController`). Every route needs `schedule.view`; every
change also needs `schedule.manage`. Whose plan the caller may touch is decided per person in the
service by that permission's scope. With the module off every route is 404; a sign-in that is not a
staff account is 403 "Only staff accounts can use the workforce module."

| Route | Permission | Body / query | Returns |
|---|---|---|---|
| `GET people/{id}/plan?from&to` | schedule.view | Dates; the person's today when none given; at most 31 days | `PersonPlanDto`: the person, zone, today, `days` (the Phase 2 capacity per day, planned work counted), `allocations` (those starting on those dates in the person's zone, `Planned` only), `canPlan`, `canScheduleOthers`, `canOverride` |
| `GET plan/unscheduled` | schedule.view | | `UnscheduledWorkDto[]` |
| `GET plan/people` | schedule.view | | `PlannablePersonDto[]`: `appUserId`, `displayName`, `isSelf`, `timeZone`, `isSchedulable` |
| `GET tickets/{ticketId}/plan` | schedule.view | | `WorkAllocationDto[]` on that ticket for the people the caller may see, planned first, at most 50 (cancelled rows included; the screen shows planned ones) |
| `POST plan` | + schedule.manage | `{ ticketId, appUserId, start, end, isFixed?, note?, overrideReason? }` | `WorkAllocationDto` |
| `POST plan/internal-work` | + schedule.manage | `{ boardId, title, description?, clientCompanyId?, start, end, priority?, note?, overrideReason? }` | `WorkAllocationDto` |
| `PUT plan/{allocationId}` | + schedule.manage | `{ start, end, version, isFixed?, note?, overrideReason? }` (`note` null keeps the note) | `WorkAllocationDto` |
| `POST plan/{allocationId}/reassign` | + schedule.manage | `{ appUserId, version, start?, end?, overrideReason? }` (no times keeps the current ones) | `WorkAllocationDto` |
| `DELETE plan/{allocationId}?reason=` | + schedule.manage | Optional reason, 200 characters | `WorkAllocationDto` (status 5) |

`WorkAllocationDto`: `id`, `appUserId`, `personName`, `ticketId`, `ticketVisible`, `reference`,
`title`, `clientName`, `ticketStatus`, `ticketFinished`, `startsAt`, `endsAt`, `plannedMinutes`,
`timeZone`, `status` (1 planned, 5 cancelled), `method` (1 self, 2 authorized user, 3 automation),
`scheduledByUserId`, `scheduledByName`, `isFixed`, `note`, `overrideReason`, `overriddenConflicts`
(conflict type numbers), `overriddenByName`, `cancelledAt`, `cancelledByName`, `cancelReason`,
`version`, `canEdit`, `canCancel`, `canReassign`. The ticket's facts (`reference`, `title`,
`clientName`, `ticketStatus`) come along **only when the asker may see that ticket**
(`ticketVisible`); otherwise they are null and the asker learns that the time is taken, nothing
more. Instants are UTC with the zone to show them in.

Status codes:

| Code | `title` | When |
|---|---|---|
| 200 | | Done; the body is the result |
| 400 | `validation_failed` | A period, note or reason rule; placing fixed work as a non-scheduler; a finished ticket; work already out of the plan; giving work to its own person; the holder-bridging refusal |
| 403 | `forbidden` | No right to plan this person's work or to change this plan; moving fixed work scheduled for you; taking scheduled work out; fixing or freeing work on an update as a non-scheduler; giving work away as a non-scheduler; not a staff account |
| 404 | `not_found` | Person, ticket or planned work outside the caller's reach or nonexistent (the same words for both); module off ("Workforce was not found.") |
| 409 | `conflict` | A conflict (with `payload`), or a stale version (`payload.stale`) |

## Storage

Table `work_allocations` (migration `20261003033116_WorkforceWorkAllocations`, additive; `Down`
drops the one table):

```
WorkAllocation   work_allocations   tenant | TicketId (FK tickets, cascade) | AppUserId (FK app_users, cascade)
                                    | StartsAt | EndsAt (instants) | PlannedMinutes | Status | Method | ScheduledByUserId
                                    | IsFixed | Note (300) | OverrideReason (300) | OverriddenConflicts (200) | OverriddenByUserId | OverriddenAt
                                    | CancelledAt | CancelledByUserId | CancelReason (200) | UpdatedByUserId | Version (concurrency token)
```

Indexes: `(AppUserId, StartsAt)` ("this person's plan around these dates", every read the capacity
engine and My plan make), `(TicketId)` (the ticket page), `(MspOrganizationId, StartsAt)` (the
worker's pass). A piece of work is at most a day long, so the engine reads from one day before the
range and the index answers without a scan. The table is a tenant entity with the global query
filter. The registered `IWorkAllocationReader` is now `WorkAllocationReader`, which replaces Phase
2's empty one: every allocation is confirmed work (there is no pencilled-in state), and a future
allocation on a finished ticket is left out at once.

## Tenant isolation and client security

Client ticketing and workforce scheduling are separate security domains. No client role can hold
`schedule.view`, `schedule.manage` or `schedule.override`; the keys are staff-only in the catalogue
and in every role's defaults, and a client account's requests to any `api/workforce` route are
refused (403) before any id is looked at. What the tests prove:

| # | Guarantee | Proven by |
|---|---|---|
| 1 | No client role or pure client login holds any workforce key, `schedule.manage` and `schedule.override` included | `EndpointAuthorizationTests.No_client_role_or_client_login_holds_a_workforce_permission` |
| 2 | No client account can reach any action of any controller under `api/workforce`, found by route, so `WorkforcePlanController` (and one added later) is covered | `EndpointAuthorizationTests.No_client_account_can_reach_any_workforce_endpoint` |
| 3 | Each planning action demands exactly `schedule.view` (reads) or `schedule.manage` (changes) | `EndpointAuthorizationTests.Sensitive_endpoints_require_exactly_these_permissions` (the `WorkforcePlanController` rows) |
| 4 | No shape the ticket API or the client portal returns carries a planning field (`Allocat`, `Planned`, `MyPlan`, `Override`, `Schedul`, `Capacity`, …) | `EndpointAuthorizationTests.Nothing_a_client_can_receive_carries_workforce_planning` |
| 5 | A sign-in that is not a staff account is refused by every planning action even if it held every claim; with the module off every action is "not found" | `WorkPlanTests.A_sign_in_that_is_not_a_staff_account_is_refused_by_every_planning_action` |
| 6 | Another organization's people, tickets and allocations are "not found" for reading a plan, placing, moving, giving away and cancelling, and on a ticket; its unscheduled list is empty; nothing is written | `WorkPlanTests.Another_organizations_plans_cannot_be_read_written_or_detected` |
| 7 | Self-planning reaches only tickets the person may already see; a restricted ticket and a guessed id get the same words; another person's plan is "not found" | `WorkPlanTests.Self_planning_reaches_only_work_the_person_may_already_see` |
| 8 | What is planned on a ticket lists only people the asker's `schedule.view` scope reaches, and a ticket outside the asker's ticket scope is "not found" | `WorkPlanTests.What_is_planned_on_a_ticket_shows_only_people_the_asker_may_see` |
| 9 | Nobody is planned on a ticket they cannot open; a ticket held by someone else is never quietly re-held | `WorkPlanTests.Work_held_by_someone_else_is_not_quietly_moved_and_a_person_who_could_not_open_it_is_not_planned_on_it` |
| 10 | In the browser, an account without the scheduling permission has no Workforce menu, My plan and a person's Plan tab refuse and reveal no ticket, the ticket page has no Planned work panel, and every plan endpoint answers 403 | `apps/web/e2e/workforce-plan.spec.ts`: "an account without the scheduling permission is refused My plan and every planning endpoint" |

Also: a conflict never carries a ticket's title, client or number, and names the blocking work only
to a caller who may see it (Phase 2, `ConflictEvaluatorTests`); a plan withholds a ticket's facts
from an asker who may not see it (`ticketVisible` false, "Work you cannot open"); planning never
changes PSA facts and queues nothing for a provider
(`An_Autotask_ticket_is_planned_as_itself_with_nothing_copied_and_nothing_sent`).

Request bodies are explicit records, never entities. Every rule is enforced on the server. The
`DELETE` reason is a query string value, as a ticket number is; nothing personal travels in a URL.

## Performance

`CapacityPerformanceTests.Planned_work_costs_the_same_number_of_queries_whatever_the_number_of_allocations`
seeds 50, 100 and 500 people with one, two and three pieces of planned work per weekday over four
weeks (1,000, 4,000 and 30,000 allocations), runs the real stack (allocations from the table,
ticket visibility through the real scope query) and counts every database command. The number of
queries is **the same at every size**, and is asserted in every CI run:

| Request | Queries (asserted ceiling) |
|---|---|
| One person's plan for a week | 40 |
| Team capacity for one day (planned work counted) | 17 |
| Find available technician, 14 days | 14 |
| What is planned on a ticket | 21 |
| Place one piece of work (gate, conflicts, write, audit) | 50 |

The counts are higher than the Phase 2 reads because every answer now also resolves ticket
visibility and the caller's planning rights. No query is made per person, per day or per allocation.

Measured on **SQLite, on a development machine** (3 October 2026; the test prints them, and asserts
only loose ceilings so a slow CI runner cannot fail it). They show the shape of the cost, not
production latency:

| People | Allocations | One person's week | Team day | 14-day search | On a ticket | Place work |
|---|---|---|---|---|---|---|
| 50 | 1,000 | 40 queries, 13 ms | 17, 33 ms | 14, 64 ms | 21, 8 ms | 50, 15 ms |
| 100 | 4,000 | 40, 24 ms | 17, 92 ms | 14, 182 ms | 21, 11 ms | 50, 23 ms |
| 500 | 30,000 | 40, 355 ms | 17, 473 ms | 14, 736 ms | 21, 56 ms | 50, 160 ms |

(The 500-person case ran first and carries the one-off warm-up.) The same class runs on PostgreSQL
with `DESK_TEST_POSTGRES` set ([testing.md](testing.md)), where
`Two_requests_for_the_same_hour_on_a_real_database_end_with_one_booking` also runs. The Phase 2
PostgreSQL figures for the capacity reads are in [capacity.md](capacity.md#performance).

Limits that bound any one request: 31 days of plan, 100 unscheduled tickets, 50 allocations on a
ticket, 15 picker results, one piece of work of at most 24 hours, 500 releases per organization per
pass.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| "This plan changed since you opened it. Reload and try again." | Someone (or you, in another tab) changed this piece of work since the screen loaded; the version sent is stale | Reload and make the change again |
| "… cannot open this ticket: it is held by …" | The person you are scheduling cannot see the ticket, and someone else holds it in the portal | Hand the ticket over to them first (or plan it for the holder) |
| "This time cannot be used: Marked unavailable during this period." | A block: the person has time away then | Change or remove the time away, or choose another time; nobody can override a block |
| "This time cannot be used: This person is not offered for planned work." | A block: "offered for planned work" is off, or the account is inactive | Switch it back on (Work schedule tab) |
| "This time is no longer available: …" | An overridable conflict and you cannot override | Choose a free window, or ask someone with `schedule.override` |
| "This time has a conflict: … Give a reason to override it." | You may override but gave no reason (or one under 5 characters) | Type a reason of at least 5 characters; the button becomes "Override and save" |
| Work on a finished ticket still shows in a future plan | The release pass runs every 5 minutes | Wait up to 5 minutes. Capacity already ignores it; the row shows "Ticket finished" meanwhile |
| "This ticket is finished; there is nothing left to plan." | The ticket is resolved or closed | Reopen it first, or plan other work |
| "You can't plan your own work." / no "Add to plan" button | The role has no `schedule.manage` | Give the Technician role `schedule.manage` at scope Own |
| "You can't plan work for this person." / "Person was not found." | Your `schedule.manage` scope does not reach them | A wider scope (Team, Department, All) in Roles & Permissions |
| "This work was scheduled for you and is fixed in place…" | A scheduler fixed it | Ask them; only a scheduler moves fixed work |
| "This work was scheduled for you. Ask whoever planned it to take it out…" | Only a scheduler takes out scheduled work, fixed or not; the person may move it if flexible | Ask whoever planned it |
| "Only someone who schedules others can fix work in place." | Fixed means nothing on your own work | Leave Fixed off |
| A ticket is missing from "Unscheduled work of mine" | It is not held by you or a team you are in, is finished, or already has your planned work ending in the future | Check My work; take the ticket; or look at the plan |
| "Work can be planned from yesterday onwards, not earlier." | The start is more than 24 hours ago | Plan from yesterday on; log past time on the ticket instead |
| "There is no internal board to raise it on yet." | No active internal board | Create one under Internal boards |
| A notification opened My plan on an unexpected day | The link carries the date the work starts in the person's own zone on that day | Check the person's time zone on their Work schedule tab |
