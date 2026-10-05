# Workforce scheduling

Planning work against real capacity: when each technician normally works, what they are skilled in,
what is planned into their time, what is free, and later how planned work compares with actual work.

**Scope boundary.** This is capacity planning, not HR. Working hours exist only to establish usable
work capacity. There is no attendance, clock-in/out, biometrics, payroll, salary, leave balances or
accrual, HR compliance, recruitment, appraisal, or room/vehicle/equipment scheduling.

| Phase | What | Status |
|---|---|---|
| 0 | Discovery, audit and architecture | Done |
| 1 | Working schedules (overnight, breaks, time zones, versions) + skills | Done |
| 2 | Capacity, availability and free-time engine: capacity exceptions, usable and remaining capacity, exact free slots, team capacity, the technician search, conflict evaluation | Done |
| 3 | Work allocation, My plan and authorized scheduling: planning your own work, scheduling others (fixed or flexible), moving, giving away and taking out, overrides with a reason, internal work raised and planned in one step, finished work released | Done |
| 4 | Team scheduler: everyone the viewer may see on one board for a day or a week, planned work as blocks on a shared time axis with free windows, breaks and time away, the group's unscheduled work beside it, drag to move or give away, resize by the edge, a conflict dialog for overrides, Find available technician inside it; no new write path and no new table | Done |
| 5 | Advanced work planning: tentative work that takes no confirmed capacity until it is confirmed (and confirmed again, with every check, when it is), what a piece of work needs (effort, window, splittable, skill) kept beside the ticket, the planning queue with why each item waits and what the group is short, effort-in-a-window planning as preview → human review → confirm with partial fit and unallocated effort said plainly and nothing written until the confirmation, stale-plan protection, due-date awareness that never moves a due date, the full conflict taxonomy and override policy; one new table | Done |
| 5b | Find available technician: assigning from the search (the search itself arrived in Phase 2; the plan dialog offers it, the scheduler's Find panel offers **Schedule work** from a result, and the queue's **Find technician** opens it with the remaining effort) | Done along the way |
| 6 | My Day and actual work time: a technician's day as planned against actual, a server-held clock on a piece of work (start, pause, resume, stop; one running clock per person; a reload, a second tab or another device all see the same clock), the clock becoming an ordinary ticket time entry (pushed to the PSA once, retried without duplicates), time typed in by hand, corrections with a reason, unplanned and after-hours work recorded as it was, Team today for managers; two new tables, no new permission; never attendance | Done |
| 7 | Workforce analytics and the management dashboard: capacity, planned and recorded work, scheduled and capacity utilization, planned against actual and estimate variance, reactive against planned, completed work counted once, by technician / team / client / source / priority / work type / day, capacity against demand, a capacity heatmap, every card opening its records, My analytics for the person themselves, a CSV export with its own permission; every metric specified before it was drawn; no table, no cache, no score | **This release** |
| 8 | Management insights, capacity forecasting, work quality signals and operational reporting (trends and forecasts over the Phase 7 facts; quality signals only where the source data is reliable; scheduled reports) | Planned |
| 9 | Advanced reporting (a daily rollup if volumes ask for it; emailed analytics) | Planned |
| 10 | Security, performance and final QA | Planned |

**Internal only.** Scheduling, capacity, availability and planned work are for staff. No client
account can reach any of it, and a ticket a client can see does not make its planning visible to
them. Client ticketing and workforce scheduling are separate security domains
([permissions-and-security.md](permissions-and-security.md)).

**Planning is not assignment.** Planned time is a capacity boundary, not time logging. An allocation
never changes what the PSA says about a ticket, and cancelling one never touches the ticket
([planned-work.md](planned-work.md)).

The module is behind a switch, `Features:Workforce`, which is **off by default**. When it is off, the
menu entry, the tabs and every `api/workforce` route are hidden (the routes answer 404). In production
set `FEATURES_WORKFORCE=true` in `.env.prod` and redeploy; `false` hides it again and keeps the data.

- [architecture.md](architecture.md): data model, API, where it sits in PIO
- [schedules.md](schedules.md): working windows, overnight, breaks, time zones and DST, versions
- [skills.md](skills.md): the skill catalogue and levels
- [capacity.md](capacity.md): terms, the capacity formula, confirmed and tentative work, interval normalization, the free-slot algorithm, the technician search, time zones, overnight, DST, performance
- [availability-exceptions.md](availability-exceptions.md): time unavailable and additional availability
- [conflicts.md](conflicts.md): conflict types, which block and which can be overridden, why availability is not a reservation
- [planned-work.md](planned-work.md): work allocations, My plan, self-planning and authorized scheduling, fixed and flexible, holder bridging, the 409 conflict answer and overrides, concurrency, release of finished work, screens, API, client isolation, performance
- [advanced-planning.md](advanced-planning.md): tentative and confirmed work, what the work needs (the planning requirement), the planning queue and its derived waiting reasons, effort in a window (continuous or split) as preview → review → confirm, partial fit and unallocated effort, stale plan protection, due dates, the full conflict taxonomy and override policy, over-capacity and shortage, screens, API, storage, isolation, concurrency, audit, performance, deferred items, troubleshooting
- [PHASE7_ANALYTICS_METRIC_SPEC.md](PHASE7_ANALYTICS_METRIC_SPEC.md): every dashboard figure defined once: name, formula, source, inclusion and exclusion rules, zone behaviour, permission boundary, edge cases, example; the source-of-truth audit; the fixture every implementation must reproduce
- [analytics.md](analytics.md): the workforce analytics dashboard, a person's detail and My analytics, the drill-downs, the export, the API, where each figure comes from, periods and zones, permissions and isolation, measured performance, reconciliation, audit, deferred items, troubleshooting
- [work-execution.md](work-execution.md): My day, the work session and its segments, one running clock, pause / resume / waiting, stop becoming a time entry, the one rule for actual time, manual time and corrections with a reason, planned against actual (variance, not a score), unplanned / after-hours / overnight work, actual against billable time, the PSA (push, duplicates, failures), refresh and concurrency, screens, API, storage, permissions, isolation, privacy, audit, performance, deferred items, troubleshooting
- [team-scheduler.md](team-scheduler.md): the Team schedule board, Day and Week (and why not Month), the time axis and zone rules with the clock-change cases, click / drag / drop / resize and the drawer, conflicts and overrides on the board, the unscheduled queue, Find inside the scheduler, filters, states and messages, the two reads, permissions, client isolation, performance, no added dependencies, skills deferred, accessibility, troubleshooting
- [permissions-and-security.md](permissions-and-security.md): who sees and changes what; tenant and client isolation; audit
- [testing.md](testing.md): what is tested and how
- [troubleshooting.md](troubleshooting.md)
