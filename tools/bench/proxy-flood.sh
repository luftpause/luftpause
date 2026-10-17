#!/usr/bin/env bash
set -u
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

if [ $# -eq 0 ]; then
  echo "usage: [DRY_RUN=1] [DUR=180] [REPS='1 2'] [BATCH=48] [CTX=8000] $0 <arm>..." >&2
  echo "  arm: a git revision (optionally prefixed luftpause-) built into luftpause-{scheduler,dataplane,runtime}:<arm>, or 'router' (sgl-router behind a socat port)" >&2
  exit 1
fi

MODEL="RedHatAI/Llama-3.2-1B-Instruct-FP8"
SHAPE="--model $MODEL --tool-ms 1000 --max-tool-calls 3 --context-chars ${CTX:-8000}"
BATCH=${BATCH:-48}
DUR=${DUR:-180}

use_arm() {
  case $1 in
    router)
      run $COMPOSE --profile router up -d sgl-router >/dev/null 2>&1
      run docker rm -f lp-router-port >/dev/null 2>&1
      run docker run -d --rm --name lp-router-port --network luftpause_default -p 30100:30100 alpine/socat TCP-LISTEN:30100,fork,reuseaddr TCP:sgl-router:30100 >/dev/null
      pause 3
      echo "http://localhost:30100/"
      ;;
    *)
      tag_latest "$1"
      run $COMPOSE up -d --force-recreate scheduler dataplane runtime >/dev/null 2>&1
      pause 8
      echo "http://localhost:8080/"
      ;;
  esac
}

run_arm() {
  local arm=$1 rep=$2
  local base
  base=$(use_arm "$arm")
  local out="$OUT_DIR/proxy-$(arm_tag "$arm")-$rep"
  start_load "$out.batch.txt" --agents $BATCH --class batch --duration-s $DUR --base $base $SHAPE
  local b=$!
  start_load "$out.interactive.txt" --agents 4 --class interactive --duration-s $DUR --base $base $SHAPE
  local i=$!
  wait $b $i
  echo "== $arm rep $rep"
  if [ "${DRY_RUN:-}" != 1 ]; then
    echo "-- interactive (4 own-loop workers, closed loop)"; grep -vE '^[0-9]{3}: ' "$out.interactive.txt"
    echo "-- batch ($BATCH own-loop workers, closed loop)"; grep -vE '^[0-9]{3}: ' "$out.batch.txt"
  fi
}

prepare "$@"
for rep in ${REPS:-1 2}; do
  for arm in "$@"; do
    run_arm "$arm" "$rep"
  done
done
run docker rm -f lp-router-port >/dev/null 2>&1
echo DONE
