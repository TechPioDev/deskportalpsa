# Architecture (Phases 1 and 2)

The workforce module sits beside the unified ticket model and never copies it. Phase 1 adds who
works when and what they know. Phase 2 adds capacity exceptions and the engine that turns schedules,
exceptions and planned work into capacity and free time. Later phases reference tickets from work
allocations by id.

## Data model

```
AppUser (existing identity, +IsSchedulable)
  1-*  WorkSchedule        work_schedules        tenant | AppUserId | EffectiveFrom (date) | TimeZone (IANA)
         1-*  WorkScheduleDay   work_schedule_days    tenant | Day (0=Sun..6=Sat) | Start | End (time)
                1-*  WorkScheduleBreak  work_schedule_breaks  tenant | Start | End (time)
  1-*  StaffSkill          staff_skills          tenant | SkillId | Level (1 Basic, 2 Proficient, 3 Expert)
  1-*  CapacityException   capacity_exceptions   tenant | Kind (1 unavailable, 2 additional) | AllDay | FromDate | ToDate
                                                 | StartsAt | EndsAt (instants, part-day only) | TimeZone | Reason | Note
Skill                      skills                tenant | Name | NormalizedName | Description | IsActive
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
| Work allocation | **Not in Phase 2.** The engine reads planned work through `IWorkAllocationReader`; nothing can be booked yet, so the registered reader returns none. The allocation table arrives with booking. |
| Holidays | **Reused** (`DeskHoliday`, the SLA calendar): shown on the day, not deducted. |
| Schedule templates | **Deferred.** "Apply to others" copies one person's schedule to many from a date, which covers bulk setup without a template entity to maintain. |

Indexes and constraints:
- one version per person per start day (`AppUserId, EffectiveFrom` unique)
- one row per weekday per version
- one skill name per organization (case- and space-insensitive)
- one holding per person per skill

Deleting a person removes their schedules and skill holdings (cascade). A skill can't be deleted
while held (restrict); it is retired instead. The migration is additive, and its `Down` removes exactly
what `Up` added. Both were verified on PostgreSQL 17 against a database with existing staff.

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

`/api/me` carries `features.workforce`.

## Engine

```
domain      Intervals            set arithmetic over half-open stretches of real time (normalize, subtract, intersect)
            CapacityCalculator   one person, one shift date -> usable, confirmed, tentative, remaining, free slots; FirstFit
            ConflictEvaluator    a proposed stretch -> conflicts, each Block / Overridable / Warning
            (pure: no database, no clock - safe to call again inside the transaction that books work)
infra       WorkforceCalendar    reads schedules, exceptions, planned work and holidays for N people over a run
                                 of dates in a fixed number of queries, and builds the calculator's inputs
            CapacityService      person / team / search / conflicts, limited to who the caller may see
            CapacityExceptionService
application IWorkAllocationReader   where planned work comes from (none until booking exists)
```

## Web

- **Workforce** in the menu (staff with `schedule.view`, module on): overview with search, team,
  department and skill filters (any/all).
- **Workforce → person**: Work schedule and Skills tabs.
- **Workforce → Skills catalogue** (workforce managers).
- **Users → person** gets the same tabs when the module is on.
- Phase 2: **My capacity**, **Team capacity** and **Find available technician** under Workforce (the
  last two only for someone who can see more than themselves), and an **Availability** tab on a
  person (the week, the day's sums and free windows, time away). No final team scheduler yet, and
  nothing is assigned from the search.
