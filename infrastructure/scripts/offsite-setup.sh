#!/usr/bin/env bash
#
# Guided setup for the off-site backup copy. Asks for the bucket details one at a time, tests the
# bucket BEFORE saving anything, then writes /etc/deskportal/offsite.env (root-only) for the nightly
# backup (backup-vps.sh) to read. Nothing typed here is echoed, logged or sent anywhere but the bucket.
#
# Why a script and not "edit the file": a hand-edited settings file on the server was tried twice for
# another setting and left empty both times. Answering questions is harder to get wrong, and a file
# that exists with blanks would fail every night's backup rather than simply doing nothing.
#
# Install (on the VPS, as root):
#   cp /opt/deskportal/infrastructure/scripts/offsite-setup.sh /usr/local/sbin/deskportal-offsite-setup
#   chmod +x /usr/local/sbin/deskportal-offsite-setup
# Run:
#   deskportal-offsite-setup
#
# Test hooks (used by the self-test against a local S3 server, never needed in production):
#   OFFSITE_ENV            where to write the settings   (default /etc/deskportal/offsite.env)
#   OFFSITE_DOCKER_NETWORK docker network for rclone     (to reach a local test bucket)
set -euo pipefail

OFFSITE_ENV="${OFFSITE_ENV:-/etc/deskportal/offsite.env}"
RCLONE_IMAGE="${RCLONE_IMAGE:-rclone/rclone:1.69}"

[ "$(id -u)" = 0 ] || { echo "Run this as root."; exit 1; }
command -v docker >/dev/null || { echo "Docker is needed to test the bucket and was not found."; exit 1; }

say() { printf '%s\n' "$*"; }
ask() { # ask VAR "Question" [default]
  local __v __d="${3:-}"
  if [ -n "$__d" ]; then read -r -p "$2 [$__d]: " __v; __v="${__v:-$__d}"; else read -r -p "$2: " __v; fi
  __v="${__v#"${__v%%[![:space:]]*}"}"; __v="${__v%"${__v##*[![:space:]]}"}"
  printf -v "$1" '%s' "$__v"
}
ask_secret() { # ask_secret VAR "Question"
  local __v; read -r -s -p "$2: " __v; echo; printf -v "$1" '%s' "$__v"
}
need() { [ -n "$2" ] || { say "  $1 cannot be blank. Nothing was saved."; exit 1; }; }

say ""
say "Off-site backup setup for piomanage.com"
say "---------------------------------------"
say "Every night's backup will be encrypted here and a copy uploaded to your storage bucket."
say "Create the bucket first, and a key that can write to that ONE bucket only."
if [ -f "$OFFSITE_ENV" ]; then
  say ""
  say "Settings already exist at $OFFSITE_ENV. Continuing replaces them, only if the new bucket test passes."
fi
say ""
say "Where is the bucket?"
say "  1) Backblaze B2    2) Wasabi    3) AWS S3    4) Cloudflare R2    5) Other S3-compatible"
ask PROVIDER "Choose 1-5" "1"

# People paste the whole Endpoint line from the bucket page; keep only the region from it.
region_of() { local r="${1#https://}"; r="${r#http://}"; r="${r#s3.}"; r="${r%%.backblazeb2.com*}"
  r="${r%%.wasabisys.com*}"; r="${r%%.amazonaws.com*}"; printf '%s' "${r%%/*}"; }

case "$PROVIDER" in
  1) ask REGION "B2 region: paste the bucket's Endpoint (e.g. s3.us-west-004.backblazeb2.com)"; need "Region" "$REGION"
     REGION="$(region_of "$REGION")"; say "  Region: $REGION"
     ENDPOINT="https://s3.$REGION.backblazeb2.com" ;;
  2) ask REGION "Wasabi region or endpoint (e.g. eu-central-1)"; need "Region" "$REGION"; REGION="$(region_of "$REGION")"
     ENDPOINT="https://s3.$REGION.wasabisys.com" ;;
  3) ask REGION "AWS region or endpoint (e.g. ap-south-1)"; need "Region" "$REGION"; REGION="$(region_of "$REGION")"
     ENDPOINT="https://s3.$REGION.amazonaws.com" ;;
  4) ask ACCOUNT "Cloudflare account ID (from the R2 overview page)"; need "Account ID" "$ACCOUNT"
     ENDPOINT="https://$ACCOUNT.r2.cloudflarestorage.com"; REGION="auto" ;;
  5) ask ENDPOINT "Full S3 endpoint URL (https://...)"; need "Endpoint" "$ENDPOINT"
     ask REGION "Region (blank if the provider has none)" "" ;;
  *) say "Please choose a number from 1 to 5. Nothing was saved."; exit 1 ;;
esac

ask BUCKET "Bucket name"; need "Bucket name" "$BUCKET"
ask PREFIX "Folder inside the bucket" "deskportal"
ask KEY_ID "Access key ID (keyID on Backblaze)"; need "Access key ID" "$KEY_ID"
ask_secret SECRET "Secret access key (applicationKey on Backblaze; typing is hidden)"; need "Secret access key" "$SECRET"
ask KEEP "Keep copies for how many days" "30"
case "$KEEP" in ''|*[!0-9]*) say "  Days must be a number. Nothing was saved."; exit 1 ;; esac

rc() { # rclone against the bucket, configured from the environment only (no config file on disk).
  # Input is attached only when RC_STDIN=1 (the upload). An attached `docker run -i` otherwise
  # swallows answers the person has not typed yet.
  docker run --rm ${RC_STDIN:+-i} ${OFFSITE_DOCKER_NETWORK:+--network "$OFFSITE_DOCKER_NETWORK"} \
    -e RCLONE_CONFIG_OFF_TYPE=s3 -e RCLONE_CONFIG_OFF_PROVIDER=Other \
    -e RCLONE_CONFIG_OFF_ENDPOINT="$ENDPOINT" -e RCLONE_CONFIG_OFF_REGION="${REGION:-}" \
    -e RCLONE_CONFIG_OFF_ACCESS_KEY_ID="$KEY_ID" -e RCLONE_CONFIG_OFF_SECRET_ACCESS_KEY="$SECRET" \
    -e RCLONE_CONFIG_OFF_NO_CHECK_BUCKET=true \
    "$RCLONE_IMAGE" "$@"
}

say ""
say "Testing the bucket: writing, reading back and deleting one small file..."
PROBE="off:$BUCKET/$PREFIX/.piomanage-setup-test"
WANT="piomanage off-site test $(date -u +%FT%TZ)"
if ! ERR="$(printf '%s' "$WANT" | RC_STDIN=1 rc rcat "$PROBE" 2>&1)"; then
  say "  The bucket refused the upload. Nothing was saved. What it said:"
  say "$ERR" | tail -3 | sed 's/^/    /'
  say "  Check the region, the bucket name, and that the key may write to this bucket."
  exit 1
fi
GOT="$(rc cat "$PROBE" </dev/null 2>/dev/null || true)"
rc deletefile "$PROBE" </dev/null >/dev/null 2>&1 || true
if [ "$GOT" != "$WANT" ]; then
  say "  The file went up but did not read back the same. Nothing was saved."
  exit 1
fi
say "  Bucket test passed."

say ""
say "Encryption passphrase. Every file is encrypted with it before it leaves this server."
say "Without it the off-site copies can never be opened, so it must ALSO live in your password manager."
ask GEN "Generate a strong one for you? (y/n)" "y"
if [ "$GEN" = "y" ] || [ "$GEN" = "Y" ]; then
  PASSPHRASE="$(openssl rand -base64 32)"
  say ""
  say "  Your passphrase (shown once, not stored anywhere but this server):"
  say ""
  say "      $PASSPHRASE"
  say ""
  while true; do
    ask SAVED "Type 'saved' once it is in your password manager"
    [ "$SAVED" = "saved" ] && break
  done
  clear 2>/dev/null || true
else
  ask_secret PASSPHRASE "Passphrase, at least 20 characters (typing is hidden)"
  ask_secret AGAIN "Same passphrase again"
  [ "$PASSPHRASE" = "$AGAIN" ] || { say "  The two did not match. Nothing was saved."; exit 1; }
  [ "${#PASSPHRASE}" -ge 20 ] || { say "  It must be at least 20 characters. Nothing was saved."; exit 1; }
fi

mkdir -p "$(dirname "$OFFSITE_ENV")"
chmod 700 "$(dirname "$OFFSITE_ENV")"
TMP="$(mktemp "$(dirname "$OFFSITE_ENV")/.offsite.XXXXXX")"
chmod 600 "$TMP"
q() { printf "'%s'" "${1//\'/\'\\\'\'}"; } # single-quote a value so the backup can source it safely
{
  echo "# Written by deskportal-offsite-setup on $(date -u +%F). Re-run that command to change it."
  echo "OFFSITE_S3_ENDPOINT=$(q "$ENDPOINT")"
  echo "OFFSITE_S3_REGION=$(q "${REGION:-}")"
  echo "OFFSITE_S3_BUCKET=$(q "$BUCKET")"
  echo "OFFSITE_S3_PREFIX=$(q "$PREFIX")"
  echo "OFFSITE_S3_ACCESS_KEY_ID=$(q "$KEY_ID")"
  echo "OFFSITE_S3_SECRET_ACCESS_KEY=$(q "$SECRET")"
  echo "OFFSITE_PASSPHRASE=$(q "$PASSPHRASE")"
  echo "OFFSITE_KEEP_DAYS=$(q "$KEEP")"
} > "$TMP"
mv -f "$TMP" "$OFFSITE_ENV"
say ""
say "Saved to $OFFSITE_ENV (readable by root only)."
say "The nightly backup at 03:45 UTC will now send an encrypted copy to $BUCKET/$PREFIX."
say ""
say "Run a backup now to prove it end to end? It takes about a minute. Look for \"off-site ok\"."
ask NOW "Run now? (y/n)" "y"
if [ "$NOW" = "y" ] || [ "$NOW" = "Y" ]; then
  if command -v deskportal-backup >/dev/null; then
    deskportal-backup 2>&1 | tee -a /var/log/deskportal-backup.log | grep -E "off-site|OFF-SITE|retained" || true
  else
    say "  deskportal-backup is not installed on this machine; the nightly run will pick the settings up."
  fi
fi
