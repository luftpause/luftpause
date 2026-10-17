exec 3>&2
BENCH_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
ROOT=$(cd "$BENCH_DIR/../.." && pwd)
OUT_DIR="$BENCH_DIR/out"
LG="dotnet $ROOT/tools/loadgen/bin/Release/net10.0/Luftpause.LoadGen.dll"
COMPOSE="docker compose -f $ROOT/compose.yaml -f $ROOT/compose.testbed.yaml"
IMAGES="scheduler dataplane runtime"

run() {
  if [ "${DRY_RUN:-}" = 1 ]; then
    echo "+ $*" >&3
  else
    "$@"
  fi
}

arm_tag() {
  echo "${1//[^A-Za-z0-9_.-]/-}"
}

arm_rev() {
  case $1 in
    router) echo HEAD ;;
    *) echo "${1#luftpause-}" ;;
  esac
}

build_arm() {
  local arm=$1
  local tag rev wt
  tag=$(arm_tag "$arm")
  rev=$(arm_rev "$arm")
  wt="$OUT_DIR/worktrees/$tag"
  run mkdir -p "$OUT_DIR/worktrees"
  run git -C "$ROOT" worktree add --detach "$wt" "$rev"
  run docker compose -f "$wt/compose.yaml" --project-directory "$wt" build $IMAGES
  for i in $IMAGES; do
    run docker tag "luftpause-$i:latest" "luftpause-$i:$tag"
  done
  run git -C "$ROOT" worktree remove --force "$wt"
}

tag_latest() {
  local tag
  tag=$(arm_tag "$1")
  for i in $IMAGES; do
    run docker tag "luftpause-$i:$tag" "luftpause-$i:latest"
  done
}

build_loadgen() {
  run dotnet build "$ROOT/tools/loadgen" -c Release
}

prepare() {
  run mkdir -p "$OUT_DIR"
  build_loadgen
  for arm in "$@"; do
    build_arm "$arm"
  done
}

wait_mcp() {
  [ "${DRY_RUN:-}" = 1 ] && return
  for _ in $(seq 1 60); do
    curl -sf -o /dev/null -X POST localhost:7000/mcp -H 'content-type: application/json' -H 'accept: application/json, text/event-stream' \
      -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"probe","version":"0"}}}' && break
    sleep 1
  done
}

pause() {
  [ "${DRY_RUN:-}" = 1 ] || sleep "$1"
}

start_load() {
  local out=$1
  shift
  if [ "${DRY_RUN:-}" = 1 ]; then
    echo "+ $LG $* > $out" >&3
    sleep 0 &
  else
    $LG "$@" > "$out" 2>&1 &
  fi
}
