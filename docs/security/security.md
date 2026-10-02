# Security Model

## Tenant isolation (defense in depth)
1. **Database** — `DeskDbContext` applies a global query filter to every `ITenantScoped` entity;
   reads are constrained to the current tenant. An unresolved scope matches `Guid.Empty` → **zero
   rows (fail closed)**. Writes are stamped with the active tenant and cross-tenant inserts/updates
   throw. Platform super-administrators run under an explicit platform scope that bypasses the filter.
2. **API** — authentication + permission-claim authorization on every endpoint.
3. **Cache** — Redis keys are tenant-prefixed (implemented with the caching layer).
4. **Queue** — messages carry a tenant stamp; handlers set scope before touching tenant data.
5. **UI** — data is tenant-scoped server-side before it reaches the browser.

## Identity & access
- Keycloak OIDC/OAuth2; access tokens short-lived (5 min), refresh via Keycloak; brute-force
  protection and account lockout enabled in the realm.
- Authorization is **permission-claim based** (`Desk.Domain.Authorization.Permissions`), never a
  role-name check. The DB is the source of truth; claims are enriched per-request from roles.
- Seven built-in roles seeded with least-privilege claim sets.
- **Internal work stays internal.** One client rule (`TicketReadService.ClientVisible`) serves every
  client-facing path: list, detail, search (public notes only), comments and attachments. Internal
  tickets answer a client with "not found". Staff reach tickets through `TicketScopeQuery`, where an
  action is always bounded by sight. Board routes need a staff ticket view; organization-wide ticket
  lists need all-tickets sight. Monitoring alerts reach a client only from a source pinned to that
  client. The tests for each are in `InternalWorkIsolationTests` (Phase 0 of the Internal Service
  Desk, 30 Sep 2026).

### View as (administrators)

An administrator can see the portal as any staff member or client portal user. They see that
person's menu, tickets and figures, without their password and without acting as them.

- **Who:** holders of both `users.manage` and `roles.manage`, which is the Administrator role and not
  a Manager. A platform administrator can only be viewed by another platform administrator.
- **Whom:** active people in the same organization, never yourself. A client can only be viewed once
  they have signed in, because client access is found by sign-in identity.
- **How:**
  - The web proxy keeps the choice in an httpOnly cookie (`desk_view_as`) and forwards it as
    `X-Desk-View-As`. The browser cannot send that header itself, because the proxy's header
    allow-list drops it.
  - The header grants nothing on its own. On every request, `DeskClaimsTransformation` re-checks that
    the real caller may view as someone, and that this person may be viewed (`ViewAs.ResolveAsync`).
    Only then does the request run with that person's claims, plus markers naming the administrator.
  - Otherwise the request runs as the caller, as if nothing had been asked.
- **Read-only:** `ViewAsReadOnlyMiddleware` refuses every POST, PUT, PATCH and DELETE while viewing,
  before any controller runs. One rule covers every endpoint.
- **Recorded:** the start and end of each view (`user.view_as.started` / `user.view_as.ended`) are
  audited under the administrator. Anything audited during a view names the administrator too, as
  "Harpal (viewing as Sarabjit)", never the person viewed.
- **Leaving:** **Exit view** in the banner. Signing out also clears the view, and the cookie expires
  after 8 hours.

### Workforce schedules and skills

Staff only. Reading needs `schedule.view` (scoped Own/Team/Department/All) and changing needs
`workforce.manage`. No client role or client login holds either. Scope is checked per person on the
server, and another organization's people and skills are "not found". See
`docs/workforce-scheduling/permissions-and-security.md`.

### Workforce capacity and availability: internal only

Capacity, free slots, team capacity, the technician search, conflict checks and capacity exceptions
are staff-only, behind the same `schedule.view`; recording an exception also needs the scoped
`availability.manage`. Client ticketing and workforce scheduling are separate security domains: a
ticket a client can see does not make its planning visible. Tests fail if a client role gains a
workforce permission, if any controller under `api/workforce` becomes reachable by a client, or if
a ticket or client-portal response shape gains a workforce field. Conflicts never carry a ticket's
title, client or number, and name the blocking work only to a caller who may see it.

## Secret handling
- PSA credentials are encrypted at rest with **AES-256-GCM** (`EncryptedDbSecretStore`), keyed by a
  master key held only in the host's `.env.prod` (`Secrets:EncryptionKey`), never in the database.
  The connection row stores only an opaque reference (`CredentialSecretRef`); plaintext values
  never touch the database, logs, or API responses.
- Production startup **refuses to run** with the in-memory dev secret store, on both the API and the
  worker.
- `CredentialSecretRef` is never projected into any API response.
- The encryption key is a single point of failure by design — anyone who can decrypt PSA
  credentials needs both database access and the key, which live in different places. Losing the
  key makes every stored credential permanently unreadable; back it up like the database itself.

## API hardening
- RFC-7807 problem responses; internal error detail is logged, not returned.
- Correlation id on every request/response and log line.
- Global rate limiter, 25 MB request cap, CORS allowlist. The limiter is chained: 300 requests a
  minute for each PERSON (their portal user id, else the token subject, else their address) and
  3,000 a minute for their whole organization. Per organization alone made colleagues compete for
  one budget — a single page load is several requests — and per person alone would let one tenant's
  headcount set the load the host must carry. Routes that must be anonymous carry their own,
  narrower policies: public forms 5 per 10 minutes per address, monitoring alerts 120 a minute per
  source key, so neither can spend the desk's allowance.
- Security headers: `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy`, CSP;
  HSTS + HTTPS redirect outside development.

## Audit
- `audit_log` is **append-only** — the DbContext throws on any modify/delete of an existing entry.

## Supply chain
- `TreatWarningsAsErrors` + NuGet audit fail the build on any known package vulnerability
  (already caught and remediated the OTLP exporter advisory GHSA-4625-4j76-fww9 during Phase 2).
- gitleaks secret scan and `dotnet list package --vulnerable` run in CI.

## Deferred to later phases
Attachment malware scanning + quarantine + signed URLs (attachment service), webhook signature/
timestamp validation (integration framework), field-level encryption, DAST/penetration testing
(security & performance phase).
