#!/usr/bin/env bash
# Tests the disk check and the build-cache clean-up in deploy-vps.sh.
#
# Runs the REAL script, for the worker only, with stand-ins for docker, git and df on the PATH, in
# a throwaway directory. Nothing is built, started or removed: the stand-ins write down what they
# were asked to do, and the checks read that. Needs bash and nothing else.
set -u
SCRIPT="$(cd "$(dirname "$0")/.." && pwd)/deploy-vps.sh"
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/bin" "$WORK/app"

cat > "$WORK/bin/docker" <<'STUB'
#!/usr/bin/env bash
echo "docker $*" >> "$STUB_LOG"
case "${1:-} ${2:-}" in
  "info --format") echo "$STUB_ROOT" ;;
  "builder prune")
    [ "${STUB_PRUNE_FAILS:-0}" = 1 ] && exit 1
    # An older Docker does not know the newer name of the flag.
    [ "${STUB_OLD_DOCKER:-0}" = 1 ] && [[ "$*" == *--reserved-space* ]] && { echo "unknown flag: --reserved-space" >&2; exit 125; }
    : > "$STUB_PRUNED" ;;
esac
exit 0
STUB
cat > "$WORK/bin/df" <<'STUB'
#!/usr/bin/env bash
# Free space is one figure until something was pruned and another after.
[ "${STUB_DF_BROKEN:-0}" = 1 ] && { echo "df: cannot read" >&2; exit 1; }
echo "Avail"
if [ -e "$STUB_PRUNED" ]; then echo " ${STUB_FREE_AFTER}G"; else echo " ${STUB_FREE_BEFORE}G"; fi
STUB
cat > "$WORK/bin/git" <<'STUB'
#!/usr/bin/env bash
echo "git $*" >> "$STUB_LOG"
[ "${1:-}" = "rev-parse" ] && echo "abc1234"
exit 0
STUB
chmod +x "$WORK/bin/"*

pass=0; fail=0
check() { if eval "$2" >/dev/null 2>&1; then echo "PASS  $1"; pass=$((pass+1)); else echo "FAIL  $1"; fail=$((fail+1)); fi; }

# One deploy of the worker. Arguments are NAME=value settings for it. Leaves $OUT, $LOG and $RC.
deploy() {
  export STUB_LOG="$WORK/calls.log" STUB_PRUNED="$WORK/pruned" STUB_ROOT="$WORK/app"
  : > "$STUB_LOG"; rm -f "$STUB_PRUNED"
  OUT=$(env PATH="$WORK/bin:$PATH" APP_DIR="$WORK/app" STUB_FREE_BEFORE=40 STUB_FREE_AFTER=40 "$@" bash "$SCRIPT" --no-pull worker 2>&1)
  RC=$?
  LOG=$(cat "$STUB_LOG")
}
called() { grep -qE -- "$1" <<<"$LOG"; }
said() { grep -qE -- "$1" <<<"$OUT"; }
# Line numbers of the first call matching each pattern: "a before b".
before() { [ "$(grep -nE -- "$1" <<<"$LOG" | head -1 | cut -d: -f1)" -lt "$(grep -nE -- "$2" <<<"$LOG" | head -1 | cut -d: -f1)" ]; }

deploy STUB_FREE_AFTER=71
check "with room to spare the build and the deploy go ahead" "[ $RC = 0 ] && called 'compose .* build worker' && called 'compose .* up -d --no-deps worker' && said 'deployed: worker at abc1234'"
check "the build cache is cut back to its newest 8 GB, after the deploy and not before" "called 'builder prune -f -a --reserved-space 8GB' && before 'up -d --no-deps worker' 'builder prune'"
check "it says how much was freed" "said 'cut back to the newest 8G; 40G free before, 71G free now'"
check "only the build cache is ever pruned: no image, container, volume or system prune" "! called 'docker (system|image|volume|container|network) prune'"

deploy CACHE_KEEP_GB=20
check "CACHE_KEEP_GB sets how much is kept" "called 'builder prune -f -a --reserved-space 20GB' && ! called '8GB'"

deploy CACHE_KEEP_GB=off
check "CACHE_KEEP_GB=off deploys and removes nothing" "[ $RC = 0 ] && said 'deployed: worker' && ! called 'builder prune'"

deploy STUB_OLD_DOCKER=1 STUB_FREE_AFTER=60
check "an older Docker is asked by the flag's older name" "[ $RC = 0 ] && called 'builder prune -f -a --keep-storage 8GB' && said 'cut back to the newest 8G; 40G free before, 60G free now'"

deploy STUB_FREE_BEFORE=3 STUB_FREE_AFTER=30
check "a nearly full disk is cleared BEFORE building, and the deploy then goes ahead" "[ $RC = 0 ] && called 'builder prune -f -a$' && before 'builder prune -f -a$' 'build worker' && said 'only 3G free' && said 'all unused build leftovers removed' && said 'deployed: worker'"

deploy STUB_FREE_BEFORE=3 STUB_FREE_AFTER=4
check "a disk that stays nearly full refuses the build and changes nothing" "[ $RC != 0 ] && ! called 'build worker' && ! called 'up -d' && said 'still only 4G free. NOT building' && ! said 'deployed:'"

deploy STUB_FREE_BEFORE=3 STUB_FREE_AFTER=30 MIN_FREE_GB=2
check "MIN_FREE_GB sets what counts as nearly full" "[ $RC = 0 ] && ! called 'builder prune -f -a$' && called 'build worker'"

deploy STUB_PRUNE_FAILS=1
check "a clean-up that fails does not fail a deploy that worked" "[ $RC = 0 ] && said 'deployed: worker' && said 'could not be removed; the deploy itself is not affected'"

deploy STUB_DF_BROKEN=1
check "free space that cannot be read does not hold a deploy up" "[ $RC = 0 ] && called 'build worker' && said 'deployed: worker'"

echo "$pass passed, $fail failed"
[ "$fail" = 0 ]
