#!/usr/bin/env bash
# Creates the desk-portal technicians in Keycloak.
#
# YOU run this, on the VPS. It asks for the Keycloak admin password, uses it for one login, and
# never writes it anywhere. Temporary passwords are generated ON THIS MACHINE and written to a file
# only you can read - not chosen by, sent to, or visible to anyone else.
#
# Prefer federating to Microsoft 365 over running this: it creates no accounts, distributes no
# passwords, and removes portal access automatically when someone leaves. This exists for the case
# where that is not an option.
#
#   bash create-technicians.sh
#
# Safe to re-run: a user that already exists is skipped, never duplicated and never password-reset.
set -euo pipefail

REALM=desk
CONTAINER=desk-portal-prod-keycloak-1
KC=/opt/keycloak/bin/kcadm.sh
OUT="./technician-passwords-$(date +%Y%m%d-%H%M).txt"

read -rp  "Keycloak admin username: " KC_ADMIN
read -rsp "Keycloak admin password: " KC_PASS; echo

kc() { docker exec -i "$CONTAINER" "$KC" "$@"; }

kc config credentials --server http://localhost:8081 --realm master    --user "$KC_ADMIN" --password "$KC_PASS" >/dev/null
echo "Authenticated. Creating users in realm '$REALM'."

umask 077
: > "$OUT"
echo "# Temporary passwords. Each user must change theirs at first sign-in." >> "$OUT"

created=0; skipped=0
while IFS='|' read -r email first last; do
  [ -z "$email" ] && continue

  existing="$(kc get users -r "$REALM" -q "email=$email" --fields id --format csv --noquotes 2>/dev/null | tr -d '')"
  if [ -n "$existing" ]; then
    echo "  skip    $email (already exists)"; skipped=$((skipped+1)); continue
  fi

  kc create users -r "$REALM"     -s "username=$email" -s "email=$email"     -s "firstName=$first" -s "lastName=$last"     -s enabled=true -s emailVerified=true >/dev/null

  pw="$(LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 16)"
  kc set-password -r "$REALM" --username "$email" --new-password "$pw" --temporary >/dev/null
  printf '%s	%s
' "$email" "$pw" >> "$OUT"

  echo "  created $email"; created=$((created+1))
done <<'USERS'
abhishek@techpio.com|Abhishek|Virdi
akanksha@techpio.com|Akanksha|Maurya
akshay.thakur@techpio.com|Akshay|Thakur
anil@techpio.com|Anil|Modalavalasa
anish@techpio.com|Anish|Pathania
anuj.thakur@techpio.com|Anuj|Thakur
apekshit.thakur@techpio.com|Apekshit|Thakur
auvroneil@techpio.com|Auvroneil|Das
basit@techpio.com|Basit|Lone
bhavya.beri@techpio.com|Bhavya|Beri
bipasha@techpio.com|Bipasha|Shukla
bkumar@techpio.com|Banti|Kumar
chandra@techpio.com|Chandra|Sekhar
dalbeir@techpio.com|Dalbeir|Singh
deepak.bhatt@techpio.com|Deepak|Bhatt
dibyaranjan.patra@techpio.com|Dibyaranjan|Patra
diksha.jagtap@techpio.com|Diksha|Jagtap
gautam.sharma@techpio.com|Gautam|Sharma
gsingh@techpio.com|Gurpyar|Singh
gurpreet@techpio.com|Gurpreet|Singh
gyadav@techpio.com|Gurpreet|Yadav
inderpreet@techpio.com|Inderpreet|Singh
ishant@techpio.com|Ishant|Choudhary
jaspreet@techpio.com|Jaspreet|Singh
kamal@techpio.com|Kamal|Saini
komal.sharma@techpio.com|Komal|Sharma
mudasir.farooq@techpio.com|Mudasir|Farooq
narvada@techpio.com|Narvada|Thakur
rohit.chaudhary@techpio.com|Rohit|Chaudhary
roushan@techpio.com|Roushan|Singh
sarabjit@techpio.com|Sarabjit|Singh
sargam@techpio.com|Sargam|Chouhan
sree@techpio.com|Devi|Sree Vadisala
sudanshu@techpio.com|Sudanshu|Aggarwal
sugirtha@techpio.com|Sugirtha|S P
sukumar@techpio.com|Sukumar|S
sushmita.sharma@techpio.com|Sushmita|Sharma
tanvi.choudhary@techpio.com|Tanvi|Choudhary
vishal.kumar@techpio.com|Vishal|Kumar
yogesh@techpio.com|Yogesh|Mirchandani
USERS

echo
echo "Created $created, skipped $skipped."
echo "Temporary passwords: $OUT  (mode 600 - delete it once distributed)"
echo
echo "The portal already holds a row for each of these addresses. Each person is bound to theirs on"
echo "first sign-in, by verified email - nothing further to do in the portal."
