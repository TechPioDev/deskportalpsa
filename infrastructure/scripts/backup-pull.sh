#!/usr/bin/env bash
#
# Runs on the BACKUP SERVER. Copies the Desk Portal's nightly backups off the VPS, checks every new
# file is intact, and keeps them for longer than the VPS does (it keeps 14 days).
#
# The VPS side is set up once with backup-pull-vps-setup.sh, which gives this server's key
# read-only access to the backup folder and nothing else. Nothing on the VPS can reach this server,
# so the copies here survive whatever happens there.
#
# Install (on the backup server, as the user that will own the copies):
#   ssh-keygen -t ed25519 -N "" -f ~/.ssh/deskportal_pull      # send ~/.ssh/deskportal_pull.pub to the VPS admin
#   cp backup-pull.sh ~/bin/ && chmod +x ~/bin/backup-pull.sh
#   ( crontab -l 2>/dev/null; echo '30 5 * * * ~/bin/backup-pull.sh >> ~/deskportal-pull.log 2>&1' ) | crontab -
#   # 05:30 server time: the VPS writes its backup at 03:45 UTC. Adjust for this server's timezone.
#
# Settings (environment, or edit the defaults below):
#   VPS_HOST   the VPS address                       (default srv1830041.hstgr.cloud)
#   PULL_USER  the read-only user on the VPS          (default deskpull)
#   SSH_KEY    this server's private key              (default ~/.ssh/deskportal_pull)
#   DEST       where the copies live                  (default ~/deskportal-backups)
#   KEEP_DAYS  how long copies are kept here          (default 90)
#
# Exit status is non-zero when anything went wrong, so cron mails it (if mail is set up) and a
# monitor can watch the log. A run that copies nothing new when a new backup was due says so.
set -euo pipefail

VPS_HOST="${VPS_HOST:-srv1830041.hstgr.cloud}"
PULL_USER="${PULL_USER:-deskpull}"
SSH_KEY="${SSH_KEY:-$HOME/.ssh/deskportal_pull}"
DEST="${DEST:-$HOME/deskportal-backups}"
KEEP_DAYS="${KEEP_DAYS:-90}"
STATUS=0
log() { echo "$(date -Is) $*"; }

mkdir -p "$DEST"
chmod 700 "$DEST"
BEFORE="$(mktemp)"
AFTER="$(mktemp)"
trap 'rm -f "$BEFORE" "$AFTER"' EXIT
list() { find "$DEST" -maxdepth 1 -type f -name '*.gz' -printf '%f
' | sort; }
list > "$BEFORE"

# Copy only what is not here yet. Never --delete: the VPS pruning its old files must not prune ours.
# accept-new pins the VPS's host key on first contact and refuses a changed one after that.
log "pulling from $PULL_USER@$VPS_HOST"
if ! rsync -a --ignore-existing --partial-dir=.partial --timeout=600       -e "ssh -i $SSH_KEY -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=30"       "$PULL_USER@$VPS_HOST:/" "$DEST/"; then
  log "ERROR: the copy failed (network, key, or the VPS refused it)"
  exit 1
fi
list > "$AFTER"

# Every file that arrived in this run (by name: rsync keeps the VPS's dates): whole and readable?
NEW=0
while IFS= read -r name; do
  [ -n "$name" ] || continue
  NEW=$((NEW + 1))
  f="$DEST/$name"
  if gzip -t "$f" 2>/dev/null; then
    log "ok      $name ($(du -h "$f" | cut -f1))"
  else
    log "ERROR   $name is damaged - moved aside"
    mv "$f" "$f.damaged"
    STATUS=1
  fi
done < <(comm -13 "$BEFORE" "$AFTER")
log "$NEW new file(s)"

# A database backup from today or yesterday (UTC) must be here, or the VPS has stopped making them.
TODAY="$(date -u +%F)"
YESTERDAY="$(date -u -d yesterday +%F 2>/dev/null || date -u -v-1d +%F)"
if ! ls "$DEST"/desk_portal_"$TODAY"_*.dump.gz "$DEST"/desk_portal_"$YESTERDAY"_*.dump.gz >/dev/null 2>&1; then
  log "WARNING: no database backup from today or yesterday - is the VPS's nightly backup running?"
  STATUS=1
fi

# The newest database dump must also open as a Postgres archive, where the tools are installed.
LATEST="$(ls -1t "$DEST"/desk_portal_*.dump.gz 2>/dev/null | head -1 || true)"
if [ -n "$LATEST" ] && command -v pg_restore >/dev/null; then
  if gunzip -c "$LATEST" | pg_restore -l >/dev/null 2>&1; then
    log "ok      newest dump lists as a valid Postgres archive: $(basename "$LATEST")"
  else
    log "ERROR   newest dump does not read as a Postgres archive: $(basename "$LATEST")"
    STATUS=1
  fi
fi

# Our own retention, independent of the VPS's: by the VPS's own file date, which rsync kept.
OLD="$(find "$DEST" -maxdepth 1 -type f -name '*.gz' -mtime +"$KEEP_DAYS" | wc -l)"
find "$DEST" -maxdepth 1 -type f -name '*.gz' -mtime +"$KEEP_DAYS" -delete
log "done: $(find "$DEST" -maxdepth 1 -type f -name '*.gz' | wc -l) files kept ($(du -sh "$DEST" | cut -f1)), $OLD older than $KEEP_DAYS days removed, status $STATUS"
exit "$STATUS"
