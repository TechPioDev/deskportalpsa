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
