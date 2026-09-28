#!/usr/bin/env bash
# Disaster-recovery drill for the Desk Portal, run against the real backups on the real host.
#
# Restores last night's set into SCRATCH databases and a scratch directory, counts what came back,
# compares it against what is live, and then removes everything it made. Production is read only
# here: nothing is stopped, replaced or written to.
set -euo pipefail

APP_DIR=/opt/deskportal
BACKUP_DIR=/var/backups/deskportal
PG=desk-portal-prod-postgres-1
ATTACH_VOL=desk-portal-prod_desk_attachments
DRILL_DB=desk_portal_drdrill
DRILL_KC=keycloak_drdrill
SCRATCH=$(mktemp -d /tmp/dr-drill.XXXXXX)

set -a; . <(grep -E '^POSTGRES_(USER|PASSWORD)=' "$APP_DIR/infrastructure/docker/.env.prod"); set +a
psql() { docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$PG" psql -U "$POSTGRES_USER" "$@"; }
now() { date +%s; }
say() { echo "$(date -Is) $*"; }

DUMP=$(ls -1t "$BACKUP_DIR"/desk_portal_*.dump.gz | head -1)
KCDUMP=$(ls -1t "$BACKUP_DIR"/keycloak_*.dump.gz | head -1)
ATT=$(ls -1t "$BACKUP_DIR"/desk-attachments_*.tar.gz | head -1)
say "set under test: $(basename "$DUMP"), $(basename "$KCDUMP"), $(basename "$ATT")"

cleanup() {
  psql -d postgres -qc "DROP DATABASE IF EXISTS $DRILL_DB" >/dev/null 2>&1 || true
  psql -d postgres -qc "DROP DATABASE IF EXISTS $DRILL_KC" >/dev/null 2>&1 || true
  rm -rf "$SCRATCH"
}
trap cleanup EXIT

# ── 1. The application database ──────────────────────────────────────────────
t0=$(now)
psql -d postgres -qc "DROP DATABASE IF EXISTS $DRILL_DB" >/dev/null
psql -d postgres -qc "CREATE DATABASE $DRILL_DB" >/dev/null
gunzip -c "$DUMP" | docker exec -i -e PGPASSWORD="$POSTGRES_PASSWORD" "$PG" \
  pg_restore -U "$POSTGRES_USER" -d "$DRILL_DB" --no-owner >/dev/null 2>&1
db_secs=$(( $(now) - t0 ))

count() { psql -d "$1" -At -c "select count(*) from $2" 2>/dev/null || echo "n/a"; }
printf '%-22s %12s %12s\n' "TABLE" "RESTORED" "LIVE"
for t in tickets ticket_notes ticket_attachments ticket_time_entries app_users client_companies \
         field_mappings audit_log boards alert_sources; do
  printf '%-22s %12s %12s\n' "$t" "$(count "$DRILL_DB" "$t")" "$(count desk_portal "$t")"
done
say "database restored in ${db_secs}s"

# A restored row is only proof if it still reads as itself: check one ticket end to end.
psql -d "$DRILL_DB" -At -c \
  "select 'sample ticket: ' || \"Title\" || ' / ' || \"PortalStatus\" from tickets order by \"CreatedAt\" desc limit 1"

# ── 2. The sign-in database ──────────────────────────────────────────────────
t0=$(now)
psql -d postgres -qc "DROP DATABASE IF EXISTS $DRILL_KC" >/dev/null
psql -d postgres -qc "CREATE DATABASE $DRILL_KC" >/dev/null
gunzip -c "$KCDUMP" | docker exec -i -e PGPASSWORD="$POSTGRES_PASSWORD" "$PG" \
  pg_restore -U "$POSTGRES_USER" -d "$DRILL_KC" --no-owner >/dev/null 2>&1
kc_secs=$(( $(now) - t0 ))
printf '%-22s %12s %12s\n' "keycloak user_entity" "$(count "$DRILL_KC" user_entity)" "$(count keycloak user_entity)"
printf '%-22s %12s %12s\n' "keycloak client" "$(count "$DRILL_KC" client)" "$(count keycloak client)"
say "sign-in database restored in ${kc_secs}s"

# ── 3. The attachments ───────────────────────────────────────────────────────
t0=$(now)
tar -xzf "$ATT" -C "$SCRATCH"
att_secs=$(( $(now) - t0 ))
restored_files=$(find "$SCRATCH" -type f | wc -l)
# FILE bytes only: du counts directory entries too, which made a clean restore look short by a
# few kilobytes and invited a hunt for missing data that was never missing.
restored_bytes=$(find "$SCRATCH" -type f -printf "%s
" | awk "{t+=\$1} END {print t+0}")
live=$(docker run --rm -v "$ATTACH_VOL":/data:ro alpine sh -c 'find /data -type f | wc -l; find /data -type f -exec stat -c %s {} + | awk "{t+=\$1} END {print t+0}"')
live_files=$(echo "$live" | head -1); live_bytes=$(echo "$live" | tail -1)
printf '%-22s %12s %12s\n' "attachment files" "$restored_files" "$live_files"
printf '%-22s %12s %12s\n' "attachment bytes" "$restored_bytes" "$live_bytes"

# Bytes identical, not merely counted: one file compared against the live copy by checksum.
sample=$(find "$SCRATCH" -type f | head -1)
if [ -n "$sample" ]; then
  rel=${sample#"$SCRATCH"/}
  a=$(sha256sum "$sample" | cut -d' ' -f1)
  b=$(docker run --rm -v "$ATTACH_VOL":/data:ro alpine sha256sum "/data/$rel" | cut -d' ' -f1)
  [ "$a" = "$b" ] && say "checksum match on $rel" || say "CHECKSUM MISMATCH on $rel"
fi
say "attachments restored in ${att_secs}s"

say "drill complete: database ${db_secs}s, sign-in ${kc_secs}s, attachments ${att_secs}s"
