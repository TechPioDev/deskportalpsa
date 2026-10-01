# Testing (Phase 1)

| Layer | Where | Covers |
|---|---|---|
| Unit: arithmetic | `WorkingWindowTests` (19) | Day shift, different hours, overnight, several breaks, zero length, overlap, outside the window (incl. overnight), no time left, break limit, time-zone placement (+05:30 and -05:00), spring-forward and fall-back nights, the skipped and the repeated hour, IANA normalization |
| Unit: services | `WorkforceFoundationTests` (12) | Save and read back; overnight; versions and withdrawal; past start refused; every problem at once; technician own-only and read-only; team-lead scope; another organization's people and skills not found; copy to others; offered/not offered; inactive people; skill catalogue, assignment, levels, retire, any/all filters; plain-text names; audit with correlation id |
| Endpoint security | `EndpointAuthorizationTests` | Each workforce action's required permission; no client login can reach any of them |
| Permission matrix | `PermissionGoldenMatrixTests` | New keys recorded deliberately per role; technician scope is Own |
| SQL translation | `RelationalQueryTests.Workforce_schedules_and_skills_translate` | Scope subqueries, all-skills count, holder counts, time/date columns on a real SQL engine |
| PostgreSQL 17 | One-off check before merge (throwaway container) | Migration on a database with existing staff keeps them offered; services on Postgres; `Down` removes exactly Phase 1 |
| E2E (Chromium, Firefox, WebKit in CI) | `e2e/workforce.spec.ts` (4) | Admin sets and reloads a schedule; night shift + refused break + server-side refusal; skill catalogue, person, overview filter, retire; technician sees only their own schedule (View as) |

**Known limitation:** local test mode has no client login, so "client is denied" is proven by the
endpoint tests rather than in the browser.
