# Backup & Disaster Recovery Runbook

Covers the self-hosted stack: PostgreSQL (also holds PSA credentials, encrypted), MinIO
(attachments), and Keycloak (identity). **Postgres and `SECRET_ENCRYPTION_KEY` are the crown
jewels** — losing the database is unrecoverable without a backup, and losing the key makes every
credential *in* that backup permanently unreadable even if the database itself is fine.

> This replaced a Vault container that ran in dev mode: its in-memory backend discarded every PSA
> credential on restart, which is exactly what happened in production before this rewrite (~26h of
> silently stalled sync). Credentials now live in the same durable store as everything else — one
> less process that can lose state independently of the database.

## Drill record

A restore is a claim until someone performs one. `infrastructure/scripts/restore-drill.sh` restores
the most recent set into scratch databases and a scratch directory on the live host, compares what
came back against what is live, and removes everything it made. Production is read only throughout:
nothing is stopped, replaced or written to.

**28 September 2026, against the 03:45 set of the same morning:**

| What | Restored | Live | Time |
|---|---|---|---|
| Tickets | 150 | 150 | database restored in 1s |
| Ticket notes | 362 | 362 | |
| Attachments (rows) | 37 | 37 | |
| Time entries | 16 | 16 | |
| Staff users | 41 | 41 | |
| Client companies | 11 | 11 | |
| Field mappings | 52 | 52 | |
| Audit entries | 269 | 269 | |
| Sign-in accounts | 3 | 3 | sign-in database restored in 1s |
| Sign-in clients | 15 | 15 | |
| Attachment files | 40 | 40 | attachments restored in under a second |
| Attachment bytes | 7,259,259 | 7,259,259 | byte-for-byte, and one file verified by checksum |

Two things the drill taught, both now fixed in the script: the tables added that same day are
absent from the night's backup, which is correct and expected rather than a shortfall; and comparing
directory sizes rather than file sizes made a clean restore look short by a few kilobytes and
invited a hunt for data that was never missing.

Run it again after any change to the backup script, and read the numbers rather than the exit code.

## What to back up

| Component | Contains | Backup method | RPO target |
|---|---|---|---|
| PostgreSQL | All portal data (tenants, tickets, mappings, audit, **encrypted PSA credentials**) | `pg_dump` (logical) + WAL archiving (PITR) | ≤ 5 min (with WAL) |
| `SECRET_ENCRYPTION_KEY` | The only key that decrypts the credentials in the Postgres backup above | Stored in `.env.prod` on the host; copy to an offsite secrets manager or password manager, **never alongside the Postgres backup** | on change (should not change) |
| MinIO | Attachment bytes | `mc mirror` to offsite bucket | ≤ 1 h |
| Keycloak | Realm, clients, users config | Realm export (`kc.sh export`) | on change |

Encrypt every backup at rest and in transit; store offsite; restrict access. Keeping the encryption
key separate from the Postgres backup is the whole point: a stolen database dump alone should not
be enough to read a PSA credential.

## Daily backup (scripted)

```bash
infrastructure/scripts/backup.sh          # see script below
```

- Postgres → `desk-YYYYMMDD.dump` (custom format, `pg_dump -Fc`) — includes the encrypted
  `secret_blobs` table
- MinIO → mirrored to the offsite bucket
- Keycloak realm → `desk-realm-YYYYMMDD.json`

`SECRET_ENCRYPTION_KEY` is not part of this script — it changes essentially never, so back it up
manually to wherever the rest of the host's root secrets live, the moment it is generated.

## Restore procedure (drill quarterly)

1. **Provision** a clean stack: `docker compose -f infrastructure/docker/docker-compose.yml up -d`.
2. **Postgres**: `pg_restore --clean --if-exists -d desk_portal desk-YYYYMMDD.dump`.
   Then apply any newer migrations: `dotnet ef database update`.
3. **Set `SECRET_ENCRYPTION_KEY`** in `.env.prod` to the value from offsite storage — it must be the
   exact key active when the Postgres dump was taken, or every `secret_blobs` row fails to decrypt.
   Verify a PSA credential reads back: the connection's `CredentialSecretRef` must resolve.
4. **MinIO**: `mc mirror offsite/desk-attachments local/desk-attachments`.
5. **Keycloak**: import the realm export (`--import-realm`).
6. **Verify** (acceptance):
   - API `/health/ready` is green.
   - A known ticket loads with its public conversation.
   - A PSA connection test succeeds (credentials decrypt correctly under the restored key).
   - The audit log shows history (append-only chain intact).
   - Tenant isolation spot-check: a client sees only their company's tickets.

## Disaster-recovery scenarios

| Scenario | Action |
|---|---|
| Postgres corruption | Restore latest dump + replay WAL to just before the incident (PITR). |
| `SECRET_ENCRYPTION_KEY` lost, Postgres intact | **All PSA credentials must be re-entered** by MSP admins — the `secret_blobs` rows survive but nothing can decrypt them. This is why the key is backed up separately from the database it protects. |
| Full region loss | Rebuild stack from IaC in the DR region; restore Postgres, the encryption key (from its separate offsite location), MinIO and Keycloak. |
| Ransomware | Restore from the most recent *immutable* offsite backup predating the compromise. |

## Targets
- **RTO**: 4 hours (full stack restore + verification).
- **RPO**: 5 minutes for Postgres (WAL), 1 hour for object storage.
- Restore drills are run quarterly and the result recorded in this doc's changelog.

> Status: procedure documented and scripted. A live restore drill requires a running stack and is
> tracked as a production-readiness gate (not executable in the current stack-less environment).
