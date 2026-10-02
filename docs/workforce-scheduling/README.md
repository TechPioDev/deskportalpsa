# Workforce scheduling

Planning work against real capacity: when each technician normally works, what they are skilled in,
and later what is booked, what is free, and how planned work compares with actual work.

**Scope boundary.** This is capacity planning, not HR. Working hours exist only to establish usable
work capacity. There is no attendance, clock-in/out, biometrics, payroll, salary, leave balances or
accrual, HR compliance, recruitment, appraisal, or room/vehicle/equipment scheduling.

| Phase | What | Status |
|---|---|---|
| 0 | Discovery, audit and architecture | Done |
| 1 | Working schedules (overnight, breaks, time zones, versions) + skills | Done |
| 2 | Capacity, availability and free-time engine: capacity exceptions, usable and remaining capacity, exact free slots, team capacity, the technician search, conflict evaluation | **This release** |
| 3 | Work allocation and personal planning (booking work into people's time, self-planning, scheduling others, overrides) | Planned |
| 4 | Team scheduler | Planned |
| 5 | Find available technician: assigning from the search (the search itself arrived in Phase 2) | Planned |
| 6 | My Day / My Plan / My Schedule | Planned |
| 7 | Planned vs actual | Planned |
| 8 | Capacity and utilization analytics | Planned |
| 9 | Heatmap and advanced reporting | Planned |
| 10 | Security, performance and final QA | Planned |

**Internal only.** Scheduling, capacity and availability are for staff. No client account can reach
any of it, and a ticket a client can see does not make its planning visible to them. Client
ticketing and workforce scheduling are separate security domains
([permissions-and-security.md](permissions-and-security.md)).

The module is behind a switch, `Features:Workforce`, which is **off by default**. When it is off, the
menu entry, the tabs and every `api/workforce` route are hidden (the routes answer 404). In production
set `FEATURES_WORKFORCE=true` in `.env.prod` and redeploy; `false` hides it again and keeps the data.

- [architecture.md](architecture.md): data model, API, where it sits in PIO
- [schedules.md](schedules.md): working windows, overnight, breaks, time zones and DST, versions
- [skills.md](skills.md): the skill catalogue and levels
- [capacity.md](capacity.md): terms, the capacity formula, confirmed and tentative work, interval normalization, the free-slot algorithm, the technician search, time zones, overnight, DST, performance
- [availability-exceptions.md](availability-exceptions.md): time unavailable and additional availability
- [conflicts.md](conflicts.md): conflict types, which block and which can be overridden, why availability is not a reservation
- [permissions-and-security.md](permissions-and-security.md): who sees and changes what; tenant and client isolation; audit
- [testing.md](testing.md): what is tested and how
- [troubleshooting.md](troubleshooting.md)
