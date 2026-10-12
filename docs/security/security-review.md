# Security Review (Phase 9)

Independent review of the implemented platform against the OWASP Top 10 and the spec's security
requirements. "Verified" = covered by automated tests or a runnable scan; "Pending (live)" = needs a
running stack (DAST/pen-test/load) not available in the current environment.

## Dependency & secret posture (runnable now)
- **.NET**: `dotnet list package --vulnerable --include-transitive` → **0 vulnerable packages**.
  Build gate: `TreatWarningsAsErrors` + NuGetAudit (already caught & remediated an OTLP-exporter CVE).
- **Web (production bundle)**: `npm audit --omit=dev` → **0 vulnerabilities**. sharp/postcss pinned via
  overrides. Residual 9 highs are **dev-only** (ESLint `brace-expansion` DoS in a glob matcher) — not
  bundled, not a production surface. Remediation: ESLint 10 major upgrade (deferred, low risk).
- **Secrets**: gitleaks in CI; repo scan for key patterns → clean. Dev fixtures allow-listed.

## OWASP Top 10 mapping

| # | Risk | Status | Notes |
|---|---|---|---|
| A01 | Broken access control | ✅ Verified | Permission-claim authz on every endpoint; portal detail returns `null` (not 403) cross-tenant/company to avoid existence leaks; 10 tenant/company-isolation tests. |
| A02 | Cryptographic failures | ✅ / Pending | HTTPS+HSTS; PSA secrets in Vault only. Field-level encryption for extra-sensitive columns: **deferred**. |
| A03 | Injection | ✅ Verified | EF Core parameterizes all queries; **no raw SQL / string-built queries** in the codebase. React auto-escapes; no `dangerouslySetInnerHTML`. |
| A04 | Insecure design | ✅ | Connector capability model, sync loop-prevention, append-only audit, fail-closed tenant default. |
| A05 | Security misconfiguration | ✅ | CSP, X-Frame-Options DENY, nosniff, Referrer-Policy, HSTS; CORS allowlist; 25 MB request cap; prod refuses the in-memory secret store. |
| A06 | Vulnerable components | ✅ Verified | See dependency posture above. |
| A07 | Auth failures | ✅ / Pending | Keycloak OIDC; web login is auth-code + **PKCE (S256)** with tokens in **httpOnly cookies** (BFF — never in client JS) and refresh-on-401; short-lived tokens; brute-force + lockout in realm. Full round-trip test: **pending (needs a running Keycloak)**. |
| A08 | Integrity failures | ✅ Verified | Webhook deliveries need the connection's own secret: an HMAC over the timestamp **and** the body, so a captured delivery cannot be re-sent under a new time. A connection with no secret accepts none. Idempotency + update-hash echo suppression. |
| A09 | Logging/monitoring | ✅ | Structured Serilog + correlation IDs; **immutable audit log** (modify/delete throws); admin actions audited. |
| A10 | SSRF | ✅ Verified | A connection's API address is checked when it is saved (`ConnectorEndpointPolicy`: https, no credentials or query in it, not a private address, and an Autotask connection must be on autotask.net). `EgressGuard` then blocks connector calls to loopback/private/link-local/reserved hosts (incl. the 169.254.169.254 metadata endpoint, and IPv4-mapped, NAT64 and 6to4 forms). It is **on unless the process is in local mode**, in the API and the worker alike; the transport connects to the address it checked and follows no redirects. A self-hosted PSA on a private network is allowed by the operator in `Connectors:AllowedHosts`. |

## Multi-tenant isolation (defense in depth) — Verified
1. DB global query filter on every `ITenantScoped` entity + write guards (cross-tenant insert/modify throws).
2. `AuditLog` and `AppUser` are **not** globally filtered by design — their services scope explicitly;
   dedicated adversarial tests confirm no leak (both tenants in one shared store).
3. Fail-closed: an unresolved scope matches `Guid.Empty` → zero rows.
4. Platform super-admins operate under an explicit, opt-in platform scope only.
5. Code that runs under platform scope for one connection (the scheduled sync, the activity rollup,
   the webhook route) names that connection's organization in its own queries: the global filter is
   off there. Mapping rules are loaded through `ConnectionMappingRules`, by organization and
   connection.
6. A person's PSA login is a pair, connection + id (`UserPsaIdentity`). Ticket visibility and
   analytics resolve it as a pair; the same id on another PSA account is somebody else.

## Findings & recommendations
| Severity | Finding | Recommendation |
|---|---|---|
| ~~Medium~~ Resolved | SSRF surface via admin-configured connection URLs. | **Implemented**: `EgressGuard` blocks private/reserved egress (tested). |
| ~~High~~ Resolved (Phase 9) | The guard was opt-in and the production worker was never opted in; the address was saved unchecked. | On by default outside local mode; address checked at save; pinned transport, no redirects. |
| ~~High~~ Resolved (Phase 9) | The scheduled sync loaded every organization's mapping rules for a provider; a rule's connection id was stored as sent. | Rules are loaded by organization and connection; a rule's connection is checked when saved. |
| ~~High~~ Resolved (Phase 9) | "Assigned to me" read a per-person PSA id that nothing wrote, so it did not follow a technician's PSA link, and could not tell two PSA accounts apart. | Visibility resolves logins per connection from the links. |
| ~~High~~ Resolved (Phase 9) | With no webhook secret stored, the signing key was empty. | No secret, no delivery; timestamp signed; size and rate limits on the route. |
| ~~High~~ Resolved (Phase 9, slice 2a) | A change made in the PSA during a sync run could be missed; an import larger than 5,000 tickets never completed. | Sync cursors, continuation and one run per connection. See the audit, D1 and D2, and `docs/integrations/sync-engine.md`. |
| ~~Medium~~ Resolved (Phase 9, slice 3a) | New credentials replaced the stored ones before anything had tried them, and a changed address had the stored credentials sent to it unchecked (audit S4). | Both are tried against the PSA first; a refusal leaves the connection and its stored credentials exactly as they were. |
| ~~Low~~ Resolved (Phase 9, slice 3a) | A new connection was enabled the moment it was saved (audit S5). | Saved switched off; switched on only by a route that refuses until a test has passed. The same PSA account cannot be connected twice. |
| ~~Low~~ Resolved (Phase 9, slice 3a) | Credentials a PSA had rejected were sent again on every polling cycle, which is how an API account gets locked. | Polling of that connection stops until credentials that pass a test are saved; the needs-attention list says so. |
| ~~Low~~ Resolved (Phase 9, slice 3b) | Capability flags said more than the connectors do: inbound webhooks (both), custom fields (ConnectWise) (audit S6). | A capability is claimed only where the connector does the thing; a test holds it. |
| ~~Low~~ Resolved (Phase 9, slice 3b) | "Test connection" proved only that the PSA accepted the credentials, so an API account with no right to read tickets was switched on. | The test tries each read the sync needs, reads only, and a connection in setup cannot be switched on until they pass. |
| ~~Low~~ Resolved (Phase 9, slice 6a) | A background job was taken by reading it and marked running only after its handler returned: two workers would both have run it, and a worker that stopped left no trace that it had started (audit R11). One worker runs today. | Taking a job is a save of its own, checked by the job's version, and held for a lease; a second worker's save is refused, and a job whose worker stopped is taken again when the lease runs out. Proven on PostgreSQL with six workers and thirty jobs: each taken once, 150 saves refused. |
| ~~Low~~ Resolved | Attachment malware scanning / quarantine / signed URLs. | **Implemented**: extension/MIME/size validation, EICAR/PE scan, quarantine (bytes never stored), randomized keys, HMAC time-limited signed URLs, audited downloads (7 tests). Production binds ClamAV + MinIO. |
| Low | Field-level encryption for PII columns not implemented. | Add column encryption for requester PII if required by the data-classification policy. |
| Info | Dev-only ESLint advisory (`brace-expansion`). | Upgrade to ESLint 10 at a convenient major-version bump. |

## Pending a live environment (production-readiness gates)
- DAST (OWASP ZAP) against the running API.
- Authenticated authorization fuzzing across roles.
- Penetration test.
- Load/performance test to the §13 targets (see `tests/load/k6-smoke.js`).
- Backup restore drill (see `docs/deployment/backup-and-recovery.md`).

**Verdict:** The Phase 9 integration audit (5 Oct 2026,
[PHASE9_PSA_INTEGRATION_ARCHITECTURE_AUDIT.md](../integrations/PHASE9_PSA_INTEGRATION_ARCHITECTURE_AUDIT.md))
found six high-severity defects in the PSA integration. Four are resolved above; two, both about
sync completeness rather than access, are open and scheduled. Production sign-off remains contingent
on those and on the live-environment gates above.
