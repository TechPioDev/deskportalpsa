#!/usr/bin/env bash
#
# Nightly backup for the Desk Portal production stack on the VPS: the database, the uploaded
# attachments, and Keycloak's own database (the sign-in accounts).
#
# Three things, because each is useless without the others:
#   - desk_portal (Postgres): tickets, notes, users, mappings, and the PSA credentials - encrypted
#     rows whose key is SECRET_ENCRYPTION_KEY in .env.prod. That key is deliberately NOT copied
#     here: keep it where the host's other root secrets live, never next to the dump it decrypts.
#   - attachments volume: the files the rows in `ticket_attachments` point at.
#   - Keycloak: it runs on its dev-file H2 database INSIDE the container (no KC_DB, no volume), so
#     recreating that container would lose every account; a copy of the H2 files each night is the
#     only thing standing between a rebuild and "nobody can sign in". The realm definition itself
#     is re-imported from infrastructure/keycloak on start and needs no backup.
#
# Backups live OUTSIDE the git working tree (/var/backups/deskportal) so a pull or a redeploy never
# touches them. Every dump is test-restored into a scratch database before it is counted as good.
#
# Install (on the VPS, as root):
#   cp /opt/deskportal/infrastructure/scripts/backup-vps.sh /usr/local/sbin/deskportal-backup
#   chmod +x /usr/local/sbin/deskportal-backup
#   ( crontab -l 2>/dev/null; echo '45 3 * * * /usr/local/sbin/deskportal-backup >> /var/log/deskportal-backup.log 2>&1' ) | crontab -
#
# Restore the database (stop api + worker first so nothing writes while it is replaced):
#   cd /opt/deskportal
#   F="-f infrastructure/docker/docker-compose.prod.yml -f infrastructure/docker/docker-compose.hostproxy.yml --env-file infrastructure/docker/.env.prod"
#   docker compose $F stop api worker
#   gunzip -c /var/backups/deskportal/desk_portal_YYYY-MM-DD_HHMMSS.dump.gz \
#     | docker exec -i desk-portal-prod-postgres-1 pg_restore -U desk -d desk_portal --clean --if-exists --no-owner
#   docker compose $F start api worker
#
# Restore the attachments from the SAME timestamp:
#   docker compose $F stop api worker
#   docker run --rm -v desk-portal-prod_desk_attachments:/data -v /var/backups/deskportal:/b alpine \
#     sh -c 'rm -rf /data/* && tar -xzf /b/desk-attachments_YYYY-MM-DD_HHMMSS.tar.gz -C /data'
#   docker compose $F start api worker
#
# Restore Keycloak's accounts (only after a container rebuild lost them):
#   docker compose $F stop keycloak
#   docker cp /var/backups/deskportal/keycloak-h2_YYYY-MM-DD_HHMMSS/. desk-portal-prod-keycloak-1:/opt/keycloak/data/h2/
#   docker compose $F start keycloak
set -euo pipefail

APP_DIR="${APP_DIR:-/opt/deskportal}"
BACKUP_DIR="${BACKUP_DIR:-/var/backups/deskportal}"
KEEP_DAYS="${KEEP_DAYS:-14}"
PG="${PG_CONTAINER:-desk-portal-prod-postgres-1}"
KC="${KC_CONTAINER:-desk-portal-prod-keycloak-1}"
ATTACH_VOL="${ATTACH_VOLUME:-desk-portal-prod_desk_attachments}"

set -a
# shellcheck disable=SC1090
source <(grep -E '^POSTGRES_(USER|PASSWORD)=' "$APP_DIR/infrastructure/docker/.env.prod")
set +a
DB=desk_portal

mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"
TS="$(date +%F_%H%M%S)"
STATUS=0
log() { echo "$(date -Is) $*"; }

# ── 1. Database ──────────────────────────────────────────────────────────────
DUMP="$BACKUP_DIR/${DB}_${TS}.dump.gz"
docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$PG" \
  pg_dump -U "$POSTGRES_USER" -d "$DB" -Fc --no-owner | gzip -9 > "$DUMP"
if ! gzip -t "$DUMP" 2>/dev/null || [ ! -s "$DUMP" ]; then
  log "DATABASE BACKUP FAILED (empty or corrupt): $DUMP" >&2
  rm -f "$DUMP"; exit 1
fi

# Prove it restores: a dump that pg_dump wrote but pg_restore cannot read is not a backup. Into a
# scratch database on the same server, then dropped; the live database is never touched.
SCRATCH="${DB}_restorecheck"
docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$PG" psql -U "$POSTGRES_USER" -d postgres -qc "DROP DATABASE IF EXISTS $SCRATCH" >/dev/null
docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$PG" psql -U "$POSTGRES_USER" -d postgres -qc "CREATE DATABASE $SCRATCH" >/dev/null
if gunzip -c "$DUMP" | docker exec -i -e PGPASSWORD="$POSTGRES_PASSWORD" "$PG" pg_restore -U "$POSTGRES_USER" -d "$SCRATCH" --no-owner >/dev/null 2>&1; then
  TICKETS=$(docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$PG" psql -U "$POSTGRES_USER" -d "$SCRATCH" -At -c 'select count(*) from tickets')
  TABLES=$(docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$PG" psql -U "$POSTGRES_USER" -d "$SCRATCH" -At -c "select count(*) from information_schema.tables where table_schema='public'")
  log "database ok: $DUMP ($(du -h "$DUMP" | cut -f1)); restore check: $TABLES tables, $TICKETS tickets"
else
  log "DATABASE BACKUP FAILED: dump written but does not restore: $DUMP" >&2
  STATUS=1
fi
docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$PG" psql -U "$POSTGRES_USER" -d postgres -qc "DROP DATABASE IF EXISTS $SCRATCH" >/dev/null || true

# ── 2. Attachments volume ────────────────────────────────────────────────────
ATT="$BACKUP_DIR/desk-attachments_${TS}.tar.gz"
# BusyBox tar (alpine): no GNU warning flags, and exit 0 is the only success.
if docker run --rm -v "$ATTACH_VOL":/data:ro -v "$BACKUP_DIR":/b alpine \
     tar -czf "/b/$(basename "$ATT")" -C /data . \
   && gzip -t "$ATT" 2>/dev/null && [ -s "$ATT" ]; then
  log "attachments ok: $ATT ($(du -h "$ATT" | cut -f1), $(tar -tzf "$ATT" | grep -cv '/$') files)"
else
  log "ATTACHMENTS BACKUP FAILED: $ATT" >&2
  rm -f "$ATT"; STATUS=1
fi

# ── 3. Keycloak accounts (H2 files) ──────────────────────────────────────────
# Copied while Keycloak runs. H2's MVStore file is append-structured and a copy taken mid-write
# opens at its last completed checkpoint, so this loses at most the final seconds of changes -
# acceptable for a nightly copy of a store that changes only when someone signs up or changes a
# password. Moving Keycloak onto Postgres (KC_DB) is the proper fix; until then this is the copy.
KCDIR="$BACKUP_DIR/keycloak-h2_${TS}"
# docker cp keeps the files' own timestamps (the store dates from August), so the folder is stamped
# now - otherwise the retention sweep below would delete it the moment it was made.
if docker cp "$KC:/opt/keycloak/data/h2/." "$KCDIR" 2>/dev/null && [ -s "$KCDIR/keycloakdb.mv.db" ] && touch "$KCDIR"; then
  log "keycloak ok: $KCDIR ($(du -sh "$KCDIR" | cut -f1))"
else
  log "KEYCLOAK BACKUP FAILED: could not copy /opt/keycloak/data/h2 from $KC" >&2
  rm -rf "$KCDIR"; STATUS=1
fi

# ── Rotate ───────────────────────────────────────────────────────────────────
find "$BACKUP_DIR" -maxdepth 1 -name "${DB}_*.dump.gz" -type f -mtime "+${KEEP_DAYS}" -delete
find "$BACKUP_DIR" -maxdepth 1 -name 'desk-attachments_*.tar.gz' -type f -mtime "+${KEEP_DAYS}" -delete
find "$BACKUP_DIR" -maxdepth 1 -name 'keycloak-h2_*' -type d -mtime "+${KEEP_DAYS}" -exec rm -rf {} +
log "retained: $(ls -1 "$BACKUP_DIR"/${DB}_*.dump.gz 2>/dev/null | wc -l) dumps, $(du -sh "$BACKUP_DIR" | cut -f1) total. Off-site copy: NONE - this host is the only copy."

exit "$STATUS"
