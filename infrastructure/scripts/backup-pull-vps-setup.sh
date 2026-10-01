#!/usr/bin/env bash
#
# Lets ONE backup server copy the nightly backups off this VPS - and nothing else.
#
# The backup server PULLS. This machine holds no login for it, so whoever takes over the VPS cannot
# reach, overwrite or delete the copies kept there; that is the whole point of a second copy.
#
# What it creates:
#   - a user `deskpull` with no password, which cannot open a shell, forward ports or run a command:
#     its one key is pinned to rrsync in READ-ONLY mode, rooted at the backup folder;
#   - read access for that user to the backup folder (ACLs; the folder stays root's, mode 700),
#     including every backup written from now on.
#
# Run on the VPS as root, with the backup server's PUBLIC key (a public key is not a secret):
#   bash backup-pull-vps-setup.sh "ssh-ed25519 AAAA... backup@server" [backup-server-ip]
#
# Give the IP whenever the backup server has a fixed one: the key then only works from there.
# Safe to re-run: it replaces the key, never adds a second one. Remove it all with:
#   userdel -r deskpull && setfacl -R -x u:deskpull /var/backups/deskportal && setfacl -x d:u:deskpull /var/backups/deskportal
set -euo pipefail

PUBKEY="${1:-}"
FROM_IP="${2:-}"
BACKUP_DIR="${BACKUP_DIR:-/var/backups/deskportal}"
PULL_USER="${PULL_USER:-deskpull}"

[ "$(id -u)" -eq 0 ] || { echo "Run as root."; exit 1; }
case "$PUBKEY" in
  ssh-ed25519\ *|ssh-rsa\ *|ecdsa-sha2-*) ;;
  *) echo "Pass the backup server's PUBLIC key (the one line in its id_ed25519.pub)."; exit 1 ;;
esac
if [ -n "$FROM_IP" ] && ! printf '%s' "$FROM_IP" | grep -Eq '^[0-9a-fA-F:.]+(/[0-9]{1,3})?$'; then
  echo "That does not look like an IP address: $FROM_IP"; exit 1
fi
[ -d "$BACKUP_DIR" ] || { echo "No backups at $BACKUP_DIR yet - install the nightly backup first."; exit 1; }

# rrsync ships with rsync; older Ubuntu keeps it gzipped under the docs.
RRSYNC="$(command -v rrsync || true)"
if [ -z "$RRSYNC" ]; then
  if [ -f /usr/share/doc/rsync/scripts/rrsync.gz ]; then
    gunzip -c /usr/share/doc/rsync/scripts/rrsync.gz > /usr/local/bin/rrsync && chmod 755 /usr/local/bin/rrsync
  elif [ -f /usr/share/doc/rsync/scripts/rrsync ]; then
    install -m 755 /usr/share/doc/rsync/scripts/rrsync /usr/local/bin/rrsync
  else
    echo "rrsync not found. Install rsync (apt-get install rsync) and run this again."; exit 1
  fi
  RRSYNC=/usr/local/bin/rrsync
fi
command -v setfacl >/dev/null || { echo "setfacl not found. Install acl (apt-get install acl) and run this again."; exit 1; }

if ! id "$PULL_USER" >/dev/null 2>&1; then
  # A real shell is needed for sshd to run the forced command; the key's restrictions are what
  # stop it being used as one. No password: the account cannot be logged into any other way.
  useradd --create-home --shell /bin/sh "$PULL_USER"
  passwd -l "$PULL_USER" >/dev/null
  echo "Created user $PULL_USER."
fi

HOME_DIR="$(getent passwd "$PULL_USER" | cut -d: -f6)"
install -d -m 700 -o "$PULL_USER" -g "$PULL_USER" "$HOME_DIR/.ssh"
OPTS="command=\"$RRSYNC -ro $BACKUP_DIR/\",restrict"
[ -n "$FROM_IP" ] && OPTS="from=\"$FROM_IP\",$OPTS"
# One key, replaced on every run: a second run must not leave the old key working.
printf '%s %s\n' "$OPTS" "$PUBKEY" > "$HOME_DIR/.ssh/authorized_keys"
chown "$PULL_USER:$PULL_USER" "$HOME_DIR/.ssh/authorized_keys"
chmod 600 "$HOME_DIR/.ssh/authorized_keys"

# Read (and list) the folder and everything in it; the default entry covers tomorrow's files too.
setfacl -m "u:$PULL_USER:rx" "$BACKUP_DIR"
setfacl -R -m "u:$PULL_USER:rX" "$BACKUP_DIR"
setfacl -d -m "u:$PULL_USER:rX" "$BACKUP_DIR"

echo "Done. $PULL_USER can read $BACKUP_DIR (read-only)${FROM_IP:+, only from $FROM_IP}."
echo "On the backup server, test with:  rsync -n -av $PULL_USER@$(hostname -f 2>/dev/null || hostname):/ /tmp/check/"
