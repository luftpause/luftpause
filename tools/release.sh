#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

if [ $# -ne 1 ]; then
  echo "usage: tools/release.sh vX.Y.Z   (DRY_RUN=1 prints the commands only)" >&2
  exit 2
fi

version=$1
registry=${LUFTPAUSE_REGISTRY:-ghcr.io/luftpause}
images=(scheduler dataplane runtime)

if ! [[ $version =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "version must match vX.Y.Z: $version" >&2
  exit 1
fi

if [ -n "$(git status --porcelain)" ]; then
  echo "working tree is dirty" >&2
  exit 1
fi

run() {
  if [ "${DRY_RUN:-}" = 1 ]; then
    printf '%q ' "$@"
    printf '\n'
  else
    "$@"
  fi
}

export LUFTPAUSE_REGISTRY=$registry LUFTPAUSE_VERSION=$version
run docker compose build "${images[@]}"

for image in "${images[@]}"; do
  run docker tag "$registry/$image:$version" "$registry/$image:latest"
  run docker push "$registry/$image:$version"
  run docker push "$registry/$image:latest"
done

run git tag -a "$version" -m "$version"
