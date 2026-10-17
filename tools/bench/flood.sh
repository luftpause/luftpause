#!/usr/bin/env bash
set -u
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

if [ $# -eq 0 ]; then
  echo "usage: [DRY_RUN=1] [DUR=180] [REPS='1 2'] [BATCHN=24] [CTX=2000] [TAG=] $0 <arm>..." >&2
  echo "  arm: a git revision built into luftpause-{scheduler,dataplane,runtime}:<arm>, or 'router' (HEAD images behind sgl-router)" >&2
  exit 1
fi

DUR=${DUR:-180}

use_arm() {
  local arm=$1
  tag_latest "$arm"
  case $arm in
    router)
      DATAPLANE_URL=http://sgl-router:30100/ run $COMPOSE --profile router up -d --force-recreate scheduler dataplane runtime sgl-router >/dev/null 2>&1
      ;;
    *)
      run $COMPOSE up -d --force-recreate scheduler dataplane runtime >/dev/null 2>&1
      ;;
  esac
  wait_mcp
  pause 5
}

run_flood() {
  local arm=$1 rep=$2
  local out="$OUT_DIR/flood${TAG:-}-$(arm_tag "$arm")-$rep"
  start_load "$out.batch.txt" --spawn ${BATCHN:-24} --class batch --duration-s $DUR --context-chars ${CTX:-2000} --max-tool-calls 3
  local b=$!
  start_load "$out.interactive.txt" --spawn 4 --class interactive --duration-s $DUR --context-chars ${CTX:-2000} --max-tool-calls 3
  local i=$!
  wait $b $i
  echo "== $arm rep $rep"
  if [ "${DRY_RUN:-}" != 1 ]; then
    echo "-- interactive"; cat "$out.interactive.txt"
    echo "-- batch"; cat "$out.batch.txt"
  fi
}

prepare "$@"
for rep in ${REPS:-1 2}; do
  for arm in "$@"; do
    use_arm "$arm"
    run_flood "$arm" "$rep"
  done
done
echo DONE
