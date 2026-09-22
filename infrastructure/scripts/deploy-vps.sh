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
#   web    - a standby container from the new image starts on 127.0.0.1:3101 and nginx is
#            pointed at it (graceful reload: open connections finish on the old workers). The
#            main container is recreated on 3100, and once it answers nginx is pointed back.
#   worker - recreated in place. It serves no requests, and two at once would run each job twice.
#
# Migrations run when the new api starts, while the old api is still serving: keep them
# additive (new columns/tables), as they have been. A destructive migration needs a plain
# stop-and-start deploy instead.
#
# Usage (on the VPS, as root):
#   /opt/deskportal/infrastructure/scripts/deploy-vps.sh              # pull, build, deploy api worker web
#   /opt/deskportal/infrastructure/scripts/deploy-vps.sh --no-pull web
#
# One-time nginx setup (the script refuses to touch web until it is in place):
#   echo 'upstream deskportal_web { include /etc/nginx/deskportal-web-upstream.inc; }' \
#     > /etc/nginx/conf.d/deskportal-upstream.conf
#   echo 'server 127.0.0.1:3100;' > /etc/nginx/deskportal-web-upstream.inc
#   # in the piomanage.com server block: proxy_pass http://deskportal_web;
#   nginx -t && systemctl reload nginx
set -euo pipefail

APP_DIR="${APP_DIR:-/opt/deskportal}"
UPSTREAM_INC="${UPSTREAM_INC:-/etc/nginx/deskportal-web-upstream.inc}"
WEB_PORT="${WEB_PORT:-3100}"
STANDBY_PORT="${STANDBY_PORT:-3101}"
STANDBY_NAME="desk-portal-prod-web-standby"
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

point_nginx_at() {
  local port="$1"
  echo "server 127.0.0.1:${port};" > "$UPSTREAM_INC.new"
  mv "$UPSTREAM_INC.new" "$UPSTREAM_INC"
  nginx -t -q && nginx -s reload
  log "nginx -> 127.0.0.1:${port}"
}

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
  if ! grep -q "127.0.0.1:" "$UPSTREAM_INC" 2>/dev/null; then
    log "web: $UPSTREAM_INC is missing - do the one-time nginx setup in this script's header first" >&2
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
  point_nginx_at "$STANDBY_PORT"
  sleep 3

  log "web: recreating the main container on ${WEB_PORT}"
  dc up -d --no-deps web
  if ! wait_http "http://127.0.0.1:${WEB_PORT}/login" 120; then
    log "web: main container did not answer; LEAVING nginx on the standby (${STANDBY_NAME}) - fix and re-run" >&2
    return 1
  fi
  point_nginx_at "$WEB_PORT"
  # Old nginx workers finish their open requests against the standby before it goes.
  sleep 10
  docker stop -t 10 "$STANDBY_NAME" >/dev/null && docker rm "$STANDBY_NAME" >/dev/null
  log "web: done"
}

if (( PULL )); then
  log "pulling"
  git pull --ff-only
fi
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
