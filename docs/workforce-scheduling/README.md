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
| 3 | Work allocation, My plan and authorized scheduling: planning your own work, scheduling others (fixed or flexible), moving, giving away and taking out, overrides with a reason, internal work raised and planned in one step, finished work released | **This release** |
| 4 | Team scheduler | Planned |
| 5 | Find available technician: assigning from the search (the search itself arrived in Phase 2; the plan dialog already offers it) | Planned |
| 6 | My Day / My Schedule (My plan arrived in Phase 3) | Planned |
| 7 | Planned vs actual | Planned |
| 8 | Capacity and utilization analytics | Planned |
| 9 | Heatmap and advanced reporting | Planned |
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
- [permissions-and-security.md](permissions-and-security.md): who sees and changes what; tenant and client isolation; audit
- [testing.md](testing.md): what is tested and how
- [troubleshooting.md](troubleshooting.md)
