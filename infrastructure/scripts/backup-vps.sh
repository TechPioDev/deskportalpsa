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
#   - Keycloak: its accounts, clients and signing keys, in the `keycloak` database on the same
#     Postgres (KC_DB). Older installations that still run Keycloak on its in-container H2 store get
#     a copy of those files instead.
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
# Restore Keycloak (accounts, clients, keys):
#   docker compose $F stop keycloak
#   gunzip -c /var/backups/deskportal/keycloak_YYYY-MM-DD_HHMMSS.dump.gz #     | docker exec -i desk-portal-prod-postgres-1 pg_restore -U desk -d keycloak --clean --if-exists --no-owner
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

# ── 3. Keycloak accounts ─────────────────────────────────────────────────────
# Keycloak now keeps its accounts in the `keycloak` database on the same Postgres, so it is dumped
# and restore-checked the same way. The H2 copy below stays for an installation that has not been
# moved yet, and is skipped quietly once the in-container store is gone.
KCDUMP="$BACKUP_DIR/keycloak_${TS}.dump.gz"
if docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$PG" psql -U "$POSTGRES_USER" -d postgres -At -c "select 1 from pg_database where datname='keycloak'" | grep -q 1; then
  docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$PG" pg_dump -U "$POSTGRES_USER" -d keycloak -Fc --no-owner | gzip -9 > "$KCDUMP"
  if gzip -t "$KCDUMP" 2>/dev/null && [ -s "$KCDUMP" ]; then
    log "keycloak db ok: $KCDUMP ($(du -h "$KCDUMP" | cut -f1))"
  else
    log "KEYCLOAK DB BACKUP FAILED: $KCDUMP" >&2; rm -f "$KCDUMP"; STATUS=1
  fi
fi

# ── 3b. Keycloak accounts (legacy H2 files) ──────────────────────────────────
# Copied while Keycloak runs. H2's MVStore file is append-structured and a copy taken mid-write
# opens at its last completed checkpoint, so this loses at most the final seconds of changes -
# acceptable for a nightly copy of a store that changes only when someone signs up or changes a
# password. Moving Keycloak onto Postgres (KC_DB) is the proper fix; until then this is the copy.
KCDIR="$BACKUP_DIR/keycloak-h2_${TS}"
# docker cp keeps the files' own timestamps (the store dates from August), so the folder is stamped
# now - otherwise the retention sweep below would delete it the moment it was made.
if docker exec "$KC" test -s /opt/keycloak/data/h2/keycloakdb.mv.db 2>/dev/null; then
  if docker cp "$KC:/opt/keycloak/data/h2/." "$KCDIR" 2>/dev/null && [ -s "$KCDIR/keycloakdb.mv.db" ] && touch "$KCDIR"; then
    log "keycloak h2 ok: $KCDIR ($(du -sh "$KCDIR" | cut -f1))"
  else
    log "KEYCLOAK H2 BACKUP FAILED: could not copy /opt/keycloak/data/h2 from $KC" >&2
    rm -rf "$KCDIR"; STATUS=1
  fi
fi

# ── 4. Off-site copy (optional) ──────────────────────────────────────────────
# Everything above lives on this one disk; a lost VPS loses every copy. When $OFFSITE_ENV exists,
# this run's files are encrypted HERE (AES-256, key derived from OFFSITE_PASSPHRASE) and uploaded to
# any S3-compatible bucket (Backblaze B2, Wasabi, AWS S3, Cloudflare R2...), then their sizes are
# checked on the far side. The bucket never sees plaintext, so its credentials alone expose nothing.
#
# KEEP OFFSITE_PASSPHRASE SOMEWHERE ELSE TOO (a password manager). Without it the off-site copies
# cannot be decrypted, and the one place it is stored is the server they exist to replace.
#
# $OFFSITE_ENV (root-only, chmod 600), see infrastructure/scripts/offsite.env.example:
#   OFFSITE_S3_ENDPOINT, OFFSITE_S3_REGION, OFFSITE_S3_BUCKET, OFFSITE_S3_PREFIX,
#   OFFSITE_S3_ACCESS_KEY_ID, OFFSITE_S3_SECRET_ACCESS_KEY, OFFSITE_PASSPHRASE, OFFSITE_KEEP_DAYS
#
# Restore one file:
#   (download it from the bucket, then)
#   openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -pass env:OFFSITE_PASSPHRASE \
#     -in desk_portal_YYYY-MM-DD_HHMMSS.dump.gz.enc -out desk_portal_YYYY-MM-DD_HHMMSS.dump.gz
#   and continue with the restore steps at the top of this file.
OFFSITE_ENV="${OFFSITE_ENV:-/etc/deskportal/offsite.env}"
RCLONE_IMAGE="${RCLONE_IMAGE:-rclone/rclone:1.69}"
if [ -f "$OFFSITE_ENV" ]; then
  if [ "$(stat -c %a "$OFFSITE_ENV")" != "600" ]; then
    log "OFF-SITE SKIPPED: $OFFSITE_ENV must be chmod 600 (it holds the bucket key and the passphrase)" >&2; STATUS=1
  else
    # shellcheck disable=SC1090
    set -a; . "$OFFSITE_ENV"; set +a
    OFFSITE_S3_PREFIX="${OFFSITE_S3_PREFIX:-deskportal}"
    OFFSITE_KEEP_DAYS="${OFFSITE_KEEP_DAYS:-30}"
    missing=""
    for v in OFFSITE_S3_ENDPOINT OFFSITE_S3_BUCKET OFFSITE_S3_ACCESS_KEY_ID OFFSITE_S3_SECRET_ACCESS_KEY OFFSITE_PASSPHRASE; do
      [ -n "${!v:-}" ] || missing="$missing $v"
    done
    if [ -n "$missing" ]; then
      log "OFF-SITE SKIPPED: $OFFSITE_ENV is missing:$missing" >&2; STATUS=1
    elif [ "${#OFFSITE_PASSPHRASE}" -lt 20 ]; then
      log "OFF-SITE SKIPPED: OFFSITE_PASSPHRASE must be at least 20 characters" >&2; STATUS=1
    else
      STAGE="$(mktemp -d "$BACKUP_DIR/.offsite.XXXXXX")"
      for f in "$DUMP" "$ATT" "$KCDUMP"; do
        [ -s "$f" ] || continue
        openssl enc -aes-256-cbc -pbkdf2 -iter 200000 -salt -pass env:OFFSITE_PASSPHRASE \
          -in "$f" -out "$STAGE/$(basename "$f").enc"
      done
      # The bucket is addressed through rclone's on-the-fly remote (environment only, no config
      # file on disk). OFFSITE_DOCKER_NETWORK exists for the self-test against a local S3 server.
      rc() {
        docker run --rm ${OFFSITE_DOCKER_NETWORK:+--network "$OFFSITE_DOCKER_NETWORK"} -v "$STAGE":/stage:ro \
          -e RCLONE_CONFIG_OFF_TYPE=s3 -e RCLONE_CONFIG_OFF_PROVIDER=Other \
          -e RCLONE_CONFIG_OFF_ENDPOINT="$OFFSITE_S3_ENDPOINT" -e RCLONE_CONFIG_OFF_REGION="${OFFSITE_S3_REGION:-}" \
          -e RCLONE_CONFIG_OFF_ACCESS_KEY_ID="$OFFSITE_S3_ACCESS_KEY_ID" -e RCLONE_CONFIG_OFF_SECRET_ACCESS_KEY="$OFFSITE_S3_SECRET_ACCESS_KEY" \
          -e RCLONE_CONFIG_OFF_NO_CHECK_BUCKET=true \
          "$RCLONE_IMAGE" "$@"
      }
      DEST="off:$OFFSITE_S3_BUCKET/$OFFSITE_S3_PREFIX"
      COUNT=$(find "$STAGE" -type f | wc -l)
      if rc copy /stage "$DEST" --s3-no-check-bucket -q && rc check /stage "$DEST" --size-only --one-way -q; then
        log "off-site ok: $COUNT encrypted file(s) to $OFFSITE_S3_BUCKET/$OFFSITE_S3_PREFIX, sizes verified"
        rc delete "$DEST" --min-age "${OFFSITE_KEEP_DAYS}d" --include '*.enc' -q \
          || log "off-site retention sweep failed (uploads are fine; older copies were not removed)" >&2
      else
        log "OFF-SITE UPLOAD FAILED to $OFFSITE_S3_BUCKET/$OFFSITE_S3_PREFIX - local backups are fine, this host is still the only copy" >&2
        STATUS=1
      fi
      rm -rf "$STAGE"
    fi
  fi
fi

# ── Rotate ───────────────────────────────────────────────────────────────────
find "$BACKUP_DIR" -maxdepth 1 -name "${DB}_*.dump.gz" -type f -mtime "+${KEEP_DAYS}" -delete
find "$BACKUP_DIR" -maxdepth 1 -name 'desk-attachments_*.tar.gz' -type f -mtime "+${KEEP_DAYS}" -delete
find "$BACKUP_DIR" -maxdepth 1 -name 'keycloak_*.dump.gz' -type f -mtime "+${KEEP_DAYS}" -delete
find "$BACKUP_DIR" -maxdepth 1 -name 'keycloak-h2_*' -type d -mtime "+${KEEP_DAYS}" -exec rm -rf {} +
if [ -f "$OFFSITE_ENV" ]; then OFFSITE_NOTE="off-site: see above"; else OFFSITE_NOTE="Off-site copy: NONE - this host is the only copy (create $OFFSITE_ENV to enable)"; fi
log "retained: $(ls -1 "$BACKUP_DIR"/${DB}_*.dump.gz 2>/dev/null | wc -l) dumps, $(du -sh "$BACKUP_DIR" | cut -f1) total. $OFFSITE_NOTE."

exit "$STATUS"
