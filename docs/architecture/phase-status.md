# Phase & Provider Status

## Delivery phases

| Phase | Name | Status |
|---|---|---|
| 1 | Discovery / architecture | ✅ Complete (approved) |
| 2 | Foundation | ✅ Complete |
| 3 | PSA integration framework | ✅ Complete |
| 4 | Autotask integration | ✅ Complete |
| 5 | ConnectWise integration | ✅ Complete |
| 6 | Client portal | ✅ Complete |
| 7 | Technician & manager dashboards | ✅ Complete |
| 8 | Administration | ✅ Complete |
| 9 | Security & performance | ✅ Complete (code + artifacts; live gates pending a stack) |
| 10 | Final QA & production readiness | ✅ Complete (feature-complete; GA gated on live validations) |

## Phase 2 — Foundation acceptance

| Criterion | Status | Evidence |
|---|---|---|
| Monorepo + Docker Compose (pg/redis/rabbitmq/minio/keycloak/vault) | ✅ authored | `infrastructure/docker/docker-compose.yml` |
| EF Core schema + initial migration | ✅ | `packages/infrastructure/Persistence/Migrations/*InitialSchema*` |
| Tenant context + global query filter | ✅ | `DeskDbContext`, 6 isolation tests pass |
| Keycloak OIDC auth on API | ✅ | `Program.cs` JwtBearer + realm import |
| Permission-claim authorization (7 roles) | ✅ | `PermissionPolicyProvider`, RBAC tests pass |
| Secret provider abstraction | ✅ | `ISecretStore`; production uses `EncryptedDbSecretStore` (Vault dev-mode lost secrets on restart and was retired) |
| Serilog structured logging + correlation id + RFC-7807 | ✅ | middleware trio |
| CI (build, test, secret scan, dep scan) | ✅ authored | `.github/workflows/ci.yml` |
| Health + Swagger | ✅ | `/health`, `/health/ready`, Swagger (dev) |

**Verified locally:** `dotnet build` clean (warnings-as-errors), 15/15 unit tests green, migration generated.
**Requires Docker Desktop (not on this machine):** `docker compose up`, `dotnet ef database update`,
live Keycloak/Vault, and `npm run build` for the web app.

## Phase 3 — Integration framework acceptance

| Criterion | Status | Evidence |
|---|---|---|
| Connector interface + capability model | ✅ | `IServiceManagementConnector`, `ProviderCapabilities` |
| Connector factory + resolver | ✅ | `IConnectorFactory`, `ConnectorResolver` |
| Mock PSA connector works | ✅ | `MockConnector` — full contract + fault injection |
| Contract / certification suite passes | ✅ | `ConnectorCertificationTests` (18 tests) |
| Field-mapping engine (8-scope resolution) | ✅ | `MappingEngine`, `MappingEngineTests` (10 tests) |
| Retry / backoff / circuit breaker | ✅ | `ResilientExecutor`, `CircuitBreaker`, `ResilienceTests` (9) |
| Failed-job handling (retry + dead-letter) | ✅ | `JobProcessor`, `JobProcessorTests` (3) |
| Webhook validation framework (sig + timestamp) | ✅ | `WebhooksController`, webhook tests |
| Sync loop-prevention (idempotency + hash + echo) | ✅ | `SyncEventStore`, `UpdateHasher`, `SyncTests` (4) |
| Polling / reconciliation framework | ✅ | `PollingSyncService` (skeleton) |

**Verified locally:** Release build clean, **55/55 unit tests green** (15 Phase 2 + 40 Phase 3).

## Phase 6 — Client portal acceptance

| Criterion | Status | Evidence |
|---|---|---|
| Ticket list / detail / create / comment | ✅ | `TicketsController`, `TicketReadService`, `TicketCommandService` |
| Notifications + profile | ✅ | `PortalController` |
| **No internal notes exposed** | ✅ | read service filters `IsPublic`; internal notes never persisted; test |
| **Client-company scoping / tenant isolation** | ✅ | `Visible()` filter; cross-company test returns nothing |
| PSA-first writes with echo suppression | ✅ | create/comment record portal-origin sync events |
| Frontend (list/detail/create/notifications/profile) | ✅ | Next.js pages, React Query, Zod validation |
| Responsive + dark mode + no console errors | ✅ | verified mobile 375px + dark in browser |

**Verified locally:** .NET Release clean, **98/98 unit tests green**; web typecheck + build clean;
client portal pages render with graceful empty states, form validation works, 0 console errors.

## Phase 7 — Dashboards acceptance

| Criterion | Status | Evidence |
|---|---|---|
| Configurable weighted productivity score | ✅ | `ProductivityScorer` (renormalizes over measured components), 6 tests |
| Score calculations pass | ✅ | known-value + clamp + configurable-weights tests |
| Metrics + time calculations correct | ✅ | `TechnicianMetricsService`, counts/SLA/avg-resolution tests |
| Filters work | ✅ | date/technician/company/priority filter tests |
| Team comparison + trend | ✅ | grouped, ranked; per-day trend |
| CSV export accurate | ✅ | `DashboardController` export with escaping + disclaimer header |
| "Operational indicator" guardrail surfaced | ✅ | disclaimer on every API response + shown in UI |

**Verified locally:** .NET Release clean, **110/110 unit tests green**; web build clean; productivity
page renders with the disclaimer, score card, tiles, trend sparkline, team table — 0 console errors.

## Phase 8 — Administration acceptance

| Criterion | Status | Evidence |
|---|---|---|
| PSA connection management | ✅ | `ConnectionAdminService` (create/list/enable) |
| **Secrets remain hidden** | ✅ | creds → Vault, only ref on row; DTO has no secret field; audit excludes creds; tests |
| Mapping management | ✅ | `MappingAdminService` list/upsert |
| **Mapping changes are audited** | ✅ | every upsert writes a version snapshot + audit entry; test |
| Mapping versioning + rollback | ✅ | `FieldMappingVersion` snapshots; rollback restores prior state; test |
| User / role management | ✅ | `UserAdminService` (org-scoped, audited role changes) |
| Audit log viewer | ✅ | `AuditQueryService` (tenant-scoped) + UI |
| Integration health | ✅ | `IntegrationHealthService` (pending/DLQ/failed per connection) |
| **Failed jobs can be reprocessed** | ✅ | `JobMonitorService.ReprocessAsync` (DLQ→Queued, audited); test |

**Verified locally:** .NET Release clean, **117/117 unit tests green**; web build clean; admin pages
render (connection form shows Vault-secrets note + masked Secret field), 0 console errors.

## Phase 9 — Security & performance acceptance

| Criterion | Status | Evidence |
|---|---|---|
| Dependency scanning | ✅ Verified | `.NET` 0 vulnerable; web prod bundle 0 vulns (dev-only ESLint advisory documented) |
| Secret scanning | ✅ Verified | gitleaks in CI; repo pattern scan clean |
| Tenant isolation testing | ✅ Verified | 10 isolation tests incl. adversarial cross-tenant over shared store (AuditLog/AppUser) |
| Vulnerability scanning / hardening | ✅ | SSRF `EgressGuard` (opt-in, tested); security headers, rate limit, request cap in place |
| Performance (indexes) | ✅ | `PerformanceIndexes` migration for portal/dashboard query patterns |
| Load testing | ⏳ Authored | `tests/load/k6-smoke.js` with §13 thresholds — needs a running stack |
| Backup / DR | ⏳ Authored | runbook + `infrastructure/scripts/backup.sh` — live restore drill needs a stack |
| Penetration / DAST | ⏳ Pending | needs a running stack (documented in security-review.md) |

**Verified locally:** .NET Release clean, **136/136 unit tests green** (+15 egress classifier, +4 cross-tenant);
security review = no critical/high in code; live load/DR/pen-test gates documented as pending a stack.

**Current state (15 Sep 2026):** live at piomanage.com; 685 unit tests green in CI. Since the phases above:
scheduled staff reports (PDF + CSV), client business reviews, organization email settings, mapping
snapshots, and an endpoint-authorization test across every controller.

## Provider readiness matrix

Statuses: Planned · API Research · Foundation · In Progress · Integration Testing · QA · Limited · **Production Ready** · Blocked · Unsupported.

| Wave | Provider | Status |
|---|---|---|
| 1 | ConnectWise PSA | **Limited** — live on piomanage.com against the ConnectWise *staging* sandbox; a board status must have Closed ticked for closure dates to sync |
| 1 | Datto Autotask PSA | **Production** — live on piomanage.com with real tickets, notes, attachments, time entries and field mappings |
| 2 | HaloPSA | Planned |
| 2 | Syncro | Planned |
| 2 | SuperOps | Planned |
| 3 | Atera / Kaseya BMS / N-able MSP Manager / DeskDay | Planned |
| 4 | ServiceNow / Freshservice / Jira Service Management | Planned |
| 5 | ManageEngine SDP / Zendesk / Zoho Desk | Planned |
