# My Day and actual work time (Phase 6)

What was planned against what actually happened: a technician's day, a clock on a piece of work
(start, pause, resume, stop), the actual time that becomes the ticket's time entry, time typed in
by hand, corrections with a reason, and a manager's "team today". **Internal only**: no route, shape
or screen here is reachable by a client account.

**This is not attendance.** A clock is attached to a ticket, never to a person's presence. The
module records work, time, technician, work status, work note and the plan relationship, and
nothing else: no clock-in or clock-out, no keystrokes, screenshots, browsing, mouse activity,
camera, microphone, location or idle telemetry. A working window of 08:30–17:30, 6 h planned and
5 h 20 m logged are three different facts, and none of them says when anyone arrived or left.

- [Overview](#overview)
- [The time architecture that already existed](#the-time-architecture-that-already-existed)
- [The work session](#the-work-session)
- [One running clock](#one-running-clock)
- [Pause, resume, waiting](#pause-resume-waiting)
- [Stop: the clock becomes a time entry](#stop-the-clock-becomes-a-time-entry)
- [Actual time: the one rule](#actual-time-the-one-rule)
- [Manual time and corrections](#manual-time-and-corrections)
- [Planned against actual](#planned-against-actual)
- [Unplanned, after-hours and overnight work](#unplanned-after-hours-and-overnight-work)
- [Actual time and billable time](#actual-time-and-billable-time)
- [The PSA: pushing, duplicates, failures](#the-psa-pushing-duplicates-failures)
- [Refresh, two tabs, two devices, double clicks](#refresh-two-tabs-two-devices-double-clicks)
- [Screens](#screens)
- [API](#api)
- [Storage](#storage)
- [Permissions, client isolation, tenant isolation, privacy](#permissions-client-isolation-tenant-isolation-privacy)
- [Audit](#audit)
- [Performance](#performance)
- [Deferred](#deferred)
- [Troubleshooting](#troubleshooting)

## Overview

The employee's day, from the work that reaches them to the comparison with the plan.

```
   Assigned . Scheduled . Reactive work
                     |
                     v
                  MY DAY
                     |
               Start work        one clock at a time; starting a second asks:
                     |           return, pause the first, or stop the first
          +----------+----------+
       Pause (waiting)      Continue
          +----------+----------+
                     v
             Stop the clock  <----------------- or: Add time (minutes typed in, no clock)
                     |
                     v
       Actual worklog = ONE ticket time entry
       (dated the start; the clock's seconds; the person; the note)
                     |
          +----------+----------+
    board ticket:            PSA ticket: the SAME entry,
    stays here               pushed to the PSA once (retried if rejected)
                     |
                     v
      Planned against actual, on My day and Team today
      (the day's entries plus any clock still running; variance, or "not planned")
                     |
                     v
          Finish the ticket   (separate, optional: the existing status change, after the stop)
```

Three things the picture leaves out, each covered below:

- **Stop and Finish are two different acts.** Stop ends the clock and logs the time; it never changes
  the ticket. Finish is the existing status change (resolve, with its review and resolution rules),
  offered after a stop ([Stop: the clock becomes a time entry](#stop-the-clock-becomes-a-time-entry)).
- **The portal's actual time and the PSA's time entry are one record, not two.** A stopped clock, or
  minutes typed in, becomes one ticket time entry; it is the portal's actual time, and for a PSA
  ticket that same entry is pushed to the PSA, kept here as "not in the PSA yet" when the push fails
  and retried without a duplicate ([The PSA: pushing, duplicates, failures](#the-psa-pushing-duplicates-failures)).
- **Paused time is not in the picture because it is not in the count.** Only the clock's segments
  are actual time; a pause, with or without a reason, adds nothing
  ([Pause, resume, waiting](#pause-resume-waiting)). And nothing here is attendance.

## The time architecture that already existed

Inspected before anything was written, and reused rather than duplicated:

| Concept | Verdict | What it is |
|---|---|---|
| Recorded time | **Reuse** | `TicketTimeEntry` (`ticket_time_entries`): hours (4 decimals), billable, notes, work type and role, the portal author (`AppUserId`), `Source` (Portal / Provider), `SyncStatus` (Pending / Synced / Failed) with the PSA's words in `SyncError`, `ExternalEntryId`, `EntryDate`, the reply it was logged with (`NoteId`). The PSA is the system of record for hours. |
| Logging, editing, deleting, retrying time | **Reuse, extracted** | `TicketTimeController` (`api/tickets/{id}/time…`, every action `tickets.time.log`): the push to the PSA, the technician identity, the work-type label, the ticket's totals and the audit now live in `TicketTimeWriter`, which the controller and a stopped clock both log through. The controller keeps its routes, rules and messages. |
| PSA time | **Reuse** | `IServiceManagementConnector.Get/Add/Update/DeleteTimeEntryAsync`; Autotask (`hoursWorked`, `isNonBillable`, `hoursToBill`, `billingCodeID`, start/end stamped by the connector) and ConnectWise (`actualHours`, `billableOption`, `workType`, `timeStart`) implementations; the sync runner's `te-` time notes and the `ExternalEntryId` skip list. |
| Ticket notes and visibility | **Reuse** | A time entry's note is the entry's note (internal to the provider's time stream); client visibility stays the ticket-note rule (`IsPublic`), which no part of this phase touches. |
| Planned work | **Reuse** | `WorkAllocation` (Phase 3–5) and the person's capacity day (`DayCapacityDto`). |
| The browser timer | **Replaced** | `TimerProvider` kept a counter in `localStorage` that the server never saw. It is now a thin alias over the server-backed clock below; the old key is simply no longer read. The clock needs the Workforce module and `schedule.view`: an installation with the module off has no header clock, and hours are typed into the reply or the time panel as before. |
| Work session, segment | **New** | The only new tables. |
| Attendance, surveillance | **Not required** | Not built, by design. |

Two defects in the old path were fixed on the way: the header timer and the reply composer posted a
work type's **label** where the PSA wants its **id** (Autotask silently dropped it), and a lead
could rewrite a technician's hours without saying why.

## The work session

```
WorkSession          one per started piece of work: person, ticket, the planned allocation it came from (or none),
                     Status (Active / Paused / Completed / Cancelled), PauseReason, StartedAt, EndedAt,
                     ActiveSeconds, TimeEntryId (once stopped), Note, Version
  1-*  WorkSessionSegment   one run of the clock: StartedAt, EndedAt (null while running), Seconds
```

Time runs only inside segments. **The server's timestamps are the truth**: a start, pause, resume or
stop writes a state change, and that is all that is ever sent; nothing streams ticks. The screen
draws the clock from `activeSeconds` (the closed segments) plus the time since `runningSince` (the
open segment), by its own clock, for display only.

Starting needs the ticket to be one the caller may **log time on** (`tickets.time.log` through the
ticket scope, exactly as the time panel), not finished ("This ticket is finished; there is
nothing left to work on.") and, for a PSA ticket, accepted by the PSA ("This ticket is not yet
synced to the PSA, so time cannot be logged.": its hour could never reach the PSA). An allocation may be named (the planned work it is started from); it
must be the caller's own, on that ticket. Starting from the day's planned item passes it; starting
from the unscheduled list or a ticket page passes none, which is how unplanned work is told apart.

## One running clock

A person has at most one **Active** session; paused ones may be several. The rule is held three
ways: the person's `PlanningGate` (a row lock on PostgreSQL, a per-person semaphore elsewhere)
serialises every state change; a partial unique index on `work_sessions (AppUserId) WHERE Status = 1`
holds it in the database (PostgreSQL and SQLite); and every change carries the `version` the screen
saw.

Starting or resuming while something else runs answers **409** with `ActiveWorkProblemDto`: the
running work (reference, title, since when) and the two ways on. The screen asks:

```
You already have active work on PIO-1032.
[Return]  [Pause current and start new]  [Stop current and start new]
```

`switch` on the request says which (`PauseCurrent` keeps its time for later; `StopCurrent` logs its
time now, with no note), and `currentId` names the clock the person agreed to pause or stop: a
different one running by then is refused as stale. Nothing is stopped without the person saying so. Starting the ticket that
is already running returns the same session (a double click, a second tab); starting a ticket that
is paused resumes it: there is never a second session on the same ticket.

## Pause, resume, waiting

A pause closes the open segment; its whole seconds join `ActiveSeconds`; the status is Paused with
an optional reason (**waiting on client / vendor / reboot / third party**, or none). A resume opens
the next segment. Paused time counts for nothing:

```
09:00–09:35  35 m   paused
10:00–10:25  25 m   actual = 60 m, not 85
```

Waiting is a pause with a reason, kept in the audit entry and shown on the clock; it is not a
separate state and it is never working time.

## Stop: the clock becomes a time entry

`POST work/{id}/stop` with `{ version, note?, billable (default true), workType?, workRole?, discard }`.
The open segment closes, `EndedAt` is set and:

- **under a minute, or discarded**: the session is Cancelled; nothing is logged; the audit entry says which.
- otherwise: the session is Completed and `TicketTimeWriter.LogAsync` writes **one** `TicketTimeEntry`
  for it: `Hours = ActiveSeconds / 3600` rounded to four decimals (a third of a second), the note,
  billable, work type and role, dated the moment the clock started, authored by the person, with
  `WorkSessionId` set. The session points at the entry (`TimeEntryId`). A board ticket's entry is
  Synced at once; a PSA ticket's waits as Pending and is pushed **after the gate is released** (a
  slow provider never holds the person's lock); a rejection leaves it Failed with the provider's
  words, retryable from the ticket's time panel as any other.

**Stopping is not completing.** The ticket's status is never changed by the clock. "Complete work"
on the screens is the existing status change, offered after the stop, through the existing
normalised statuses and the existing rules (review step, resolution note); nothing here invents a
PSA status mapping.

## Actual time: the one rule

A person's actual time on a day = **their time entries dated that day** (synced, pending or failed:
rejected time is still work, marked "not in the PSA yet") **plus their live clocks** (Active or
Paused sessions that have not become entries yet, as the closed segments plus the open one by the
server's clock). A stopped session's seconds are in its entry and nowhere else, so a minute is never
counted twice; a cancelled session counts for nothing; a deleted entry takes its minutes with it.

A clock belongs to the day it **started** on: a day's items and figures hold the entries dated that
day and the live clocks that started that day; the running clock itself is recovered on every day
viewed (the header, the Now card). Viewing yesterday while a clock runs today adds nothing to
yesterday.

Time entered directly in the PSA has no portal row today (it reaches the ticket's totals and its
time notes) and is not in a day's figure; that is the same rule technician productivity uses.

## Manual time and corrections

**Add time** (My Day, a row or the unscheduled list) is the existing `POST api/tickets/{id}/time`:
minutes typed in, a note, billable, the work type by id. It needs the same permission, the same
ticket scope and the same validation (hours 0.01–1000, notes ≤ 2000, a board ticket's date up to 30
days back and never ahead; a PSA ticket's date is the PSA's), and it joins the day's actual time
like any other entry.

**Corrections** are the existing `PUT api/tickets/{id}/time/{entryId}` (hours, billable, notes) with
one new field, `reason`:

- a person corrects their own time freely;
- a board lead (`boards.manage`) changing someone else's **must say why** (5–300 characters):
  "Say why the time is being changed (at least 5 characters)."; anyone else is refused as before;
- the audit entry `ticket.time.edited` keeps the entry, `from` and `to`, the `reason`, who changed it
  (`byUserId`) and whose hour it was (`forUserId`). Nobody silently rewrites recorded work, and the
  original value is always in the trail.

## Planned against actual

`GET api/workforce/my-day` groups the day **by ticket**: every allocation of the person that starts
that day (in their zone) is a slot of its ticket's item; the item's planned minutes are the confirmed
slots' sum (tentative shown apart); its actual seconds are the rule above for that ticket; then:

```
variance  = actual − planned           (minutes; +35m over, −8m under)
variance% = (actual − planned) / planned × 100     only when planned > 0
```

When nothing was planned the item says **not planned**, carries no variance and counts towards the
day's unplanned actual time. Raw seconds and minutes are in the answer; the screen rounds.

An item's state is derived: **Completed** (the ticket is finished), **Active** (its clock runs),
**Paused**, **In progress** (time recorded, ticket open), **Planned**. The summary is facts:
planned, tentative, actual, unplanned actual, completed / in progress / not started, remaining
planned (for each unfinished item, what is left of its planned time after the actual). Nothing is a
score, nobody is ranked, and a long task is a long task: an estimate can be wrong, a client can be
slow, a reboot can take an hour.

**Actual past planned.** A clock running past its last slot's end is never cut off; the item says
"past its planned end; the next work may be affected" (`overPlannedEnd`) and the next planned item
is shown under *Next*. Completed work stays on the day's list with its actual time.

## Unplanned, after-hours and overnight work

A critical ticket at 11:00 is started at once from the unscheduled list or its ticket page; its item
is *not planned*, its time is unplanned actual time, and the derived classification (planned /
reactive) comes from whether a session names an allocation, never from a stored flag. A clock
started outside the person's working window (or on a day off) is recorded as it was and marked
**outside the schedule** (`outsideSchedule`), a fact, never "overtime". A clock across midnight
belongs to the day it **started** on (its entry is dated the start); its seconds are instants, so a
clock change in the night changes nothing.

## Actual time and billable time

The session's seconds, and the entry's four-decimal hours, are the **actual** time. What the PSA
bills (quarter-hour increments, `hoursToBill`, a billable option) is the PSA's and is read back from
it on the ticket's time panel; the portal never rounds actual time into billable time, and never
overwrites what it recorded with what was billed. Both are shown where both exist.

## The PSA: pushing, duplicates, failures

Policy today, as before this phase: a PSA ticket's time is pushed to the PSA (`SYNC_TO_PSA`), a
board ticket's stays here (`PIO_ONLY`); the PSA stays authoritative for the hours it holds. Nothing
new is hard-coded: the writer does what the controller did.

**Duplicate protection**: one entry per session is a database rule (a unique index on
`WorkSessionId`); a push happens once per entry and stamps `ExternalEntryId`; the time panel merges
the PSA's entries with the portal's by that id, so the entry the PSA hands back on every read is
shown once, as the portal's; the sync runner skips portal-origin entries when it imports time
notes. A retry re-sends the **same** entry and never creates another (`Time_on_a_PSA_ticket_is_pushed_once_and_a_rejected_push_keeps_the_time_for_a_retry`).

**Failed push**: the entry stays, Failed, with the provider's words; the session is Completed with
`timeEntrySyncStatus` and `timeEntrySyncError` on it; My Day marks the item "not in the PSA yet"
and counts the time; the existing Retry on the ticket's time panel sends it again.

## Refresh, two tabs, two devices, double clicks

- A reload asks `GET work/active` and redraws the clock from the server's timestamps; nothing is
  created by asking.
- Two tabs starting different tickets at once: the gate serialises them, one clock runs, the other
  is told (`Two_tabs_or_two_devices_starting_work_at_once_end_with_one_clock`). The same ticket from
  both: the same session.
- Two stops at once: one wins; the other is told the work changed since the screen loaded (a stale
  `version`) and nothing is logged twice.
- A device whose clock is wrong draws a wrong number of seconds; the logged time is the server's.

## Screens

**My day** (`/dashboard/workforce/my-day`, first in the Workforce navigation): the day's summary;
*Now* (the running or paused item with its clock) and *Next*; *Today*: every item with its slots,
state chip, planned, actual, variance, the clock, "past its planned end", "outside working hours",
"not in the PSA yet", and **Start work / Pause / Resume / Stop** and **Add time**; *Unscheduled my
work* with **Start work** and **Add time**. Day navigation; a manager opens someone's day from Team
today (`?person=`), read-only for the work (a lead may still stop a runaway clock).

**The clock in the header** (`ActiveWorkWidget`, in place of the old timer): the running work, its
clock, Pause / Resume / Stop; a paused clock shows why; a quiet *My day* link when nothing runs.

**Stop work** dialog: the time, a work note, billable, the work type; *Stop and log time*, *Discard*
(confirmed), *Keep running*; under a minute it says nothing will be logged.

**Work already running** dialog: Return / Pause current and start new / Stop current and start new.

**On a ticket**: *Start work* (or Pause / Stop for its own clock) beside the reply's time field;
the reply's hours stay for time typed in by hand.

**Team today** (`/dashboard/workforce/team-today`): the people the viewer may see, each with their
current work and its clock, planned today, actual logged, done / in progress / not started, with the
team, department and skill filters; "Work facts only: no attendance, no ranking."

## API

All under `api/workforce` behind `Features:Workforce`; every action needs `schedule.view`; the
module refuses client accounts before any of these run.

| Route | Permission | Does |
|---|---|---|
| `GET work/active` | `schedule.view` | the caller's running and paused sessions, running first |
| `POST work/start` `{ ticketId, allocationId?, switch? }` | `tickets.time.log` on the ticket | starts (or returns the running / resumes the paused) session; 409 `ActiveWorkProblemDto` |
| `POST work/{id}/pause` `{ version, pauseReason? }` | `tickets.time.log` | closes the open segment |
| `POST work/{id}/resume` `{ version, switch? }` | `tickets.time.log` | opens the next; 409 as start |
| `POST work/{id}/stop` `{ version, note?, billable?, workType?, workRole?, discard? }` | `tickets.time.log` | the clock becomes a time entry, or nothing |
| `GET my-day?appUserId&date` | `schedule.view` (Own: oneself; wider: someone in scope) | `MyDayDto` |
| `GET team-today?date&teamId&departmentId&skills&matchAll` | `schedule.view` | `TeamTodayDto` |
| `PUT api/tickets/{id}/time/{entryId}` `{ hours?, billable?, notes?, reason? }` | `tickets.time.log` | as before, with the reason rule |

Reads are GETs and change nothing; every clock change is a POST (pinned in `EndpointAuthorizationTests`).

## Storage

One additive migration, `20261003094908_WorkforceWorkSessions`:

```
work_sessions           tenant | AppUserId | TicketId (-> tickets, cascade) | AllocationId | Status | PauseReason | StartedAt | EndedAt
                        | ActiveSeconds | TimeEntryId | Note (2000) | UpdatedByUserId | Version (concurrency token)
                        indexes: (AppUserId, Status); (MspOrganizationId, AppUserId, StartedAt); TicketId;
                                 UNIQUE (AppUserId) WHERE "Status" = 1        one running clock per person
work_session_segments   tenant | SessionId (-> work_sessions, cascade) | StartedAt | EndedAt | Seconds
                        index: (SessionId, StartedAt)
ticket_time_entries     + WorkSessionId (nullable), UNIQUE WHERE NOT NULL   one entry per session
```

`Down` drops the two tables, the index and the column. Precision: segments are whole seconds from
instants; the entry's hours keep four decimals; durations are rounded only where they are shown, so
7 m 31 s + 7 m 31 s is 15 m 02 s, never 16 m.

## Permissions, client isolation, tenant isolation, privacy

No new permission key. The conceptual keys map onto what exists, so nothing is duplicated:

| Concept | Is |
|---|---|
| worktime.self.view | `schedule.view` at Own (every staff role) |
| worktime.self.start / manage | `tickets.time.log` on the ticket (Technician: All by default) |
| worktime.team.view | `schedule.view` wider than Own (Team, Department, All) |
| worklog.self.create / edit | `tickets.time.log` (own entries) |
| worklog.team.edit | `boards.manage` (a lead; with a reason) |

Whose session: the person's own, or a board lead's to pause or stop (`canControl`); anyone else is
"Work session was not found." A person outside the caller's scope is "Person was not found."; a
ticket outside their scope "Ticket was not found."

**Client isolation.** No client role holds `schedule.view`; the client-role walk refuses all seven
actions; the client-shape guard now also forbids `Session`, `Segment`, `ActualSeconds`, `MyDay`,
`TeamToday` and `Variance` in any ticket, control-panel, knowledge or attachment shape. A time
entry's note stays internal; a client sees a ticket's note only by the existing `IsPublic` rule.

**Tenant isolation.** Sessions and segments are tenant entities under the global filter; another
organisation's administrator cannot start on, read, pause, stop or enumerate a session, read a
person's day or see them in Team today (`A_finished_ticket_takes_no_clock_and_nobody_outside_reaches_a_session`).

**Privacy by design.** Collected: ticket, technician, timestamps of the clock's state changes,
pause reason, note, allocation link. Not collected and not collectable here: anything about the
device or the person's presence. Session history (A paused, B done, A resumed) is kept as the record
of work and is **not** interpreted.

## Audit

| Event | Detail |
|---|---|
| `workforce.session.started` | reference, ticket, allocation, at, planned (whether it came from the plan) |
| `workforce.session.paused` | reference, at, reason, activeSeconds |
| `workforce.session.resumed` | reference, at |
| `workforce.session.stopped` | reference, at, activeSeconds, hours, entryId, planned |
| `workforce.session.cancelled` | reference, at, activeSeconds, reason (discarded / under a minute) |
| `ticket.time.logged` (existing) | the entry, plus `context` { sessionId, activeSeconds, allocationId } for a clock's entry |
| `ticket.time.edited` (existing) | now with `reason`, `byUserId`, `forUserId` |

No audit noise from ticks: the browser sends none.

## Performance

`CapacityPerformanceTests` at 50 / 100 / 500 people (1,000 / 4,000 / 30,000 allocations), pinned
and constant; the relational test runs the whole clock on SQLite and PostgreSQL.

| Read | Queries | 50 people | 100 | 500 |
|---|---|---|---|---|
| My day (one person, a clock running) | 27 | 4 ms | 5 ms | 23 ms |
| Active lookup (the clock plus the day it started on) | 17 | <1 ms | <1 ms | 2 ms |
| Team today (everyone) | 27 | 23 ms | 44 ms | 349 ms |

A day is the person's capacity (Phase 2's fixed reads), their allocations, their entries, their
sessions and the ticket scope: constant. Team today is one query each over everyone. The screens
poll nothing per second; My day and Team today refetch once a minute and on focus.

## Deferred

| Not in this phase | Why |
|---|---|
| "Complete work" as one server action (stop + status + note) | The existing status change already carries the rules (review, resolution); the screen chains stop then status. |
| Session edits (changing a segment after the fact) | Corrections are made on the entry, with the reason rule; segments stay the record of what the clock did. |
| Importing PSA-side time entries as portal rows | A separate sync change; today they reach totals and time notes. |
| Idempotency keys on the manual time POST | The screens disable the button during the request; the clock's entries are unique by session. |
| Reactive-vs-planned analytics, utilisation, heatmaps | Phase 7. |

## Troubleshooting

| Symptom | Cause | Do |
|---|---|---|
| "You already have active work on …" | One clock runs at a time | Return, or pause / stop the current one from the dialog |
| "This work is not running." / "This work is not paused." / "This work is already stopped." | The action does not fit the session's state (often a second tab already did it) | Reload; the server's state is the truth |
| "This work changed since the screen loaded. Reload and try again." | A stale version: another tab or device changed the session | Reload |
| "This ticket is finished; there is nothing left to work on." | Start on a resolved or closed ticket | Reopen it first, or log time by hand if the rules allow |
| "That planned work is not yours, or not on this ticket." | An allocation id that is someone else's or on another ticket | Start without it |
| The clock stopped but no time entry appeared | Under a minute, or discarded: the session is Cancelled | Add the time by hand if it was real |
| "Not in the PSA yet" on an item | The entry's push failed or is pending | Retry from the ticket's time panel; the time is counted meanwhile |
| "Say why the time is being changed (at least 5 characters)." | A lead changing someone else's entry without a reason | Give the reason; it is kept with the change |
| Actual time on My day does not match the PSA's hours | The PSA bills in increments; the day shows actual seconds | Expected; both are shown on the ticket |
| Time logged in the PSA itself is missing from My day | PSA-side entries have no portal row | Expected today; see Deferred |
| The header clock is a few seconds off the day's figure | The device's clock, display only | The logged time is the server's |
