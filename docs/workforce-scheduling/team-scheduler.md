# Team scheduler (Phase 4)

Everyone a scheduler may see, side by side, for one day or one week: when each person works, what
is planned into their time, where the free windows are, and, beside it, the work that is in nobody's
plan yet. Work is placed by clicking a free window or pressing **Plan**, moved by dragging a block
along its row, given to someone else by dropping it on their row, lengthened or shortened by its
edge, and taken out from its panel. **Team schedule** under Workforce
(`/dashboard/workforce/schedule?date=&view=`).

It is the **second screen over the Phase 3 model** ([planned-work.md](planned-work.md)), not a
second model. There is no new write path: every drag, drop and resize ends in exactly the calls My
plan makes (`PUT plan/{id}`, `POST plan/{id}/reassign`, `POST plan`, `DELETE plan/{id}`), with the
same rules about fixed and flexible work, holder bridging, the gate, the version and overrides. What
the board shows is a proposal until the server has said yes; nothing drawn on it is a reservation.

**Internal only.** The page, both of its reads and every action are staff-only, behind
`schedule.view` and the module switch; a client account is refused before any id is looked at
([Permissions](#permissions), [Tenant isolation and client security](#tenant-isolation-and-client-security)).
A technician whose `schedule.view` is Own opens the same page and sees one row: their own.

## Overview

```
 Toolbar   Previous · Today · Next · date · Day | Week · Compact/Standard/Detailed · "Times in Asia/Kolkata"
           Find available technician · Unscheduled work (N) · Refresh
 Filters   team · department · skills (holds all / any)   |   search · source · client · status
                        |                                                 |
            GET plan/team?from&to&teamId&...                 applied in the browser to what was loaded
                        v
   +------------------------------------------------------------+     +----------------------------+
   | Person          | 08:00  09:00  10:00  ...  17:00  (zone)  |     | Unscheduled work (2)       |
   | JC Jason Carter | [Avail][ INT-000001 ][Avail][Break][...] |     | search · Everything        |
   |    NOC          |  2h planned / 8h · 6h free               |     | INT-000002  Board    Plan  |
   | AN Abbie Noor   | [Unavailable][  Autotask 43829  ][Avail] | <-- | Autotask 777  AT     Plan  |
   |    NOC · Europe/London   0h planned / 8h · 8h free         |     +----------------------------+
   +------------------------------------------------------------+        GET plan/unscheduled/team
     click a free window  -> Choose work to plan -> Plan work -> POST plan
     drag from the queue  -> Plan work, pre-filled with the row and the time -> POST plan
     drag a block along its row -> PUT plan/{id}          drop it on another row -> POST plan/{id}/reassign
     drag its right edge -> PUT plan/{id} (same start)     click it -> panel: Open ticket · Reschedule ·
                                                                      Give to someone else · Remove from plan
```

## Who it is for

Schedulers: anyone whose `schedule.view` reaches more than themselves (a team lead at Team scope, a
manager or administrator at All). The **Team schedule** link appears in the Workforce
sub-navigation, with Team capacity and Find available technician, only for someone who can see more
than themselves (`WorkforceNav`: the people list has more than one row). The page itself is open to
anyone with `schedule.view`: a technician reaching it by address gets their own row and nothing
else, and the two reads answer only what their scope reaches
(`The_team_scheduler_lists_the_people_the_asker_may_see_with_their_days_and_what_is_planned`;
e2e "a technician has no team schedule entry and the page shows only themselves"). Clients never
reach it: no client role holds `schedule.view`.

What the viewer may **do** on the board follows `schedule.manage`, per person and per piece of
work, exactly as in Phase 3 ([Permissions](#permissions)).

## What the screen shows

### The toolbar and the summary

| Control | What it does |
|---|---|
| **Previous** / **Today** / **Next** | One day or one week at a time (the buttons are "Previous day" / "Next day" or "Previous week" / "Next week"). **Today** asks for no date, so the server answers with today in the organization's zone; in Week view the week then starts on that day |
| **Date** | Any date; the URL's `?date=yyyy-MM-dd` opens the page on it (a link from elsewhere, or a bookmark) |
| **Day** / **Week** (a "View" group, `aria-pressed`) | The day board, or the week grid starting on the chosen date (seven days, not aligned to a Monday); `?view=week` opens the grid |
| **Time scale** (day view, not on phones) | Compact 48 px, Standard 80 px, Detailed 136 px per hour. Narrower blocks say less ([Blocks](#blocks)) |
| "Times in {zone}" | The organization's zone, which the axis is drawn in ([Time axis and zones](#time-axis-and-zones)) |
| **Find available technician** | The Phase 2 search inside the scheduler ([below](#find-available-technician-inside-the-scheduler)) |
| **Unscheduled work (N)** (`aria-pressed`) | Shows or hides the queue beside the board; the list is fetched only while it is open |
| **Refresh** | Re-reads the board (the icon spins while a read is in flight) |

Under the toolbar, four figures for what is shown: **Capacity** (`usableMinutes`), **Planned**
(`confirmedMinutes`), **Free** (`remainingConfirmedMinutes`) and **Pieces of work**
(`allocationCount`). The first three are sums over the people **offered for planned work**, across
every day shown, as Team capacity counts them; someone switched out of planned work is listed, so
nobody wonders where they went, but adds nothing. Pieces of work counts every block shown. The
figures come from the server's answer, so the browser-side filters below (search, source, client,
status) hide rows and blocks without changing them.

### Rows

One row per person the viewer's scope reaches, narrowed by the team, department and skill
filters; active accounts only; ordered by name. Down the side: initials in a circle, the name (a
link to the person's Plan tab on that date), their teams, the person's own zone when it is not the
organization's ("NOC · America/New_York"), and the day's **capacity bar**:

- a thin bar of planned (brand colour), free (green) and, if any, over (red), labelled "{planned}
  planned of {usable} usable" (and ", {n} over");
- the text "**2h planned / 8h · 6h free**", or "2h planned / 1h **· 1h over**" in red when more is
  planned than the day holds; the track then carries an **Over capacity** chip at its right.

A row without a usable day says why instead: "**No schedule**" (no working schedule), "**Away all
day**" (a full-day exception) or "**Not working**" (a day the schedule does not work). A person with
who is not offered for planned work is labelled "Not offered for work" under their name, after their teams.

### The track

Across the row, on the shared axis:

| Drawn as | What it is | Label |
|---|---|---|
| A lighter band | The person's working window for the day (Phase 1), as instants | aria-hidden |
| Dashed green **buttons** | The free windows (`freeSlots`, Phase 2): click one to schedule work there. Wide ones read "Available · 2h 30m", narrow ones "2h 30m", the narrowest nothing | "Available 11:00–12:30, 1h 30m. Schedule work for Jason Carter" |
| Hatched grey | Breaks | "Break 12:30–13:30" |
| Hatched amber | Time away that has a start and an end (part-day unavailable exceptions); the tooltip adds the note when the viewer may see it | "Unavailable 15:00–16:00" |
| Solid blocks | Planned work ([Blocks](#blocks)) | "{reference}, {title}, {time}, {duration}, fixed, ticket finished" |
| A dashed brand-coloured outline | The drop preview while something is dragged over the row: where it would land and its length, "10:15–11:15" | aria-hidden |
| A red vertical line | **Now**, on today only (today in the organization's zone), through the header and every row, moved once a minute | "Now" (header) |

Full-day time away is not a stretch on the track: the row says "Away all day" and the day has no
window. A night shift's window runs past midnight on the same axis
([Time axis and zones](#time-axis-and-zones)).

### Blocks

A block is a **button**: clicking it opens the detail drawer, which holds every action the board
offers. It is also draggable when the viewer may move it or give it away (`canEdit || canReassign`),
and carries a resize handle on its right edge when they may edit it (`canEdit`).

| Shown | When | Meaning |
|---|---|---|
| Reference ("INT-000123", "Autotask 43829") | Block wider than 48 px; narrower blocks show the source code instead | Where the work is: the board number, or the provider and its id |
| Source chip **AT** / **CW** / **Board** | Wider than 110 px (always in the drawer and the queue) | Autotask, ConnectWise, or the team's own board, read off the reference |
| Grip | Wider than 60 px and movable | It can be dragged |
| Title, or "Work you cannot open" | Wider than 90 px, lane at least 24 px tall | The ticket's title only when the viewer may see the ticket (`ticketVisible`) |
| Client · duration ("ABC Company · 1h 30m") | Wider than 150 px, lane taller than 38 px | |
| Lock, amber tone | `isFixed` | Fixed in place by a scheduler; the person cannot move it |
| Muted, struck through | `ticketFinished` | The ticket is resolved or closed; the release pass takes a future one out within 5 minutes ([planned-work.md](planned-work.md#what-finishes-leaves-the-plan)) |
| Brand tone | Otherwise | Flexible planned work |

Blocks that overlap in one row (work placed over other work with an override) take separate
**lanes**: each block takes the first lane that is free when it starts, so nothing is drawn on top
of anything else; blocks that only touch share a lane. The row keeps its height (68 px) and the
lanes share it.

The accessible name says everything the visual does: "INT-000001, Rebuild backup job, 09:00–11:00,
2h, fixed" (and ", ticket finished"), followed, for screen readers, by who planned it.

### The detail drawer

Clicking a block (or a card entry on a phone) opens "**Planned work {reference}**" from the right:
the reference, the source name, **Fixed** and **Ticket finished** chips, the title (or "Work you
cannot open"), and a fact list: Person, Client, Date (the weekday and date in the person's zone),
Time ("09:00–11:00 (Asia/Kolkata)", the person's own zone named), Planned (the duration), Ticket
status, Scheduled ("Scheduled by you" / "Scheduled by Lena Lead" / "Planned by you" / "Planned by Jason Carter" /
"Placed by the system", then "· fixed in place" or "· flexible"), Note, and Override (the reason,
with who overrode in brackets). Then the actions the viewer may take, each only when the API says
so:

| Action | When | Does |
|---|---|---|
| **Open ticket** | `ticketVisible` | Link to `/dashboard/tickets/{id}` |
| **Reschedule** | `canEdit` | The Phase 3 **Move planned work** dialog |
| **Give to someone else** | `canReassign` | The Phase 3 **Give work to someone else** dialog |
| **Remove from plan** | `canCancel` | After "Take {reference} out of the plan? The ticket itself is not changed.", `DELETE plan/{id}` |
| "You cannot change this work. Ask whoever planned it." | None of the three | |

Escape, the close button or a click outside closes it. The drawer is the **accessible alternative**
to every drag: nothing on the board can be done only by dragging.

### The week grid

**Week** replaces the board with a table ("Planned and usable time per person per day"): a column
per day (the header dates are buttons), a row per person (name linking to their Plan tab, teams),
and in each cell the day's capacity bar with its text, or "Away" / "Off", then up to three pieces
of work as "🔒 INT-000123 · 1h" (the lock for fixed work; hover for who planned it) and "+N more".
Every cell is a button, "Jason Carter, Mon 5 Oct: 2h planned of 8h, 1 pieces of work. Open the
day": clicking a cell or a header switches to the day board on that date. The week is for seeing
workload; the day is for changing it. Nothing is dragged in the grid.

### Phones

Below 768 px (`matchMedia('(max-width: 767px)')`) there is no timeline. Each person is a card: name
(link to their Plan tab), teams, then for the day (or each day of the week, each headed by a date
button that opens that day) the capacity bar or "Away all day" / "Not working", and the day's work
as "09:00–10:00 · INT-000123 · {title}" with a lock for fixed work, or "Nothing planned". Tapping a
piece of work opens the drawer, with the same actions. Nothing is dragged or resized on a phone, the
time-scale control is not offered, the queue's rows are not drag sources and say nothing about
dragging; **Plan** stays. The e2e "the schedule becomes cards with each person's day, and nothing
scrolls sideways" runs at 375 px.

## Views: Day and Week

- **Day** is the scheduling view: the axis, the free windows, drag, drop and resize.
- **Week** is the workload view: planned against usable per person per day, with the first three
  references; a click opens the day.
- **Month is not built**, deliberately. The view type is `'day' | 'week'`. A month grid can hold
  at best a count per person per day, with no time axis, so nothing can be placed, moved or
  resized on it; it would be a poorer Team capacity. It is deferred, not forgotten: a later
  reporting phase may add a month heatmap of utilization ([README.md](README.md), Phase 9).

The view, like the time scale, the team and department filters and whether the queue is open, is
remembered in the browser ([Filters and search](#filters-search-and-what-is-remembered)).

## Time axis and zones

The arithmetic is one pure module, `apps/web/src/lib/timeline.ts`, over epoch milliseconds and
ISO instants, so overnight windows, clock changes and half-hour zones are handled by the platform's
zone tables (`Intl.DateTimeFormat`), never by assuming a day is 24 hours long.

**The axis** (`buildAxis`) is built for the date in the **organization's zone** (`TeamPlanDto.
timeZone`, said in the toolbar as "Times in {zone}"):

1. From the earliest working-window start to the latest working-window end across the rows shown.
2. Widened to whatever planned work or part-day time away reaches beyond that, so nothing is cut
   off: work at 18:30–19:15 extends an axis that would have ended at 18:00 to 20:00.
3. With nothing at all (nobody has a window, nothing is planned), 08:00 to 18:00 on that date.
4. On whole hours of the zone's clock: the start floored to the previous hour, the end raised to the
   next, at least one hour long (`floorHour` works from the wall-clock minute, so a half-hour zone
   like Asia/Kolkata floors to 09:00, not 09:30).
5. One mark per hour, labelled in the zone. On a fall-back day an hour label appears twice; on a
   spring-forward day one is missing.

A night shift (18:00–03:00) runs past midnight **on the same axis**; its hours read 18:00 … 23:00,
00:00, 01:00, 02:00 and the right edge falls on the next date.

**Zones on the board:**

| Element | Zone |
|---|---|
| The axis, its hour labels, the free windows ("Available 11:00–12:30"), breaks, time away, the drop preview, the "now" line | The organization's zone |
| A row's "· {zone}" under the name | The person's own zone, shown only when it differs from the organization's (the zone of their schedule in force on the first date shown) |
| A block's accessible name and the drawer's Date and Time ("09:00–11:00 (America/New_York)") | The person's own zone, named in the drawer |
| Which date a piece of work belongs to (the day board's blocks, the week grid's cells, a phone's cards) | The person's own zone: the server returns, for each date, the work that **starts** on that date in the person's zone (`AllocationsOnAsync`); the grid and cards group by `dateOf(startsAt, person.timeZone)` |
| The capacity per day | Phase 2 shift dates, in the person's zone ([capacity.md](capacity.md#time-zones)) |

A person who works in another zone therefore has their block drawn where it falls on the
organization's clock and labelled with their own wall-clock time; the drawer names the zone so the
two are never confused.

**Where a pointer lands** (`atPointer`): the fraction of the track's width, clipped to the axis,
snapped to the nearest **15 minutes** (`SNAP_MINUTES`). Snapping is on the UTC clock, which is also
the zone's clock for whole- and half-hour zones because 15 divides 30. A dragged block keeps the
point it was grabbed at under the pointer (its grab offset is kept), so it lands where it looks.

**Clock changes** (`wallToMs`: a wall-clock time in a zone, read back through the zone tables and
corrected once):

| Case | Behaviour | Unit test |
|---|---|---|
| Half-hour zone | 09:30 Asia/Kolkata on 5 Oct 2026 is 04:00Z; 14:00 Europe/London is 13:00Z; 09:00 America/New_York is 13:00Z; 00:15 Kolkata is still 4 Oct in UTC | "wall times in a zone become the right instants, half-hour zones included" |
| A normal day | Windows 08:30–17:30 and 09:00–18:00 give an axis 08:00–18:00 with ten hour marks; 10:00–11:00 sits at 20 % with width 10 %; work to 19:15 widens the end to 20:00; nothing at all gives 08:00–18:00 | "a normal day: the axis spans the working window on whole hours and blocks sit where they should" |
| Night shift | A day row and an 18:00–03:00 row give one 19-hour axis whose last label is 02:00 and whose end is the next date; 01:00–02:30 sits at 17/19 of the width | "a night shift runs past midnight on one axis" |
| **Fall back** (America/New_York, 1 Nov 2026: 01:00–02:00 EDT, then 01:00–02:00 EST) | A 04:00Z–10:00Z window labels 00:00, 01:00, **01:00**, 02:00, 03:00, 04:00: the repeated hour is on the axis twice and the axis is six real hours long; 05:30Z–06:30Z is one sixth of it and 60 minutes; the ambiguous wall time 01:30 resolves to its **first** occurrence (05:30Z) | "fall back: the repeated hour is on the axis twice, and nothing has a negative length" |
| **Spring forward** (Europe/London, 29 Mar 2026: 01:00 GMT becomes 02:00 BST) | A 00:00Z–04:00Z window labels 00:00, 02:00, 03:00, 04:00: the skipped hour is not on the axis; a block across the gap (00:30Z–01:30Z) keeps its real length (25 % of four hours); a wall time that never happens (01:30) lands on the instant after the gap (01:30Z, which reads 02:30) | "spring forward: the skipped hour is not on the axis and a block across the gap keeps its real length" |
| Pointer snapping | On an 08:00–18:00 axis 1,000 px wide, x = 250 and 262 give 10:30, 275 gives 10:45, −40 gives 08:00, 5,000 gives 18:00; 10:07 snaps to 10:00 and 10:08 to 10:15 | "pointer positions snap to the step and invert the placement" |
| Lanes | 09–11, 10–12, 11–13 and 14–15 take lanes 0, 1, 0, 0 (two lanes); touching blocks share | "overlapping blocks take separate lanes; blocks that only touch share one" |
| Durations | 0h, 45m, 1h, 1h 30m, 2h 30m | "durations read naturally" |

These eight tests (`apps/web/src/lib/timeline.unit.ts`) run in **Node with full zone tables, no
browser and no server**, through Playwright's test runner: `npm run test:unit`
(`playwright test -c playwright.unit.config.ts`, which picks up `src/**/*.unit.ts`). CI runs them in
the web job between the type check and the build ([testing.md](testing.md)). The server has the same
rules for the same cases in `WorkingWindowTests` and `CapacityEngineTests` ([capacity.md](capacity.md#clock-changes-dst)).

The Phase 3 dialogs use the same module for the wall-clock times people type (`wallToIso`): the
browser has no "wall time in zone X" primitive of its own.

## Interactions

Every interaction ends in a Phase 3 call. The server re-reads the plan under the person's gate,
evaluates conflicts against what is really there, checks the version the screen holds and audits
the change ([planned-work.md](planned-work.md#placing-work-the-order-of-checks)).

### Scheduling from a free window

Click an **Available** window in a row → **Choose work to plan** ("Plan work for Jason Carter": the
Phase 3 ticket picker, a search over open tickets the viewer can see) → **Plan work**, locked to
that person ("Plan INT-000124 · for Jason Carter"), with the date and the start pre-filled from the
window's start in the person's zone and a duration of 60 minutes; the free windows that hold the
duration are offered as chips, and **Add to plan** places it (`POST plan`). The e2e "work is
scheduled from a free slot and from the unscheduled queue without dragging" confirms the dialog
opens with the date and 13:30, and that the row and the queue follow.

### From the unscheduled queue

- **Plan** on a queue row opens **Plan work** with the person preselected: the ticket's holder when
  they are on the board and the viewer may plan for them, otherwise the first person on the board
  the viewer may plan for. The person can be changed ("Whose time"), the date and start are chosen in
  the dialog.
- **Dragging** a queue row onto a track (desktop only, and only for a viewer who holds
  `schedule.manage` and has someone to plan for) shows a 60-minute drop preview; the drop opens
  **Plan work** locked to that row's person, with the date and start where it was dropped and 60
  minutes. **Nothing is saved by the drop itself**: a queue item has no length of its own, so the
  dialog asks, and the server checks, before anything is written. The dragged item travels on the
  event as `application/x-pio-unscheduled`.

### Moving a block, and giving it away

Drag a block (it must be `canEdit` or `canReassign`; otherwise it is not draggable) and drop it:

| Dropped | Call | Checked first in the browser |
|---|---|---|
| On its own row at another time | `PUT plan/{id}` with the new start and end (the length is kept), the block's `version`, no note change | `canEdit`; otherwise "This work is fixed in place. Ask whoever planned it to move it." (fixed) or "You cannot move this work." |
| On another row (any time) | `POST plan/{id}/reassign` with the new person, start, end and `version` | `canReassign`; otherwise "Only someone who schedules others can give work to someone else." |
| On its own row at the same time | Nothing | |

While dragging, the target row shows the **drop preview** (a dashed outline with the would-be
times). On drop the block is drawn at its new place or in its new row **at once** (optimistic, the
pending change laid over the server's rows); if the server refuses, it goes **back where it was**
and the viewer is told why ([Conflicts and overrides on the board](#conflicts-and-overrides-on-the-board)).
On success the board and every capacity view reload. The browser does not pre-check capacity or
`canPlan` for the target row: the server's answer is the answer, and a row the viewer may not plan
for is refused there ("You can't plan work for that person.").

A drop on another row goes through every Phase 3 reassignment rule: the new person's capacity is
checked, the work becomes `AuthorizedUser` scheduled by the viewer, the ticket goes with it when the
first person held it, and both people are notified
([planned-work.md](planned-work.md#allocation-is-not-assignment)).

### Resizing

The right edge of a block the viewer may edit is a handle (a `separator`, "Resize INT-000123").
Dragging it (pointer events) previews the new end, snapped to 15 minutes and never shorter than 15
minutes; releasing sends `PUT plan/{id}` with the **same start** and the new end. Releasing at the
same end sends nothing. The server audits a change that keeps the start and changes only the end as
`workforce.allocation.resized`, with the planned duration following
(`A_resize_keeps_the_start_changes_the_planned_duration_and_is_audited_as_a_resize`: 14:00–15:00
to 14:00–16:00 is 120 planned minutes and one `resized` entry, no `moved` entry; growing to 17:00
still fits the window; growing to 18:00 is refused "This time is no longer available: …" for a
technician, who cannot override). Only the end can be resized; a change of start is a move.

### The drawer's actions

**Reschedule** opens the Phase 3 **Move planned work** dialog (date, duration, start, the free
windows that hold it with the work's own place counted as free, Fixed for a scheduler, note),
**Give to someone else** the **Give work to someone else** dialog, **Remove from plan** the confirm
and the `DELETE`. These are the same dialogs as My plan and a person's Plan tab
([planned-work.md](planned-work.md#the-dialogs)).

### Keyboard

Blocks and free windows are buttons: Tab reaches them, Enter or Space opens the drawer or the
picker. The drawer, the Find panel, the conflict dialog and the Phase 3 dialogs close on **Escape**
(and on a click outside). The view switch and the queue toggle are ordinary buttons. Drag and
resize are pointer-only; the drawer and the dialogs do everything they do.

## Conflicts and overrides on the board

A drop or a resize is checked by the server like any `PUT` or reassignment, and refused with the
Phase 3 **409** when the time has a conflict ([planned-work.md](planned-work.md#conflicts-and-overrides)).
What the viewer sees depends on the answer:

| Answer | On the board |
|---|---|
| 409 with `payload.canOverride` and `payload.overrideAllowedForCaller` true | The block goes back. A dialog, "**Conflict detected**" (role dialog "Conflict"), shows the server's sentence ("This time has a conflict: *Already has confirmed work during this period.* Give a reason to override it."), then every conflict as "*Already planned*: Already has confirmed work during this period." (the names: Already planned, Tentative work, Break, Unavailable, Outside working hours, Over capacity, Skill, Not offered for work), a box "**Reason to override (kept with the work)**" (placeholder "Approved after-hours client maintenance", up to 300 characters) and **Cancel** / **Override and save**, enabled once the reason has **5** characters. Saving re-sends the same move or reassignment with `overrideReason`; the reason, the conflict types, who and when are kept with the work and the drawer shows "Override: *{reason}* ({who})" |
| 409 with `canOverride` true but `overrideAllowedForCaller` false | The block goes back; the sentence is shown as a notice: "This time is no longer available: *{message}*" |
| 409 with `canOverride` false (a block: time away, not offered for work) | The block goes back; "This time cannot be used: *{message}*" |
| 409 with `payload.stale` | The block goes back; "**This plan changed since the screen loaded. Reloading it.**", and the board reloads with the current plan |
| 400 or 403 | The block goes back; the server's sentence ("The work must end after it starts.", "You can't plan work for that person.", …) |

The notice is a `role="alert"` strip with **Reload** and a dismiss button. Nothing is written on a
refusal; the audit entry for an accepted drop is `workforce.allocation.moved` (same row, time
changed), `workforce.allocation.resized` (same start, new end) or
`workforce.allocation.reassigned` (another row), each carrying `overrideReason` and `overridden`
when a reason was given. Blocks are never overridable, by anyone; a reason given when nothing needs
overriding is ignored. The e2e "dragging a block to another row gives the work away; a clash is
refused and overridden with a reason" drops Jason's 09:00 block onto Abbie's 09:00, sees "Conflict
detected" and "Already planned" with the block back in Jason's row, overrides with "Both on the same
server", and reads the work back from the API as hers with that reason; a clean move to 14:00 saves
without a dialog.

## The unscheduled queue

`GET api/workforce/plan/unscheduled/team`: open work **in the group's hands that is in nobody's
plan**. The group is the scheduler's rows: everyone the caller's `schedule.view` reaches, narrowed by
the same team, department and skill filters, active accounts only.

| Term | Meaning |
|---|---|
| In the group's hands | Held by one of those people (`AssignedAppUserId`), **or** routed to one of their teams (`AssignedTeamId`): every team one of them is in, or just the team asked for when the team filter is set |
| Open | Not finished (`TicketStatusRules.Open()`) |
| Visible | Within the **caller's** ticket scope, exactly as the ticket list is |
| In nobody's plan | No `Planned` allocation on the ticket that ends in the future, **whoever** it is planned for. Work planned only in the past is back on the list, with `plannedMinutesSoFar` saying how much was planned before |

Ordered by SLA due time (those with one first), then newest; at most **100**. Each row:
`ticketId`, `reference`, `title`, `clientName`, `priority`, `status`, `source` ("Team board",
"Monitoring", "Autotask", "ConnectWise"), `dueAt`, `holderId` / `holderName`, `teamId` / `teamName`,
`plannedMinutesSoFar`. With nobody in the group the list is empty. Proven by
`Team_unscheduled_work_is_what_the_group_holds_or_is_routed_and_nobody_has_planned`: the lead
sees Jason's board ticket and his Autotask ticket but never Sam's (outside the NOC); a ticket routed
to the NOC joins the list with `teamName` "NOC" and no holder; planned for anyone it leaves, and
taken out again it is back; a technician's list is the work in their own hands; a team they are not
in adds nothing.

Unlike My plan's `plan/unscheduled`, which answers an empty list to anyone without
`schedule.manage`, the team list needs only `schedule.view`: it is what the rows show, and planning
from it is gated separately ([Permissions](#permissions)).

**The panel** ("Unscheduled work (N)", a complementary landmark beside the board; on a phone, under
it): "Open work these people hold, or that sits with their teams, in nobody's plan. Drag one onto a
row, or press Plan." A search box (reference, title, client, holder) and **Show**:

| Show | Keeps |
|---|---|
| Everything | |
| Held by someone | Rows with a holder |
| With a team, nobody holds it | Rows without a holder (routed to a team) |
| Due today | Rows due on the organization's today, read in the organization's zone (the zone the board is drawn in) |
| Overdue | Rows whose due time has passed (these also carry an **Overdue** chip) |

Each row: the reference, the source chip, the title (a link to the ticket), the client, "Held by
Jason Carter" / "With NOC" / "Held outside this group, with NOC" (routed to one of the group's teams but held by someone the viewer may not see: no id and no name are returned) / "Nobody holds it", "1h planned before" when something was planned
earlier, and **Plan** (for a viewer with `schedule.manage` who has someone on the board to plan
for). Rows are drag sources on desktop under the same condition. The list depends on the filters,
not on the date, and is fetched only while the panel is open. Empty: "**Nothing is waiting:
everything open is planned.**"; filtered to nothing: "**Nothing matches.**".

## Find available technician inside the scheduler

The toolbar's **Find available technician** opens the Phase 2 search ([capacity.md](capacity.md#find-available-technician))
as a panel over the board, for **the board's date** and **the toolbar's team, department and skill
filters**: "Who has one continuous free slot on Mon 5 Oct, within the team, department and skills
filtered above. Facts, not a ranking; nothing is booked." Minutes (5 to 720 in steps of 5; 60 by
default), Earliest, Latest, **Find** (`GET availability`, a read). While it runs: "Searching…".

Each match: the name, the search's free minutes in the window ("4h free in the window"), "First fit" and the
recommended slot (the earliest stretch of exactly that length, with the day named when it falls on
another date), the matching skills with a tick, and two buttons:

- **View on timeline** (only when the person is one of the rows): closes the panel, switches to
  Day, scrolls the board to their row and highlights it for four seconds.
- **Schedule work**: closes the panel and opens **Choose work to plan** for that person, then
  **Plan work** pre-filled with the recommended start (the e2e "find available technician runs
  inside the scheduler and offers to schedule from a result": Jason's morning is taken, 240 minutes
  finds 13:30–17:30, Schedule work opens "Plan work for Jason Carter").

Nobody: "**Nobody has a free slot that long. Try a shorter duration, another date, or fewer
skills.**" Escape closes the panel. The search's own rules apply (14 days, 1,000 people, nothing in
the past); here it asks for one date.

## Filters, search and what is remembered

Two kinds of filter, deliberately:

| Filter | Where it is applied | Effect |
|---|---|---|
| **Team**, **Department**, **Skills** (holds all of them / any of them; the Phase 2 `GroupAndSkillFilters`, the groups being those with someone the viewer may see) | **Server** (`teamId`, `departmentId`, `skills`, `matchAll` on both reads) | Which people are rows, which work is in the queue, and the summary figures |
| **Search** ("Person, ticket or client") | Browser | Keeps rows whose name or team matches, or that have a block whose reference, title or client matches |
| **Work source** (Any source / Team board / Autotask / ConnectWise) | Browser | Hides blocks from other sources, read off the reference prefix |
| **Client** (the clients of the blocks loaded) | Browser | Hides blocks for other clients |
| **Work status** (Any status / Open work / Finished tickets) | Browser | Hides blocks on finished, or on open, tickets |

The browser-side filters never change the summary, and hide blocks without hiding their rows (only
the search hides rows). When the server returns nobody: "**Nobody matches these filters. Choose
another team or clear the skills.**"; when the search hides everyone: "**Nothing matches the
search.**"

**Remembered** in the browser (`localStorage` key `pio.schedule`, written on every change): the
view, the time scale, the team and department filters, and whether the queue is open. The URL's
`?date=` and `?view=` take precedence when the page opens. **Not** remembered: the date, the skill
filter, the search and the three block filters.

## Loading, empty and error states

| State | What is shown |
|---|---|
| The sign-in is being resolved | "Loading…" under the heading |
| Not a staff account | "This sign-in is not a staff account." |
| The first read of the board | Six skeleton rows ("Loading the schedule") |
| A later read (another day, a filter) | The previous rows stay until the new ones arrive (`placeholderData`); the Refresh icon spins |
| The read failed | A `role="alert"` with the server's message and **Try again** (no Try again on a 403: a refusal is not a retry) |
| Nobody in the answer | "Nobody matches these filters. Choose another team or clear the skills." |
| The search hides everyone | "Nothing matches the search." |
| A row with no usable day | "No schedule" / "Away all day" / "Not working" (week cells: "Away" / "Off"; cards: "Away all day" / "Not working", and "Nothing planned" on a working day with nothing) |
| A drop or resize refused | The block returns; a notice with the server's sentence, or the conflict dialog ([above](#conflicts-and-overrides-on-the-board)) |
| A refused drop the browser caught first | "Only someone who schedules others can give work to someone else." / "This work is fixed in place. Ask whoever planned it to move it." / "You cannot move this work." |
| A stale version | "This plan changed since the screen loaded. Reloading it." |
| The queue is loading / failed / empty / filtered empty | "Loading…" / the server's message / "Nothing is waiting: everything open is planned." / "Nothing matches." |
| Find is running / found nobody / failed | "Searching…" / "Nobody has a free slot that long. Try a shorter duration, another date, or fewer skills." / the server's message |
| A piece of work the viewer may not see | "Work you cannot open" for its title; no client, no status, no ticket link |
| Nothing the viewer may do with a block | "You cannot change this work. Ask whoever planned it." |

## API

Two reads on `WorkforcePlanController`, under the class-level `schedule.view`, the module switch
and the staff check like the rest of `api/workforce` ([planned-work.md](planned-work.md#api)). No
new write: the board uses Phase 3's.

| Route | Permission | Query | Returns |
|---|---|---|---|
| `GET plan/team?from&to&teamId&departmentId&skills=a,b&matchAll` | schedule.view | `from` defaults to today in the organization's zone, `to` to `from`; at most **14** days; within a year of today. `skills` comma-separated ids, `matchAll` true by default (every skill) | `TeamPlanDto` |
| `GET plan/unscheduled/team?teamId&departmentId&skills&matchAll` | schedule.view | The same request shape (`TeamPlanRequest`; dates are accepted and ignored) | `TeamUnscheduledWorkDto[]`, at most 100 ([The unscheduled queue](#the-unscheduled-queue)) |

`TeamPlanDto`: `from`, `to`, `today` (the organization's), `timeZone` (the organization's), `people`,
`usableMinutes`, `confirmedMinutes`, `remainingConfirmedMinutes` (sums over people offered for
planned work, across the days), `allocationCount` (every block), `canScheduleOthers` (the caller
schedules at least one of the people shown), `canOverride`. Each person (`TeamPlanPersonDto`):
`appUserId`, `displayName`, `timeZone` (their schedule's zone on the first date), `isSchedulable`,
`hasSchedule`, `teams`, `skills`, `days` (one Phase 2 `DayCapacityDto` per date, planned work
counted: window, breaks, free slots, exceptions, figures), `allocations` (`WorkAllocationDto`,
`Planned` only, starting on those dates in the person's zone, with `canEdit` / `canCancel` /
`canReassign` and the ticket's facts only when the caller may see the ticket), `canPlan` (the caller
may put work into this plan: a scheduler for them, or themselves with `schedule.manage`).

The rows and their days come from the capacity engine's `ICapacityService.ForTeamRangeAsync`
(`TeamRangeQuery` → `TeamRangeDto`, new in this phase: Team capacity over a run of dates, at most
`MaxTeamRangeDays` = 14); the pieces of work are one more query for everyone, shaped once
(`WorkPlanService.AllocationsOnAsync`); who the caller schedules is one query
(`WorkforceAccess.ScheduledByAsync`).

| Code | `title` | When |
|---|---|---|
| 200 | | The body is the result. A team or department the caller cannot see, or an id that names nothing, is an **empty** `people` list, not an error |
| 400 | `validation_failed` | "The last date is before the first." · "Ask for at most 14 days at a time." · "Choose dates within a year of today." · "That is more than 1000 people. Choose a team or a department to narrow it down." (`MaxPeople`) · "Ask for at most 20 skills at a time." · "One of the skills asked for is not in the skill catalogue." (an unknown id and another organization's, the same words) · "One of the values in a filter is not a valid id." (a malformed `teamId`, `departmentId` or `skills` value is refused, never ignored) |
| 403 | `forbidden` | No `schedule.view` (a client account, or a staff role without it); a sign-in that is not a staff account ("Only staff accounts can use the workforce module.") |
| 404 | `not_found` | Module off ("Workforce was not found.") |

Limits that bound any one request: 14 days, 1,000 people (narrow by team beyond that), 20 skills,
100 queue items. The web asks for one day or seven.

## Permissions

Nothing new. The three Phase 3 keys do the three jobs ([permissions-and-security.md](permissions-and-security.md)):

| Key | On the scheduler |
|---|---|
| `schedule.view` (scope) | Opens the page and both reads. The rows are the people the scope reaches: a technician at Own sees one row, a team lead at Team their team, a manager at All everyone. The **Team schedule** link is shown only to someone who sees more than themselves |
| `schedule.manage` (scope) | Acting. Per row, `canPlan`: a scheduler for that person (scope wider than Own, reaching them), or the person themselves with the key at any scope: free windows open the picker for anyone, but the server refuses a plan for someone the caller may not plan for. Per block, `canEdit` / `canCancel` / `canReassign` exactly as Phase 3 ([planned-work.md](planned-work.md#fixed-and-flexible)): a block is draggable when it may be edited or given away, resizable when it may be edited, and the drawer offers only what is allowed. The queue's **Plan** and drag need the viewer to hold the key and to have someone on the board they may plan for |
| `schedule.override` | The conflict dialog appears only when the 409 says `overrideAllowedForCaller`; without the key the refusal is a notice. `TeamPlanDto.canOverride` says the same |

A technician (Own) on their own row: their self-planned work moves, resizes and can be removed;
work scheduled for them moves and resizes if flexible and does nothing if fixed (the test above:
`!CanEdit && !CanCancel && !CanReassign` for fixed work scheduled for them). A scheduler's own row
is theirs to schedule as a scheduler.

## Tenant isolation and client security

The scheduler adds two reads and no new table or field. Everything it shows is already bound by the
Phase 3 rules: a person outside the caller's `schedule.view` scope is not a row, a ticket outside
their ticket scope is "Work you cannot open" with its time and nothing else, and another
organization's people never appear because `WorkforceAccess` applies the organization explicitly.

| # | Guarantee | Proven by |
|---|---|---|
| 1 | Both reads demand exactly `schedule.view`; no client role or client login holds it; the by-route walk covers the controller | `EndpointAuthorizationTests.Sensitive_endpoints_require_exactly_these_permissions` (the `Team` and `UnscheduledTeam` rows), `No_client_account_can_reach_any_workforce_endpoint`, `No_client_role_or_client_login_holds_a_workforce_permission` |
| 2 | No shape the ticket API or the client portal returns carries a scheduler field | `EndpointAuthorizationTests.Nothing_a_client_can_receive_carries_workforce_planning` (`Allocat`, `Planned`, `Schedul`, `Capacity`, …) |
| 3 | The rows are the people the asker's scope reaches: a lead sees their team and never someone in another team; a team the lead cannot see, or an id that names nothing, is an empty list, not a leak; a technician's scheduler is themselves alone | `WorkPlanTests.The_team_scheduler_lists_the_people_the_asker_may_see_with_their_days_and_what_is_planned` |
| 4 | The queue holds only work in the hands of people the asker may see, within the asker's ticket scope; a ticket held by someone outside their reach is not on it; a team the asker is not in adds nothing | `WorkPlanTests.Team_unscheduled_work_is_what_the_group_holds_or_is_routed_and_nobody_has_planned` |
| 5 | A sign-in that is not a staff account is refused by every action of the controller, the two new ones included, before any id is looked at; with the module off every action is "not found" | `WorkforcePlanController.Caller()` (code: the same guard as every other action; the Phase 3 test `A_sign_in_that_is_not_a_staff_account_is_refused_by_every_planning_action` lists the nine Phase 3 actions and should grow the two new ones) |
| 6 | In the browser: a technician has no **Team schedule** link, sees one row (their own) and no other person's name or work, and the API answers only their id; an account without the permission is refused the page and both endpoints answer 403, with a `teamId` as well | `apps/web/e2e/workforce-schedule.spec.ts`: "a technician has no team schedule entry and the page shows only themselves", "an account without the scheduling permission is refused the scheduler and its endpoints" |

Also, from Phase 3 and unchanged: a conflict never carries a ticket's title, client or number;
planning never changes PSA facts; request bodies are explicit records. The two reads are **GETs**
that change nothing, so they stay usable while an administrator views the portal as someone.

## Performance

Measured on 3 October 2026 with `CapacityPerformanceTests` (the theory on SQLite on a development
machine; the last row on PostgreSQL 17 in a throwaway container). Query counts are what is pinned;
timings are what one run printed and are not a promise.

| People | Allocations | Scheduler day | Scheduler week | Team queue |
|---|---|---|---|---|
| 50 | 1,000 | 42 queries, 89 ms (50 blocks) | 42 queries, 122 ms (250 blocks) | 8 queries, 9 ms |
| 100 | 4,000 | 42 queries, 112 ms (200 blocks) | 42 queries, 150 ms (1,000 blocks) | 8 queries, 9 ms |
| 500 | 30,000 | 42 queries, 773 ms (1,500 blocks) | 42 queries, 987 ms (7,500 blocks) | 8 queries, 331 ms |
| 500 (PostgreSQL) | 98,000 | 42 queries, 1,475 ms (3,500 blocks) | 42 queries, 2,813 ms (24,500 blocks) | 8 queries, 1,222 ms |

The query count never moves with the size: the rows' capacity costs what Team capacity costs, the
planned work is one query for everyone, shaped once, and the queue is eight. The week of 500 people
carries 24,500 blocks in one response, which is why the board windows its rows past 60 people and
groups each person's work by date once per render.


**Server.** `TeamAsync` costs what Team capacity costs over the days asked for (the calendar loads
schedules, exceptions, planned work and holidays for everyone shown in a fixed number of queries),
plus **one** query for everyone's planned work in the window (`AllocationsOnAsync`: the people,
`Planned`, `StartsAt` before two days after the last date and `EndsAt` after the day before the
first, served by the `(AppUserId, StartsAt)` index; the person's-zone date is then applied in
memory and the rows are shaped once by `DtosAsync`), plus one query for who the caller schedules.
No query per person, per day or per block. The queue is a handful of queries whatever the number of
people or tickets.

`CapacityPerformanceTests.Planned_work_costs_the_same_number_of_queries_whatever_the_number_of_allocations`
(50, 100 and 500 people with 1, 2 and 3 pieces of planned work per weekday over four weeks: 1,000,
4,000 and 30,000 allocations, the real stack) now also measures the scheduler for a day, the
scheduler for a week and the team queue, asserts the block counts (people × per person for the day,
× 5 for the week) and pins the query counts at **every** size:

| Request | Queries (asserted ceiling) |
|---|---|
| Scheduler, one day, everyone | 42 |
| Scheduler, one week, everyone | 42 |
| Team unscheduled work | 8 |

The ceiling is the same for a day and a week because the days change nothing in the number of
queries; 42 is the rows' capacity as Team capacity costs it plus the one query for everyone's work.
The test prints, per size, `"{people} people, {total} allocations | scheduler day: N queries, M ms
(B blocks) | scheduler week: … | team queue: … (K items)"`; the timings it prints are **SQLite,
development-machine** figures (the Phase 3 reads from the same run are tabled in
[planned-work.md](planned-work.md#performance)) and show the shape of the cost, not production
latency. Take them from a run with `--logger "console;verbosity=detailed"`.

`The_team_scheduler_at_a_hundred_thousand_allocations_on_a_real_database_reads_only_its_window`
runs **only on PostgreSQL** (`DESK_TEST_POSTGRES`, [testing.md](testing.md#running-the-postgresql-gated-tests)):
500 people with seven pieces of work every day for four weeks, about 98,000 rows (asserted above
95,000). A day of the scheduler (3,500 blocks) and a week (24,500 blocks) read only their own
window with the same ceiling of 42 queries, in under 10 and 20 seconds respectively on the test's
loose limits; the queue is measured too. It prints `"PostgreSQL | 500 people, {total} allocations |
scheduler day: … | week: … | queue: …"`.

**Browser.** One request draws a view: `plan/team` for the day or the week (the queue is a second
request only while its panel is open; Find a third, on demand). The day board renders only the rows
in and around the viewport: rows are 68 px tall, the scroller is 70 % of the window high, and the
rows drawn are those visible plus six above and six below (the first 30 before the first measure),
recomputed on scroll and resize, so five hundred people is a scroll, not a wait. The "now" line
re-renders once a minute. Each block, free window and break is positioned by percentage of the
axis, so the time scale changes without a reflow of the data. A move is drawn before the server
answers and undone if it refuses.

## Dependencies

**None added.** `apps/web/package.json` is unchanged: React, Next, TanStack Query, Zod, clsx and
lucide-react as before; no drag-and-drop library, no calendar or timeline component, no date
library. The board is native **HTML5 drag and drop** (`draggable`, `dataTransfer`, `dragover` /
`drop`), **pointer events** for the resize handle, **CSS absolute positioning** by percentage for
blocks and windows, `Intl.DateTimeFormat` for zones, `matchMedia` for phones and `ResizeObserver`
for the windowed rows. The Playwright runner already present runs the timeline unit tests.

## Skills

**Not implemented, deliberately, and documented as deferred.** A ticket carries no required-skill
field, so there is nothing to compare a person's skills with when work is placed: `WorkPlanService.
CheckAsync` builds its `ProposedWork` with no `SkillIds`, and the `SkillWarning` conflict (type 7,
[conflicts.md](conflicts.md)) can never arise from placing, moving, resizing or giving away work,
on this screen or any other. What skills do here: the **skill filter narrows the rows** to people
who hold them (all, or any), and the **Find** panel passes the same filter to the search, which
reports the matching skills per person. When tickets gain a required skill, the warning is already
built and the evaluator already reports it.

## Accessibility

- The board is a `grid` ("Team schedule for Monday 5 October") with `aria-rowcount`, a header
  `row` ("Person", and the hours "Hours from 08:00 to 18:00"), one `row` per person with
  `aria-rowindex`, a `rowheader` (the person) and a `gridcell` (the track, "Jason Carter: 2h
  planned of 8h", or "No schedule" / "Away all day" / "Not working").
- Every block is a **button** with a complete name ("INT-000001, Rebuild backup job, 09:00–11:00,
  2h, fixed, ticket finished") and a visually hidden "Scheduled by Lena Lead"; every free window is
  a button ("Available 11:00–12:30, 1h 30m. Schedule work for Jason Carter"); breaks and time away
  have labels; the resize handle is a `separator` ("Resize INT-000001"); the capacity bar is an
  `img` ("2h planned of 8h usable"); the "now" line is labelled "Now"; the drop preview and the
  working-window band are hidden from assistive technology.
- The drawer, the Find panel, the conflict dialog and the Phase 3 dialogs are `dialog`
  `aria-modal`, close on **Escape**, and the first field of the conflict dialog is focused.
- Day/Week is a `group` ("View") of `aria-pressed` buttons; the queue toggle is `aria-pressed`; the
  Previous/Next buttons say which day or week; notices are `role="alert"`.
- The week grid is a table with a caption, `scope`d headers and a button per cell ("…, 1 pieces of
  work. Open the day"); phones get a list ("People") of cards.
- Nothing is possible only by dragging: the drawer, the free-window buttons, **Plan** and the
  dialogs cover every action, and the drag and resize browser tests are the only ones limited to
  Chromium; the same actions are proven without dragging in every browser.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Blocks cannot be dragged (no grab cursor, no grip) | The viewer may neither move nor give away that work: `canEdit` and `canReassign` are both false. A technician cannot drag work scheduled for them that is fixed, or anyone else's work; a scheduler can only drag work of people their `schedule.manage` scope reaches | Open the block: the drawer says "You cannot change this work. Ask whoever planned it." Ask them, or widen the scope in Roles & Permissions |
| Blocks can be dragged but not resized (no handle) | The work may be given away but not edited (`canReassign` without `canEdit`), or the device is a phone | Use **Reschedule** in the drawer |
| "Only someone who schedules others can give work to someone else." | The block was dropped on another row by someone whose `schedule.manage` is Own | Move it within your own row, or ask a scheduler |
| "This work is fixed in place. Ask whoever planned it to move it." | A scheduler fixed it; the person it is planned for dropped it elsewhere | Ask them; only a scheduler moves fixed work |
| "You cannot move this work." | The viewer can see the row but may not change this piece (`canEdit` false and not fixed) | Ask whoever planned it |
| A block jumped back after a drop | The server refused: a conflict, a stale version, a rule. The notice or the conflict dialog says which | Read the sentence: pick another time, give a reason (if offered), or reload |
| "This plan changed since the screen loaded. Reloading it." | Someone (or you, in another tab) changed that piece since the board loaded; the version sent was stale | The board reloads itself; make the change again |
| "Conflict detected" after a drop | The time clashes, and you may override | Type a reason of at least 5 characters and **Override and save**, or **Cancel** |
| "This time is no longer available: …" | An overridable conflict and you hold no `schedule.override` | Choose a free window, or ask someone who can override |
| "This time cannot be used: …" | A block: time away, or not offered for planned work; nobody can override it | Change the time away or switch "offered for planned work" on |
| Nothing in Unscheduled work | Nothing open is held by, or routed to a team of, the people shown, or all of it is already in someone's future plan; or the filters narrow the group | "Nothing is waiting: everything open is planned." is the normal state. Clear the team/skill filters; check My work; look at the plan |
| A ticket I expect is not in the queue | It is finished, outside your ticket scope, held by someone outside your `schedule.view` reach, or someone has it planned in the future (whoever that is) | The person's Plan tab or the ticket's Planned work panel says where it is |
| A person is missing from the rows | Outside your `schedule.view` scope; or the account is inactive (never a row); or the team, department or skill filter excludes them; or the search hides their row | Widen the scope (Roles & Permissions), reactivate the account, clear the filters |
| A person is a row but adds nothing to Capacity | Not offered for planned work (the row says so when they have no team), or no schedule / not working / away that day | Expected: the sums are over people offered for work; the row says which |
| "Ask for at most 14 days at a time." | A request for more than two weeks of the scheduler (`MaxTeamRangeDays`) | The screen asks for one day or seven; a hand-made request must stay within 14 |
| "That is more than 1000 people. Choose a team or a department to narrow it down." | The scope reaches more people than one board will draw (`MaxPeople`) | Pick a team or a department |
| "Nobody matches these filters. Choose another team or clear the skills." | The server returned no people for the team, department or skills asked for; a team you cannot see gives the same | Clear the filters |
| "Choose dates within a year of today." | The date is more than a year away | Pick a nearer date |
| A block's time in the drawer differs from where it sits on the axis | The person works in another zone: the axis is in the organization's zone, the drawer in theirs (named) | Expected; the row shows "· {zone}" |
| No red "now" line | The board is not showing today (in the organization's zone), or the current time is outside the axis | Expected |
| Times look shifted on a Windows dev machine | Invariant globalization on the server ([troubleshooting.md](troubleshooting.md)) | Development only |
| **Team schedule** is not in the Workforce bar | The viewer sees only themselves (`schedule.view` Own) | Expected; the page still opens by address with one row |
| The page shows an alert and no rows | No `schedule.view`, or the module is off | Grant the key; set `FEATURES_WORKFORCE=true` |
| Work I planned a moment ago is not on the board | The board reloads after its own writes; a change made elsewhere (My plan, another tab) is not pushed | Press **Refresh** |
| A block reads "Work you cannot open" | The ticket is outside your ticket scope; its time is taken, nothing more is told | Expected; a ticket scope that reaches it shows the title |
