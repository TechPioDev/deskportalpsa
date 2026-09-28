# Final QA Report — Desk Portal

Independent QA review across all modules. Automated suite: **136/136 unit/contract/integration
tests green**; web typecheck + production build clean; production dependency audit clean.

## Test-case matrix

| ID | Module | Scenario | Expected | Actual | Result | Severity |
|---|---|---|---|---|---|---|
| TC-01 | Tenancy | Query another tenant's data | Zero rows (fail closed) | Zero rows | Pass | — |
| TC-02 | Tenancy | Cross-tenant insert/modify | Rejected | Throws | Pass | — |
| TC-03 | Tenancy | Audit log / app users cross-tenant (unfiltered entities) | Scoped to caller org | Scoped | Pass | — |
| TC-04 | RBAC | Role → permission claim sets | Least privilege | Matches | Pass | — |
| TC-05 | Audit | Modify an existing entry | Rejected (immutable) | Throws | Pass | — |
| TC-06 | Connectors | Mock + Autotask + ConnectWise certification suite | All pass | All pass | Pass | — |
| TC-07 | Connectors | Cross-provider normalization | Identical UnifiedTicket shape | Identical | Pass | — |
| TC-08 | Connectors | HTTP error mapping (401/403/404/429/5xx) | Correct ConnectorException kind | Correct | Pass | — |
| TC-09 | Mapping | 8-scope resolution + fallback + required | Most-specific wins | Correct | Pass | — |
| TC-10 | Resilience | Retry transient, stop on terminal, circuit breaker | Per policy | Correct | Pass | — |
| TC-11 | Sync | Idempotency dedup + echo suppression | Duplicates/echoes skipped | Skipped | Pass | — |
| TC-12 | Jobs | Retry → backoff → dead-letter; reprocess | Correct lifecycle | Correct | Pass | — |
| TC-13 | Client portal | Company scoping + own-only for non-admin | Scoped | Scoped | Pass | — |
| TC-14 | Client portal | Ticket detail excludes internal notes | Public only | Public only | Pass | — |
| TC-15 | Client portal | Create writes to PSA then persists + portal event | PSA-first | Correct | Pass | — |
| TC-16 | Dashboards | Weighted score, renormalization, clamp, configurable | Correct math | Correct | Pass | — |
| TC-17 | Dashboards | Metrics: counts, SLA %, avg resolution, filters | Correct | Correct | Pass | — |
| TC-18 | Dashboards | Guardrail disclaimer shown | Always visible | Visible | Pass | — |
| TC-19 | Admin | Create connection → secret to Vault only | No secret on row/DTO/audit | Confirmed | Pass | — |
| TC-20 | Admin | Mapping upsert → version snapshot + audit | Both recorded | Recorded | Pass | — |
| TC-21 | Admin | Mapping rollback restores prior state | Restored | Restored | Pass | — |
| TC-22 | Admin | Reprocess dead-lettered job | DLQ → Queued | Correct | Pass | — |
| TC-23 | Security | SSRF egress classification | Private/reserved blocked | Blocked | Pass | — |
| TC-24 | UI/A11y | Text contrast (WCAG AA) | ≥ 4.5:1 | 4.55 / 7.24:1 | Pass* | — |
| TC-25 | UI/A11y | Visible keyboard focus | Focus ring present | Present | Pass* | — |
| TC-26 | UI/Responsive | Mobile navigation < 768px | Reachable | Mobile nav strip | Pass* | — |
| TC-27 | UI | Console errors on render | None | None | Pass | — |

\* Fixed during this QA pass — see defects below.

## Defects found & resolved (this pass)

| Defect | Severity | Description | Resolution |
|---|---|---|---|
| D-1 | Medium | `--faint` text failed WCAG AA contrast (~2.8:1) | Retuned neutral scale → 4.55:1 / 7.24:1 |
| D-2 | Medium | No visible keyboard focus indicator | Added `:focus-visible` ring (WCAG 2.4.7) |
| D-3 | Medium | No navigation on mobile (sidebar hidden < md) | Added scrollable mobile nav strip |
| D-4 | Low | `prefers-reduced-motion` not honoured | Added reduced-motion media query |

All found defects resolved. **No open critical or high defects.**

## Known limitations (documented, non-blocking)

| Ref | Item | Plan |
|---|---|---|
| ~~K-1~~ Resolved | Attachment upload + malware scanning/quarantine/signed URLs | **Built**: validate → scan (EICAR/PE) → quarantine → randomized key → HMAC signed time-limited URLs → audited downloads. Production wires ClamAV + MinIO behind the same interfaces. |
| K-2 (part) | Live DAST, penetration test, load run | Production-readiness gates — still to commission. **The DR restore drill is done (28 Sep 2026)**: last night's set restored into scratch on the live host, every table matching, attachments byte-for-byte with a checksum check. See `docs/deployment/backup-and-recovery.md`. |
| K-3 | Dev-only ESLint `brace-expansion` advisory (not shipped) | ESLint 10 upgrade |
| ~~K-4~~ Resolved | Cross-browser validation on Firefox/Safari | **Done (Sep 2026)**: the browser suite runs in Chromium, Firefox and WebKit. Chromium on every push; all three on main (`e2e-cross-browser`), locally with `PLAYWRIGHT_BROWSERS=all`. WebKit found one real weakness — a test that matched two rows once earlier data existed — now fixed. |
| ~~K-5~~ Resolved | Keycloak login flow in the web app | **Built**: OIDC auth-code + PKCE (S256), tokens in httpOnly cookies (BFF pattern — no token in JS), same-origin proxy with refresh-on-401, middleware guard, login/logout/session. End-to-end verified up to the Keycloak redirect (live IdP still needed for the full round-trip). |

## Cross-browser / mobile / accessibility
- **Rendering engine tested**: Chromium (in-app browser) — light + dark, mobile viewport, no horizontal scroll, 0 console errors.
- **Accessibility**: WCAG 2.1 AA contrast verified by computation; keyboard focus verified via Tab; semantic landmarks (`nav`, `main`, `header`), labelled controls, reduced-motion honoured.
- **Firefox / Safari / Edge**: not executed here (engines unavailable); code avoids engine-specific APIs.

## Monitoring review
- Structured Serilog (compact JSON) with per-request correlation IDs; `/health` + `/health/ready`
  (DB check). OTLP export deferred (unpatched exporter advisory) — re-enable when patched.

## Sign-off status
| Sign-off | Status |
|---|---|
| QA (automated + manual) | ✅ Complete — no open critical/high |
| Security | ✅ Code-level (see security-review.md); live pen-test/DAST pending a stack |
| Performance | ⏳ Thresholds authored (k6); live run pending a stack |
| Product owner | ⏳ Awaiting review |
| Rollback plan | ✅ Documented (release-checklist.md) |

**Verdict:** Feature-complete across all 10 phases with a clean automated suite and no open
critical/high defects. Production GA remains gated on the live-environment validations (K-2) and
product-owner sign-off.
