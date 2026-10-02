# Testing (Phases 1 and 2)

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
| Unit: capacity arithmetic | `CapacityEngineTests` (32) | Interval merge (overlapping, touching), subtract, intersect, exact boundaries; standard day; non-working day; several and overlapping breaks; part-day and full-day unavailable; overlapping and adjacent exceptions; exception over a break; additional availability (incl. day off, touching the window, overridden by unavailable); confirmed work and the gaps it leaves; confirmed vs tentative; tentative under confirmed; fully booked; duration search and insufficient continuous slot; search limited to a window; overnight; three time zones; spring-forward and fall-back nights (New York, London); a break in the skipped hour and in the repeated hour; work booked across the repeated hour. Every case also checks the formula identities and that nothing is negative. |
| Unit: conflicts | `ConflictEvaluatorTests` (20) | Each conflict type and its severity; hidden work id; day off; over capacity vs a clash at that hour; tentative proposals; every conflict at once; night shift across midnight; re-check after someone else books |
| Integration: services | `WorkforceCapacityTests` (51) | The ten flows: own capacity; part-day and full-day exceptions; 90-minute search; skill filter (ALL and ANY); team filter; conflict detection; overnight; another organization. Plus scope (own / team / all), who may record time away, validation, plain-text notes, duplicates, audit, version changes, holidays, extra availability next to a night shift, nothing in the past, no caching, the reason and note withheld from a colleague, a non-staff sign-in refused by every action |
| Endpoint security | `EndpointAuthorizationTests` | Required permission of each capacity action; no client account reaches any workforce controller (found by route); no client role holds a workforce permission; capacity reads are GETs; nothing a client receives carries a workforce field |
| SQL translation and query count | `CapacityPerformanceTests` (5) | Every capacity query on a real SQL engine; the number of queries is the same for 50, 100 and 500 people and for 1, 7 and 14 days; timings printed |
| PostgreSQL 17 | The same class with `DESK_TEST_POSTGRES` set (throwaway container, before merge) | All of the above on Postgres, on a database built by the real migrations; the capacity migration applied to a database that already has staff and schedules, applied again, and taken back (`Down` removes the one table and nothing else) |
| SQL regression | `RelationalQueryTests.A_schedule_can_be_corrected_on_the_day_it_starts` | The same-day correction that answered 500 on a real database |
| E2E | `e2e/workforce-capacity.spec.ts` (5) | Technician: My capacity, own-only. Administrator: time away through the form (part day, duplicate refused, full day, remove, reload). Team capacity by date, skill and team. Find 90 minutes with a required skill, and the conflict check agrees. An account without the permission: no menu, pages refuse, every endpoint 403 |

**Known limitation:** local test mode has no client login, so "client is denied" is proven by the
endpoint tests rather than in the browser. The browser test uses the nearest thing: a signed-in
account that holds no workforce permission, which is exactly what a client is to this module.
