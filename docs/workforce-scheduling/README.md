# Workforce scheduling

Planning work against real capacity: when each technician normally works, what they are skilled in,
and later what is booked, what is free, and how planned work compares with actual work.

**Scope boundary.** This is capacity planning, not HR. Working hours exist only to establish usable
work capacity. There is no attendance, clock-in/out, biometrics, payroll, salary, leave balances or
accrual, HR compliance, recruitment, appraisal, or room/vehicle/equipment scheduling.

| Phase | What | Status |
|---|---|---|
| 0 | Discovery, audit and architecture | Done |
| 1 | Working schedules (overnight, breaks, time zones, versions) + skills | **This release** |
| 2 | Capacity and availability engine | Planned |
| 3 | Work allocation domain + conflict engine | Planned |
| 4 | Team scheduler | Planned |
| 5 | Find available technician | Planned |
| 6 | My Day / My Schedule | Planned |
| 7 | Planned vs actual | Planned |
| 8 | Capacity and utilization analytics | Planned |
| 9 | Heatmap and advanced reporting | Planned |
| 10 | Security, performance and final QA | Planned |

The module is behind a switch, `Features:Workforce`, which is **off by default**. When it is off, the
menu entry, the tabs and every `api/workforce` route are hidden (the routes answer 404).

- [architecture.md](architecture.md): data model, API, where it sits in PIO
- [schedules.md](schedules.md): working windows, overnight, breaks, time zones and DST, versions
- [skills.md](skills.md): the skill catalogue and levels
- [permissions-and-security.md](permissions-and-security.md): who sees and changes what; tenant and client isolation; audit
- [testing.md](testing.md): what is tested and how
- [troubleshooting.md](troubleshooting.md)
