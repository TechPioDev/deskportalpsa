# Release Checklist & Rollback Plan

## Pre-release gates
- [ ] `dotnet test Desk.sln -c Release` green (136/136)
- [ ] Web `npm run typecheck && npm run build` clean; `npm audit --omit=dev` clean
- [ ] CI green: build, tests, gitleaks, dependency scan
- [ ] No open critical/high defects (see [qa-report.md](../testing/qa-report.md))
- [ ] DB migrations reviewed and reversible; `dotnet ef migrations script` diffed
- [ ] `ConnectionStrings`, `Keycloak`, `Secrets:EncryptionKey` configured per env — startup refuses to run without a real encryption key in Production
- [ ] `Connectors:BlockPrivateEgress` not set to `false` on the api **or the worker** (the SSRF guard is on by default; the worker makes every scheduled PSA call)
- [ ] Backups verified recent; a restore drill has passed ([backup-and-recovery.md](backup-and-recovery.md))
- [ ] **Live gates** (production GA): DAST/ZAP, penetration test, k6 load run to §13 targets

## Deploy steps (self-hosted)
1. Snapshot: `pg_dump -Fc` (rollback point — this now includes encrypted PSA credentials).
2. Bring up backing services (Postgres/Redis/RabbitMQ/MinIO/Keycloak).
3. Apply migrations: `dotnet ef database update` (forward-only; verified reversible in staging).
4. Deploy API + Worker (rolling); deploy web.
5. Smoke: `/health/ready` green; login; load a ticket; run a connection test.

## Rollback plan
| Trigger | Action |
|---|---|
| API/Worker fails health after deploy | Redeploy the previous image tag (stateless services). |
| Migration caused a regression | Roll back to the prior migration: `dotnet ef database update <PreviousMigration>`; if not cleanly reversible, restore the pre-deploy `pg_dump` snapshot. |
| Data corruption | Restore Postgres from the pre-deploy snapshot (PITR to just before deploy). |
| Secret-store issue | Restore Postgres from the pre-deploy snapshot (`secret_blobs` is a normal table in it); if `SECRET_ENCRYPTION_KEY` itself changed, restore the prior value from its offsite copy — never regenerate it while connections exist. |
| Web-only regression | Redeploy previous web build (independent of API). |

Rollback is **safe by construction**: services are stateless (image tag swap), migrations are reviewed
for reversibility, and a fresh pre-deploy snapshot is always taken. Target rollback time: < 30 min.

## Post-release
- [ ] Monitor error rate + correlation-id logs for 24h
- [ ] Verify sync health per connection (pending/DLQ counts stable)
- [ ] Tag the release; update [CHANGELOG.md](../../CHANGELOG.md)
