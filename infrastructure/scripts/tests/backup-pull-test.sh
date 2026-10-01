#!/usr/bin/env bash
# End-to-end test of the backup pull kit with two throwaway containers. Removes them after.
set -u
S="${KIT_DIR:-$(cd "$(dirname "$0")/.." && pwd)}"
NET=pulltest-net
cleanup() { docker rm -f pt-vps pt-puller pt-stranger >/dev/null 2>&1; docker network rm $NET >/dev/null 2>&1; }
cleanup
docker network create $NET >/dev/null
pass=0; fail=0
check() { if eval "$2" >/dev/null 2>&1; then echo "PASS  $1"; pass=$((pass+1)); else echo "FAIL  $1"; fail=$((fail+1)); fi; }

for c in pt-vps pt-puller pt-stranger; do
  docker run -d --name $c --network $NET -v $S:/kit:ro ubuntu:24.04 sleep 3600 >/dev/null
done
docker exec pt-vps sh -c 'apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq openssh-server rsync acl gzip postgresql-client >/dev/null 2>&1; mkdir -p /run/sshd /var/backups/deskportal && chmod 700 /var/backups/deskportal'
for c in pt-puller pt-stranger; do
  docker exec $c sh -c 'apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq openssh-client rsync gzip >/dev/null 2>&1; mkdir -p /root/.ssh && ssh-keygen -q -t ed25519 -N "" -f /root/.ssh/deskportal_pull'
done
# Same key on the stranger: proves the IP pin, not just the key.
docker exec pt-puller cat /root/.ssh/deskportal_pull | docker exec -i pt-stranger sh -c 'cat > /root/.ssh/deskportal_pull'
docker exec pt-puller cat /root/.ssh/deskportal_pull.pub | docker exec -i pt-stranger sh -c 'cat > /root/.ssh/deskportal_pull.pub'

# Backups as the nightly job writes them (root, umask 022), for today and yesterday.
T=$(date -u +%F); Y=$(date -u -d yesterday +%F)
docker exec pt-vps sh -c "cd /var/backups/deskportal && for d in $Y $T; do echo db-\$d | gzip > desk_portal_\${d}_034501.dump.gz; echo kc | gzip > keycloak_\${d}_034501.dump.gz; echo files | gzip > desk-attachments_\${d}_034501.tar.gz; done; echo secret > /etc/not-a-backup"
docker exec pt-vps /usr/sbin/sshd

PUB=$(docker exec pt-puller cat /root/.ssh/deskportal_pull.pub)
PULLER_IP=$(docker inspect -f "{{(index .NetworkSettings.Networks \"$NET\").IPAddress}}" pt-puller)
check "setup refuses a non-key argument" "! docker exec pt-vps bash /kit/backup-pull-vps-setup.sh not-a-key"
check "setup runs with the key pinned to the puller's IP" "docker exec pt-vps bash /kit/backup-pull-vps-setup.sh '$PUB' $PULLER_IP"
check "re-running setup keeps exactly one key" "docker exec pt-vps bash /kit/backup-pull-vps-setup.sh '$PUB' $PULLER_IP && [ \"\$(docker exec pt-vps sh -c 'wc -l < /home/deskpull/.ssh/authorized_keys')\" = 1 ]"

RUN="docker exec -e VPS_HOST=pt-vps pt-puller bash /kit/backup-pull.sh"
check "first pull copies all 6 backups and verifies them" "$RUN | tee /tmp/run1.log && grep -q '6 new file' /tmp/run1.log && [ \$(docker exec pt-puller sh -c 'ls /root/deskportal-backups/*.gz | wc -l') = 6 ]"
check "second pull copies nothing new" "$RUN | grep -q '0 new file'"
check "a backup written later on the VPS is readable and pulled (default ACL)" "docker exec pt-vps sh -c 'echo new | gzip > /var/backups/deskportal/desk_portal_${T}_150000.dump.gz' && $RUN | grep -q '1 new file'"
check "a damaged backup is caught and set aside" "docker exec pt-vps sh -c 'echo notgzip > /var/backups/deskportal/keycloak_${T}_160000.dump.gz' && ! $RUN && docker exec pt-puller ls /root/deskportal-backups/keycloak_${T}_160000.dump.gz.damaged"
SSH="ssh -i /root/.ssh/deskportal_pull -o BatchMode=yes -o StrictHostKeyChecking=accept-new"
check "the key cannot open a shell or run a command" "! docker exec pt-puller $SSH deskpull@pt-vps id | grep -q uid"
check "the key cannot write to the VPS" "docker exec pt-puller sh -c 'echo x > /tmp/evil.gz' && ! docker exec pt-puller rsync -e '$SSH' /tmp/evil.gz deskpull@pt-vps:/evil.gz && ! docker exec pt-vps test -e /var/backups/deskportal/evil.gz"
check "the key cannot delete on the VPS" "! docker exec pt-puller rsync -r --delete -e '$SSH' /root/.ssh/ deskpull@pt-vps:/ ; docker exec pt-vps test -e /var/backups/deskportal/desk_portal_${T}_034501.dump.gz"
check "the key cannot read outside the backup folder" "! docker exec pt-puller rsync -e '$SSH' deskpull@pt-vps:/../../../etc/not-a-backup /tmp/x"
check "the same key from another machine is refused (IP pin)" "! docker exec -e VPS_HOST=pt-vps pt-stranger bash /kit/backup-pull.sh"
check "the backup folder stays root's; its group and everyone else still get nothing" "docker exec pt-vps sh -c 'getfacl -p /var/backups/deskportal' | tee /tmp/acl.txt && grep -q '^# owner: root' /tmp/acl.txt && grep -q '^group::---' /tmp/acl.txt && grep -q '^other::---' /tmp/acl.txt && grep -q '^user:deskpull:r-x' /tmp/acl.txt"
check "the deskpull account has no usable password" "docker exec pt-vps sh -c 'passwd -S deskpull | grep -q \" L \"'"

echo "---- $pass passed, $fail failed"
cleanup
