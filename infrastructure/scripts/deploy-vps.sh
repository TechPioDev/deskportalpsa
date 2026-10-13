#!/usr/bin/env bash
#
# Zero-downtime deploy of the Desk Portal stack on the VPS (host-nginx route).
#
# A plain `docker compose up -d <service>` stops the old container before the new one answers,
# and every click in that window got a 502 (five times in September, mid-test). This keeps an
# answering copy of each user-facing service up throughout:
#
#   api    - a second container from the new image starts beside the old one; Docker's service
#            DNS sends requests to both. Once the new one reports /health/ready, the old one is
#            stopped gracefully (in-flight requests finish) and removed. The web proxy retries
#            any request that hits the old container as it goes.
#   web    - nginx always knows two web ports: the main one (3100) and a standby (3101) marked
#            `backup`, so nginx sends a request to 3101 only when 3100 refuses the connection. A
#            deploy starts the standby from the new image, then closes 3100 to NEW connections
#            with a temporary firewall rule (requests already running there finish), recreates
#            the main container, opens 3100 again, and retires the standby the same way.
#            A refused connection means nginx has not sent the request yet, so it passes it to
#            the other port - safe even for a form submission. nginx is never reloaded, so
#            nobody's open browser connection is closed under them. (The previous version
#            reloaded nginx twice per deploy; each reload closes idle keep-alive connections,
#            and a request sent at that instant was dropped: 2 per deploy on 29 Sep.)
#   worker - recreated in place. It serves no requests, and two at once would run each job twice.
#
# Migrations run when the new api starts, while the old api is still serving: keep them
# additive (new columns/tables), as they have been. A destructive migration needs a plain
# stop-and-start deploy instead.
#
# Disk. Every build leaves layers behind in Docker's build cache, and nothing ever removed them:
# by 13 October 2026 they were 38 GB of a 96 GB disk that was 79% full, growing with each deploy.
# A disk that fills during a build stops the database, which is an outage of everything on this
# server. So: before building, a deploy checks there is room and refuses if there is not; after a
# deploy that worked, the build cache is cut back to its most recently used CACHE_KEEP_GB (8).
# By size and not by age, because age does not bound it: of those 38 GB, 32 were under three days
# old (ten deploys in three days), so "older than 72h" would have freed under 2 GB. The build
# cache belongs to the Docker daemon, not to this project: the other stacks on this server lose
# their older leftovers too, which costs them a slower next build and nothing else. Images,
# containers and volumes are never touched. CACHE_KEEP_GB=off skips the clean-up.
#
# Usage (on the VPS, as root):
#   /opt/deskportal/infrastructure/scripts/deploy-vps.sh              # pull, build, deploy api worker web
#   /opt/deskportal/infrastructure/scripts/deploy-vps.sh --no-pull web
#
# One-time nginx setup (the script refuses to touch web until it is in place):
#   echo 'upstream deskportal_web { include /etc/nginx/deskportal-web-upstream.inc; }' \
#     > /etc/nginx/conf.d/deskportal-upstream.conf
#   printf 'server 127.0.0.1:3100 max_fails=0;\nserver 127.0.0.1:3101 backup max_fails=0;\n' \
#     > /etc/nginx/deskportal-web-upstream.inc
#   # in the piomanage.com server block: proxy_pass http://deskportal_web;
#   nginx -t && systemctl reload nginx
# max_fails=0 keeps nginx from marking a port dead for a while after one refusal: every request
# tries the main port first and falls to the standby only when it is refused, which costs a local
# connection attempt and nothing else.
set -euo pipefail

APP_DIR="${APP_DIR:-/opt/deskportal}"
UPSTREAM_INC="${UPSTREAM_INC:-/etc/nginx/deskportal-web-upstream.inc}"
WEB_PORT="${WEB_PORT:-3100}"
STANDBY_PORT="${STANDBY_PORT:-3101}"
STANDBY_NAME="desk-portal-prod-web-standby"
CACHE_KEEP_GB="${CACHE_KEEP_GB:-8}"
MIN_FREE_GB="${MIN_FREE_GB:-6}"
cd "$APP_DIR"
F=(-f infrastructure/docker/docker-compose.prod.yml -f infrastructure/docker/docker-compose.hostproxy.yml --env-file infrastructure/docker/.env.prod)
dc() { docker compose "${F[@]}" "$@"; }
log() { echo "$(date -Is) $*"; }

PULL=1
if [ "${1:-}" = "--no-pull" ]; then PULL=0; shift; fi
SERVICES=("$@")
[ ${#SERVICES[@]} -eq 0 ] && SERVICES=(api worker web)

# Waits until a URL answers 2xx/3xx; returns non-zero after the timeout.
wait_http() {
  local url="$1" timeout="$2" start=$SECONDS
  until curl -fsS -o /dev/null --max-time 5 "$url"; do
    (( SECONDS - start >= timeout )) && return 1
    sleep 2
  done
}

# Whole gigabytes free where Docker keeps its data. 9999 when it cannot be read: a deploy is not
# held up because a number could not be measured.
free_gb() {
  local root n
  root=$(docker info --format '{{.DockerRootDir}}' 2>/dev/null) || root=""
  [ -d "$root" ] || root=/
  n=$(df -BG --output=avail "$root" 2>/dev/null | tail -1 | tr -dc '0-9') || n=""
  echo "${n:-9999}"
}

# Cuts the build cache back to its most recently used <n> GB, or to nothing with "all". Only
# cache no build is using; never an image, a container or a volume. Never fails a deploy.
# --reserved-space is the flag's name since Docker 28; --keep-storage is the same thing before.
prune_build_cache() {
  local before after keep=()
  before=$(free_gb)
  [ "$1" = "all" ] || keep=(--reserved-space "${1}GB")
  if ! docker builder prune -f -a "${keep[@]}" >/dev/null 2>&1; then
    [ "$1" = "all" ] || keep=(--keep-storage "${1}GB")
    if [ "$1" = "all" ] || ! docker builder prune -f -a "${keep[@]}" >/dev/null 2>&1; then
      log "disk: old build leftovers could not be removed; the deploy itself is not affected"
      return 0
    fi
  fi
  after=$(free_gb)
  if [ "$1" = "all" ]; then log "disk: all unused build leftovers removed; ${before}G free before, ${after}G free now"
  else log "disk: build leftovers cut back to the newest ${1}G; ${before}G free before, ${after}G free now"; fi
}

# Refuses to build on a disk that is nearly full, after first trying to make room.
require_room() {
  local free
  free=$(free_gb)
  (( free >= MIN_FREE_GB )) && return 0
  log "disk: only ${free}G free, less than the ${MIN_FREE_GB}G a build needs; removing build leftovers first"
  prune_build_cache all
  free=$(free_gb)
  (( free >= MIN_FREE_GB )) && return 0
  log "disk: still only ${free}G free. NOT building: a build that fills the disk stops the database." >&2
  log "disk: nothing was changed and the site is still serving. Free some space, then deploy again." >&2
  return 1
}

DRAIN_SECONDS="${DRAIN_SECONDS:-10}"
CLOSED_PORTS=()

# Refuses NEW connections from this host to 127.0.0.1:<port>; connections already open carry on.
# Matched on the ORIGINAL destination, so it holds whether Docker reaches the container through
# its userland proxy or a NAT rule. Scoped to one port on loopback: nothing else on this shared
# server is touched.
drain_rule() {
  echo "-o lo -p tcp --syn -m conntrack --ctstate NEW --ctorigdst 127.0.0.1 --ctorigdstport $1 -m comment --comment deskportal-drain -j REJECT --reject-with tcp-reset"
}
close_port() {
  # shellcheck disable=SC2046
  iptables -C OUTPUT $(drain_rule "$1") 2>/dev/null || iptables -I OUTPUT $(drain_rule "$1")
  CLOSED_PORTS+=("$1")
  log "web: 127.0.0.1:$1 closed to new connections"
}
open_port() {
  # shellcheck disable=SC2046
  while iptables -C OUTPUT $(drain_rule "$1") 2>/dev/null; do iptables -D OUTPUT $(drain_rule "$1"); done
  local keep=() p
  for p in "${CLOSED_PORTS[@]}"; do [ "$p" = "$1" ] || keep+=("$p"); done
  CLOSED_PORTS=("${keep[@]}")
  log "web: 127.0.0.1:$1 open"
}
# Whatever happens, no drain rule outlives the script - unless the failure path below chose to
# keep one on purpose, in which case it said so and emptied the list.
cleanup_ports() { local p; for p in "${CLOSED_PORTS[@]}"; do open_port "$p"; done; }
trap cleanup_ports EXIT

deploy_api() {
  local old new ip
  old=$(dc ps -q api)
  log "api: starting a second container from the new image beside ${old:0:12}"
  dc up -d --no-deps --no-recreate --scale api=2 api
  new=$(dc ps -q api | grep -v -F "$old" | head -1)
  [ -n "$new" ] || { log "api: new container did not start" >&2; return 1; }
  ip=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}} {{end}}' "$new" | awk '{print $1}')
  if ! wait_http "http://${ip}:5080/health/ready" 240; then
    log "api: new container ${new:0:12} never became ready; removing it, the old one keeps serving" >&2
    docker logs --tail 40 "$new" >&2 || true
    docker rm -f "$new" >/dev/null
    dc up -d --no-deps --no-recreate --scale api=1 api >/dev/null 2>&1 || true
    return 1
  fi
  log "api: ${new:0:12} ready at ${ip}; stopping ${old:0:12}"
  docker stop -t 30 "$old" >/dev/null && docker rm "$old" >/dev/null
  dc up -d --no-deps --no-recreate --scale api=1 api >/dev/null
  log "api: done"
}

deploy_worker() {
  log "worker: recreating"
  dc up -d --no-deps worker
}

deploy_web() {
  if ! grep -q "backup" "$UPSTREAM_INC" 2>/dev/null; then
    log "web: $UPSTREAM_INC has no backup port - do the one-time nginx setup in this script's header first" >&2
    return 1
  fi
  docker rm -f "$STANDBY_NAME" >/dev/null 2>&1 || true
  log "web: starting standby from the new image on 127.0.0.1:${STANDBY_PORT}"
  dc run -d --no-deps --name "$STANDBY_NAME" -p "127.0.0.1:${STANDBY_PORT}:3000" web >/dev/null
  if ! wait_http "http://127.0.0.1:${STANDBY_PORT}/login" 120; then
    log "web: standby never answered; nothing was switched" >&2
    docker logs --tail 40 "$STANDBY_NAME" >&2 || true
    docker rm -f "$STANDBY_NAME" >/dev/null
    return 1
  fi

  # From here nginx sends every new request to the standby; requests already on the main
  # container are given time to finish before it goes.
  close_port "$WEB_PORT"
  sleep "$DRAIN_SECONDS"

  log "web: recreating the main container on ${WEB_PORT}"
  dc up -d --no-deps web
  # Checked on the container's own address: its published port is closed to new connections.
  local ip
  ip=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}} {{end}}' "$(dc ps -q web)" | awk '{print $1}')
  if ! wait_http "http://${ip}:3000/login" 120; then
    # Left closed on purpose: opening it would send traffic to a container that does not answer.
    CLOSED_PORTS=()
    log "web: main container did not answer; LEAVING ${WEB_PORT} closed and traffic on the standby (${STANDBY_NAME})." >&2
    log "web: once it is fixed:  iptables -D OUTPUT $(drain_rule "$WEB_PORT")" >&2
    return 1
  fi
  open_port "$WEB_PORT"

  # The standby is retired the same way: no new work, a moment to finish what it has, then gone.
  close_port "$STANDBY_PORT"
  sleep "$DRAIN_SECONDS"
  docker stop -t 10 "$STANDBY_NAME" >/dev/null && docker rm "$STANDBY_NAME" >/dev/null
  open_port "$STANDBY_PORT"
  log "web: done"
}

if (( PULL )); then
  log "pulling"
  git pull --ff-only
fi
require_room
log "building: ${SERVICES[*]}"
dc build "${SERVICES[@]}"

for s in "${SERVICES[@]}"; do
  case "$s" in
    api) deploy_api ;;
    worker) deploy_worker ;;
    web) deploy_web ;;
    *) log "$s: not a zero-downtime service; recreating in place"; dc up -d --no-deps "$s" ;;
  esac
done
log "deployed: ${SERVICES[*]} at $(git rev-parse --short HEAD)"
# Only after a deploy that worked: a failed one may be retried within minutes and wants its layers.
[ "$CACHE_KEEP_GB" = "off" ] || prune_build_cache "$CACHE_KEEP_GB"
