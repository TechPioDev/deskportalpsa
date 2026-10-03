# Testing (Phases 1 to 4)

| Layer | Where | Covers |
|---|---|---|
| Unit: arithmetic | `WorkingWindowTests` (19) | Day shift, different hours, overnight, several breaks, zero length, overlap, outside the window (incl. overnight), no time left, break limit, time-zone placement (+05:30 and -05:00), spring-forward and fall-back nights, the skipped and the repeated hour, IANA normalization |
| Unit: services | `WorkforceFoundationTests` (12) | Save and read back; overnight; versions and withdrawal; past start refused; every problem at once; technician own-only and read-only; team-lead scope; another organization's people and skills not found; copy to others; offered/not offered; inactive people; skill catalogue, assignment, levels, retire, any/all filters; plain-text names; audit with correlation id |
| Endpoint security | `EndpointAuthorizationTests` | Each workforce action's required permission; no client login can reach any of them |
| Permission matrix | `PermissionGoldenMatrixTests` | New keys recorded deliberately per role; technician scope is Own |
| SQL translation | `RelationalQueryTests.Workforce_schedules_and_skills_translate` | Scope subqueries, all-skills count, holder counts, time/date columns on a real SQL engine |
| PostgreSQL 17 | One-off check before merge (throwaway container) | Migration on a database with existing staff keeps them offered; services on Postgres; `Down` removes exactly Phase 1 |
| E2E (Chromium, Firefox, WebKit in CI) | `e2e/workforce.spec.ts` (4) | Admin sets and reloads a schedule; night shift + refused break + server-side refusal; skill catalogue, person, overview filter, retire; technician sees only their own schedule (View as) |

## Phase 2

| Layer | Where | Covers |
|---|---|---|
| Unit: capacity arithmetic | `CapacityEngineTests` (33) | Interval merge (overlapping, touching), subtract, intersect, exact boundaries; standard day; non-working day; several and overlapping breaks; part-day and full-day unavailable; overlapping and adjacent exceptions; exception over a break; additional availability (incl. day off, touching the window, overridden by unavailable); confirmed work and the gaps it leaves; confirmed vs tentative; tentative under confirmed; fully booked; duration search and insufficient continuous slot; search limited to a window; overnight; three time zones; spring-forward and fall-back nights (New York, London); a break in the skipped hour and in the repeated hour; work booked across the repeated hour. Every case also checks the formula identities and that nothing is negative. |
| Unit: conflicts | `ConflictEvaluatorTests` (20) | Each conflict type and its severity; hidden work id; day off; over capacity vs a clash at that hour; tentative proposals; every conflict at once; night shift across midnight; re-check after someone else books |
| Integration: services | `WorkforceCapacityTests` (55) | The ten flows: own capacity; part-day and full-day exceptions; 90-minute search; skill filter (ALL and ANY); team filter; conflict detection; overnight; another organization. Plus scope (own / team / all), who may record time away, validation, plain-text notes, duplicates, audit, version changes, holidays, extra availability next to a night shift, nothing in the past, no caching, the reason and note withheld from a colleague, a non-staff sign-in refused by every action |
| Endpoint security | `EndpointAuthorizationTests` | Required permission of each capacity action; no client account reaches any workforce controller (found by route); no client role holds a workforce permission; capacity reads are GETs; nothing a client receives carries a workforce field |
| SQL translation and query count | `CapacityPerformanceTests` (5) | Every capacity query on a real SQL engine; the number of queries is the same for 50, 100 and 500 people and for 1, 7 and 14 days; timings printed |
| PostgreSQL 17 | The same class with `DESK_TEST_POSTGRES` set (throwaway container, before merge) | All of the above on Postgres, on a database built by the real migrations; the capacity migration applied to a database that already has staff and schedules, applied again, and taken back (`Down` removes the one table and nothing else) |
| SQL regression | `RelationalQueryTests.A_schedule_can_be_corrected_on_the_day_it_starts` | The same-day correction that answered 500 on a real database |
| E2E | `e2e/workforce-capacity.spec.ts` (5) | Technician: My capacity, own-only. Administrator: time away through the form (part day, duplicate refused, full day, remove, reload). Team capacity by date, skill and team. Find 90 minutes with a required skill, and the conflict check agrees. An account without the permission: no menu, pages refuse, every endpoint 403 |

## Phase 3

| Layer | Where | Covers |
|---|---|---|
| Integration: services | `WorkPlanTests` (22 tests, 24 cases) | Through the real services with permissions and ticket visibility resolved from the database: a technician plans their own ticket (capacity follows at once, the unscheduled list empties, the ticket is unchanged, nobody is notified); an Autotask ticket planned as itself (no copy, no PSA facts changed, nothing queued); a lead schedules a ConnectWise ticket for a technician (holder bridging, `ticket.assigned.portal`, the technician sees it and is notified); work held by someone else is not quietly moved and a person who could not open it is not planned on it; self-planning reaches only visible work; fixed vs flexible (who may move, cancel, fix); giving work away with the new person's capacity checked and the ticket going with it; cancelling leaves the ticket as it was; refused unless overridden (over work, over a break, outside hours; the three wordings; the payload; the administrator's override kept with the work); time away blocks everyone and exact boundaries are fine; a night technician's work across midnight; the period rules; a stale version; plain-text notes and a finished ticket; internal work raised and planned in one step; finished work leaves future plans and stays in past ones; the unscheduled definition; plannable people per scope; what is planned on a ticket per scope; two planners taking the same hour (two contexts, the in-process gate); a non-staff sign-in and the module switch; another organization |
| SQL translation, query count and concurrency | `CapacityPerformanceTests` (+3) | `Planned_work_costs_the_same_number_of_queries_whatever_the_number_of_allocations`: 50/100/500 people with 1,000/4,000/30,000 allocations, the real stack; one person's week, a team day, a 14-day search, a ticket's plan and placing work cost the same number of queries at every size (asserted). `Every_capacity_query_runs_on_a_real_database` now also places fixed work, unfixes and moves it, gives it away, reads the plan, the ticket, the unscheduled list, raises internal work, cancels, lists plannable people and releases finished work. `Two_requests_for_the_same_hour_on_a_real_database_end_with_one_booking`: PostgreSQL only |
| Endpoint security | `EndpointAuthorizationTests` | The nine `WorkforcePlanController` actions in the golden matrix (`schedule.view` to read, `schedule.manage` to change); the by-route walk asserts the plan controller is found; `schedule.manage` and `schedule.override` in the list no client role may hold; the forbidden substrings for client-facing shapes include `Allocat`, `Planned`, `MyPlan`, `Override` |
| Permission matrix | `PermissionGoldenMatrixTests` | `schedule.manage` on Administrator (All), Manager (All) and Technician (Own); `schedule.override` on Administrator and Manager only |
| E2E (Chromium, Firefox, WebKit in CI) | `apps/web/e2e/workforce-plan.spec.ts` (6) | A person plans their own held work into a free window from My plan (figures and free windows follow; move offered; remove leaves the ticket open). A scheduler places fixed work for a technician from the Plan tab, is refused a clash ("has a conflict", "Already planned") and overrides it with a reason; the API holds both with method 2, one fixed, one with its reason and `overriddenConflicts: [1]`. Work given to someone else moves with the ticket; the ticket page's Planned work panel names the new person; she may move it but not give it on or take it out; the first person no longer sees it. Internal work raised and planned in one step is a real open ticket on the board. An account without the scheduling permission: no Workforce menu, My plan and the Plan tab refuse and show no ticket, no Planned work panel, every plan endpoint 403. On a phone (375 px): My plan fits and still plans work |

## Phase 4

| Layer | Where | Covers |
|---|---|---|
| Integration: services | `WorkPlanTests` (+3; the class now holds 27 tests, 29 cases) | `The_team_scheduler_lists_the_people_the_asker_may_see_with_their_days_and_what_is_planned`: a lead's board is their team (Jason, Abbie and the lead; never Sam in another team), each person's days with planned work counted, their blocks with `canEdit` / `canReassign`, `canPlan` for the people the lead schedules, the sums over people offered for work, `canScheduleOthers` / `canOverride`; a week has seven days and both pieces, each on its own day; a team the lead cannot see, or a guessed id, is an empty list; a technician's board is themselves alone, with fixed work scheduled for them they cannot touch; more than 14 days ("Ask for at most 14 days at a time.") and a date two years out are refused. `Team_unscheduled_work_is_what_the_group_holds_or_is_routed_and_nobody_has_planned`: held by the group or routed to one of its teams, within the lead's reach (Sam's ticket is not on it), open, in nobody's future plan; planned for anyone it leaves and taken out it is back; a technician's list is the work in their own hands and a team they are not in adds nothing. `A_resize_keeps_the_start_changes_the_planned_duration_and_is_audited_as_a_resize`: 14:00–15:00 to 14:00–16:00 is 120 planned minutes, capacity follows, one `workforce.allocation.resized` and no `moved`; growing to 17:00 still fits, growing to 18:00 is refused for a technician ("This time is no longer available") |
| Query count and the 100k case | `CapacityPerformanceTests` | `Planned_work_costs_the_same_number_of_queries_whatever_the_number_of_allocations` now also measures the scheduler for a day, the scheduler for a week and the team queue at 50 / 100 / 500 people (1,000 / 4,000 / 30,000 allocations): the block counts are asserted (people × per person for the day, × 5 for the week) and the query counts pinned at **42 / 42 / 8** at every size; timings printed. `The_team_scheduler_at_a_hundred_thousand_allocations_on_a_real_database_reads_only_its_window` (**PostgreSQL only**): 500 people with seven pieces of work every day for four weeks (about 98,000 allocations); a day (3,500 blocks) and a week (24,500 blocks) read only their own window within the same 42 queries, and the queue is measured |
| Endpoint security | `EndpointAuthorizationTests` | The golden rows for `Team` and `UnscheduledTeam` (`schedule.view`); the by-route walk, the client-role list and the forbidden client-shape substrings cover the two reads without change |
| Web unit (Node, no browser, no server) | `apps/web/src/lib/timeline.unit.ts` (8), run by `npm run test:unit` (`playwright test -c playwright.unit.config.ts`: Playwright's runner over `src/**/*.unit.ts`, no `page` fixture, full zone tables) | Wall times in UTC, Europe/London, Asia/Kolkata (a half-hour zone) and America/New_York; a normal day's axis on whole hours, placement by percentage, widening to work beyond the window, 08:00–18:00 when nothing is known; a night shift past midnight on one axis; **fall back** (America/New_York, 1 Nov 2026: the repeated hour on the axis twice, six real hours, nothing negative, the ambiguous 01:30 at its first occurrence); **spring forward** (Europe/London, 29 Mar 2026: the skipped hour absent, a block across the gap keeps its real length, a time that never happens lands after the gap); pointer positions snapped to 15 minutes and clipped to the axis; lanes for overlapping blocks (touching ones share); duration labels. CI's web job runs it after lint and typecheck, before the build |
| E2E (Chromium on every run; Firefox and WebKit on pull requests and main) | `apps/web/e2e/workforce-schedule.spec.ts` (9) | **A manager sees the team for a day**: the grid, Day pressed, "Times in UTC", a row reading "2h planned / 8h · 6h free", the block's name "…, 09:00–11:00, 2h, fixed", the free windows 08:30–09:00 and 11:00–12:30, "Break 12:30–13:30", a second row "0h planned / 8h · 8h free"; the drawer with the person, the time, "Scheduled by you · fixed in place", the note, Open ticket, Reschedule, Give to someone else, Remove from plan; Escape closes it. **Scheduled from a free slot and from the queue without dragging**: the queue says "Held by Abbie Noor"; a free window opens the picker then Plan work "for Abbie" with the date and 13:30 pre-filled; 90 minutes gives a 13:30–15:00 block, the row follows and the ticket leaves the queue; Plan from the queue preselects her, a free-window chip places 08:30–09:30; "Nothing is waiting". **Dragging a block to another row** (Chromium only): a clash shows "Conflict detected" / "Already planned" with the block back in its row; "Both on the same server" and Override and save give it to the other person at 09:00 with the reason kept (read back from the API); a clean drag within the row saves without a dialog. **Resizing by the edge** (Chromium only): the "Resize …" handle dragged to 11:00 gives "09:00–11:00, 2h", the row "2h planned / 8h", 120 planned minutes in the API. **The week view**: the table with eight column headers, a cell reading "1h planned / 8h" with the reference, a click opening that day with the block. **Find inside the scheduler**: a taken morning, 240 minutes finds 13:30–17:30, Schedule work opens "Plan work for Jason Carter". **A technician**: no Team schedule link, one row (their own), no other name or reference, the API answers only their id. **An account without the permission**: an alert, no names, 403 from `plan/team`, `plan/unscheduled/team` and `plan/team` with a `teamId`. **A phone** (375 px): cards with "1h planned / 8h" and "09:00–10:00", no sideways scroll, a tap opens the drawer. Drag and resize are skipped outside Chromium; the same actions are proven without dragging in every browser |

### Running the PostgreSQL-gated tests

`CapacityPerformanceTests` runs on SQLite by default. Set `DESK_TEST_POSTGRES` to a connection string
**without** a database name and the same class runs on PostgreSQL instead, each test in a database
of its own (`desk_p2_<guid>`) created by the real migrations and dropped afterwards. Three tests run
*only* then: `Two_requests_for_the_same_hour_on_a_real_database_end_with_one_booking` (the row-lock
gate, two connections),
`The_capacity_migration_adds_one_table_to_a_database_that_has_staff_and_its_down_removes_only_that`
and `The_team_scheduler_at_a_hundred_thousand_allocations_on_a_real_database_reads_only_its_window`
(Phase 4: about 98,000 allocations; a day and a week of the scheduler read only their window).

```powershell
docker run -d --name desk-test-pg -e POSTGRES_USER=desk -e POSTGRES_PASSWORD=desk -p 15439:5432 postgres:17
$env:DESK_TEST_POSTGRES = "Host=localhost;Port=15439;Username=desk;Password=desk"
dotnet test tests/unit/Desk.Tests.Unit.csproj --filter "FullyQualifiedName~CapacityPerformanceTests" --logger "console;verbosity=detailed"
docker rm -f desk-test-pg
```

The figures in [capacity.md](capacity.md#performance) (PostgreSQL) and
[planned-work.md](planned-work.md#performance) (SQLite) are the lines the test prints
(`--logger "console;verbosity=detailed"` shows them); the scheduler's lines
(`scheduler day: … | scheduler week: … | team queue: …`, and the `PostgreSQL | 500 people, …` line
of the 100k case) are described in [team-scheduler.md](team-scheduler.md#performance).

### Running the web unit tests

```powershell
cd apps/web
npm run test:unit
```

Playwright's test runner in Node: no browser is launched and no server is started, so the suite
runs in seconds and anywhere Node runs. It picks up `src/**/*.unit.ts` (`playwright.unit.config.ts`);
a test there never takes the `page` fixture. `npm run test:e2e` is the browser suite and is
unaffected.

**Known limitation:** local test mode has no client login, so "client is denied" is proven by the
endpoint tests rather than in the browser. The browser tests use the nearest thing: a signed-in
account that holds no workforce permission, which is exactly what a client is to this module.
