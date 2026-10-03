# Architecture (Phases 1 to 4)

The workforce module sits beside the unified ticket model and never copies it. Phase 1 adds who
works when and what they know. Phase 2 adds capacity exceptions and the engine that turns schedules,
exceptions and planned work into capacity and free time. Phase 3 adds the planned work itself: a
work allocation references the existing ticket by id, whatever its origin. Phase 4 adds **no table
and no write**: the team scheduler is a second screen over the same model, with two reads (the
people the caller may see with their days and their work; the group's unscheduled work) and a pure
timeline module in the web app ([team-scheduler.md](team-scheduler.md)).

## Data model

```
AppUser (existing identity, +IsSchedulable)
  1-*  WorkSchedule        work_schedules        tenant | AppUserId | EffectiveFrom (date) | TimeZone (IANA)
         1-*  WorkScheduleDay   work_schedule_days    tenant | Day (0=Sun..6=Sat) | Start | End (time)
                1-*  WorkScheduleBreak  work_schedule_breaks  tenant | Start | End (time)
  1-*  StaffSkill          staff_skills          tenant | SkillId | Level (1 Basic, 2 Proficient, 3 Expert)
  1-*  CapacityException   capacity_exceptions   tenant | Kind (1 unavailable, 2 additional) | AllDay | FromDate | ToDate
                                                 | StartsAt | EndsAt (instants, part-day only) | TimeZone | Reason | Note
  1-*  WorkAllocation      work_allocations      tenant | TicketId (-> tickets) | StartsAt | EndsAt (instants) | PlannedMinutes
                                                 | Status (1 planned, 5 cancelled) | Method (1 self, 2 authorized user, 3 automation)
                                                 | ScheduledByUserId | IsFixed | Note | OverrideReason | OverriddenConflicts | OverriddenByUserId | OverriddenAt
                                                 | CancelledAt | CancelledByUserId | CancelReason | UpdatedByUserId | Version
Skill                      skills                tenant | Name | NormalizedName | Description | IsActive
Ticket                     tickets               (existing, reused unchanged: the work an allocation points at)
Team / Department / UserTeam / UserDepartment    (existing, reused unchanged)
```

| Concept | Decision |
|---|---|
| Employee / technician identity | **Reused**: `AppUser`. No second identity model. |
| Active for scheduling | **Extended**: `AppUser.IsSchedulable` (default true; the migration keeps existing staff on). An inactive account is never offered, whatever the flag says. |
| Team, department, manager | **Reused** unchanged. |
| Working schedule, day, break | **New** (three tables). A weekday with no row is a day off. |
| Time zone | Stored per schedule version as an IANA id. |
| Skill, staff skill | **New**. Certification and expiry are deferred. |
| Capacity exception | **New** (one table, Phase 2). Capacity planning only: no leave balances, approvals or payroll. |
| Work allocation | **New** (one table, Phase 3). WHO is planned to do WHICH work WHEN: it points at the existing `Ticket` row (board, Autotask, ConnectWise, monitoring) and carries no title, client, status or provider of its own. Planned time, never actual time. The engine reads it through `IWorkAllocationReader`, whose registration is now `WorkAllocationReader`. See [planned-work.md](planned-work.md). |
| Planning vs assignment | An allocation never changes what the PSA says. The one bridge: someone scheduled on a ticket nobody in the portal holds becomes its portal holder (the fact "Take it" records), through the existing `TicketAssignment`. |
| Holidays | **Reused** (`DeskHoliday`, the SLA calendar): shown on the day, not deducted. |
| Schedule templates | **Deferred.** "Apply to others" copies one person's schedule to many from a date, which covers bulk setup without a template entity to maintain. |

Indexes and constraints:
- one version per person per start day (`AppUserId, EffectiveFrom` unique)
- one row per weekday per version
- one skill name per organization (case- and space-insensitive)
- one holding per person per skill
- work allocations: `(AppUserId, StartsAt)`, `(TicketId)`, `(MspOrganizationId, StartsAt)`; `Version`
  is an EF concurrency token

Deleting a person removes their schedules, skill holdings and planned work (cascade); deleting a
ticket removes its planned work. A skill can't be deleted while held (restrict); it is retired
instead. Each phase's migration is additive, and its `Down` removes exactly what `Up` added. Phases 1
and 2 were verified on PostgreSQL 17 against a database with existing staff; Phase 3's
(`20261003033116_WorkforceWorkAllocations`) adds the one table.

## API

All routes are under `api/workforce`. They are staff-only (every action needs `schedule.view`) and
answer 404 while the module is switched off.

| Route | Permission | Notes |
|---|---|---|
| `GET people?teamId&departmentId&skills=a,b&matchAll&includeInactive` | schedule.view | Only the people the caller's scope reaches |
| `GET people/{id}/schedule` | schedule.view | Current version, the next change if one is set, version dates, `canManage` |
| `PUT people/{id}/schedule` | + workforce.manage | Body `{effectiveFrom?, timeZone, days:[{day,start,end,breaks:[{start,end}]}]}`. Same start day = correction. |
| `DELETE people/{id}/schedule/{yyyy-mm-dd}` | + workforce.manage | Only a version that has not started |
| `POST people/{id}/schedule/copy` | + workforce.manage | `{toUserIds, effectiveFrom?}` (at most 200) |
| `PUT people/{id}/schedulable` | + workforce.manage | `{schedulable}` |
| `GET skills?includeInactive` | schedule.view | Catalogue with holder counts |
| `POST skills`, `PUT skills/{id}` | + workforce.manage | Create; rename, describe, retire or reactivate |
| `GET people/{id}/skills` | schedule.view | |
| `POST people/{id}/skills`, `DELETE people/{id}/skills/{skillId}` | + workforce.manage | Assign (or change level); remove |

Phase 2 (`WorkforceCapacityController`, same base route, same switch):

| Route | Permission | Notes |
|---|---|---|
| `GET people/{id}/capacity?from&to` | schedule.view | One person's capacity and free slots per shift date; their today when no dates are given; at most 31 days |
| `GET capacity?date&teamId&departmentId&skills=a,b&matchAll` | schedule.view | Everyone the caller's scope reaches, for one date, with totals |
| `GET groups` | schedule.view | Teams and departments that have someone the caller may see (the filter lists) |
| `GET availability?from&to&duration&earliest&latest&timeZone&teamId&departmentId&skills&matchAll&people` | schedule.view | Who has one continuous slot of `duration` minutes; at most 14 days |
| `GET people/{id}/conflicts?start&end&tentative&skills` | schedule.view | Whether proposed work fits, and what is in the way |
| `GET people/{id}/exceptions?from&to` | schedule.view | |
| `POST people/{id}/exceptions`, `PUT`/`DELETE people/{id}/exceptions/{exceptionId}` | + availability.manage | Scope decides whose |

The search and the conflict check are **GETs**: they change nothing, and they stay usable while an
administrator views the portal as someone (which refuses every other method).

Phase 3 (`WorkforcePlanController`, same base route, same switch; details and bodies in
[planned-work.md](planned-work.md#api)):

| Route | Permission | Notes |
|---|---|---|
| `GET people/{id}/plan?from&to` | schedule.view | Capacity per day with planned work counted, the work itself, and what the caller may do |
| `GET plan/unscheduled` | schedule.view | The caller's open work not yet in their plan |
| `GET plan/people` | schedule.view | Who the caller may plan work for |
| `GET tickets/{ticketId}/plan` | schedule.view | What is planned on a ticket, for the people the caller may see |
| `POST plan` | + schedule.manage | Place work; scope decides whose time |
| `POST plan/internal-work` | + schedule.manage | Raise a board ticket in the caller's name and plan it, in one step |
| `PUT plan/{id}` | + schedule.manage | Move, resize, note, fixed; carries `version` |
| `POST plan/{id}/reassign` | + schedule.manage | Give to someone else; carries `version` |
| `DELETE plan/{id}?reason=` | + schedule.manage | Take out of the plan; the ticket is untouched |

A refusal for a conflict or a stale version is **409** `conflict` with a `payload`
(`ConflictProblemDto`), the one problem response in the API that carries one.

Phase 4 (two more reads on `WorkforcePlanController`; details in [team-scheduler.md](team-scheduler.md#api)):

| Route | Permission | Notes |
|---|---|---|
| `GET plan/team?from&to&teamId&departmentId&skills=a,b&matchAll` | schedule.view | The team scheduler: everyone the caller's scope reaches (narrowed), each day's capacity with planned work counted, the work itself with what the caller may do to it, the sums; today when no dates are given; at most 14 days |
| `GET plan/unscheduled/team?teamId&departmentId&skills&matchAll` | schedule.view | Open work held by those people or routed to their teams that is in nobody's future plan; at most 100 |

Both are GETs. The board's drags, drops and resizes call the Phase 3 `PUT plan/{id}` and
`POST plan/{id}/reassign`; a `PUT` that keeps the start and changes only the end is audited as
`workforce.allocation.resized`.

`/api/me` carries `features.workforce`.

## Engine

```
domain      Intervals            set arithmetic over half-open stretches of real time (normalize, subtract, intersect)
            CapacityCalculator   one person, one shift date -> usable, confirmed, tentative, remaining, free slots; FirstFit
            ConflictEvaluator    a proposed stretch -> conflicts, each Block / Overridable / Warning
            (pure: no database, no clock - safe to call again inside the transaction that books work)
infra       WorkforceCalendar    reads schedules, exceptions, planned work and holidays for N people over a run
                                 of dates in a fixed number of queries, and builds the calculator's inputs
            CapacityService      person / team / search / conflicts, limited to who the caller may see; from Phase 4
                                 also team over a run of dates (ForTeamRangeAsync, TeamRangeQuery -> TeamRangeDto,
                                 MaxTeamRangeDays = 14): the scheduler's rows and their days
            CapacityExceptionService
            WorkforceAccess      who the caller may see (schedule.view scope) and plan for (schedule.manage scope),
                                 and whether they may override (schedule.override); ScheduledByAsync (Phase 4): which of
                                 a list of people the caller schedules as a scheduler, in one query
            WorkAllocationReader planned work as the engine reads it: Planned allocations, confirmed, a future one on a
                                 finished ticket left out at once; the work id only for tickets the caller may see
            WorkPlanService      plan / unscheduled / plannable people / on a ticket; place, internal work, move,
                                 give away, take out; holder bridging; conflicts under the gate; audit; notifications;
                                 from Phase 4 team (TeamAsync: the rows from ForTeamRangeAsync plus ONE query for
                                 everyone's planned work in the window, AllocationsOnAsync, shaped once) and
                                 team unscheduled (UnscheduledTeamAsync: held or routed, open, in nobody's future plan)
            PlanningGate         one person's plan changed by one request at a time: SELECT ... FOR UPDATE on the
                                 person's app_users row (PostgreSQL), a per-person semaphore elsewhere
            WorkAllocationReleaser / WorkAllocationReleaseRunner
                                 finished tickets leave future plans (every organization, per tenant scope)
application IWorkAllocationReader   where planned work comes from (the table, from Phase 3)
            IWorkPlanService, IWorkAllocationReleaser, IWorkAllocationReleaseRunner
worker      WorkAllocationReleaseBackgroundService   runs the release every 5 minutes
web         lib/timeline.ts      the scheduler's arithmetic (Phase 4): a wall time in a zone as an instant (clock
                                 changes included), the day's axis on whole hours widened to the work, where a block
                                 sits, where a pointer lands (snapped to 15 minutes), lanes for overlapping blocks;
                                 pure, unit-tested in Node (timeline.unit.ts, `npm run test:unit`)
```

## Web

- **Workforce** in the menu (staff with `schedule.view`, module on): overview with search, team,
  department and skill filters (any/all).
- **Workforce → person**: Work schedule and Skills tabs.
- **Workforce → Skills catalogue** (workforce managers).
- **Users → person** gets the same tabs when the module is on.
- Phase 2: **My capacity**, **Team capacity** and **Find available technician** under Workforce (the
  last two only for someone who can see more than themselves), and an **Availability** tab on a
  person (the week, the day's sums and free windows, time away). The team scheduler arrived in
  Phase 4; the search's **Schedule work** inside it is the first step towards Phase 5.
- Phase 3: **My plan** under Workforce (the day's agenda with capacity, planned and free figures,
  "Unscheduled work of mine", **Add to plan**, **Internal work**); a **Plan** tab on a person under
  Workforce (first tab; **Plan work** for a scheduler; move, give away, take out); a **Planned work**
  panel on a ticket for staff with `schedule.view`, with **Plan this work**. The Users → person page
  does not carry the Plan tab. Components: `WorkforcePlan.tsx` (`PlanAgenda`, `UnscheduledWorkList`,
  `TicketPlanPanel`, the dialogs).
- Phase 4: **Team schedule** under Workforce (`/dashboard/workforce/schedule?date=&view=`, shown in
  the sub-navigation only to someone who can see more than themselves, beside Team capacity and
  Find): the day board (people down the side, the organization's day across the top, free windows,
  breaks, time away and planned work as blocks; drag to move or give away, resize by the edge, a
  drop preview, an optimistic move undone on refusal, a conflict dialog for overrides), the week
  grid, person cards on phones, the **Unscheduled work** queue (a drag source and **Plan**), **Find
  available technician** as a panel, a detail drawer with every action. Components:
  `WorkforceSchedule.tsx` (`TeamScheduleWorkspace`, `DayBoard`, `PersonRow`, `Block`,
  `CapacityBar`, `WeekGrid`, `PersonCards`, `UnscheduledQueue`, `AllocationDrawer`,
  `DragConflictDialog`, `FindPanel`), reusing `WorkforcePlan.tsx`'s `PlanWorkDialog` (now taking
  `initialStart` and `initialMinutes`), `ReassignDialog`, `TicketPickerDialog` and `ConflictNotice`;
  the arithmetic in `lib/timeline.ts`. No dependency was added ([team-scheduler.md](team-scheduler.md#dependencies)).
