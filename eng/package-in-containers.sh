#!/usr/bin/env bash
set -euo pipefail

usage() {
  printf '%s\n' \
    'Usage: eng/package-in-containers.sh --stage PATH --output PATH' \
    '       [--runtime docker|podman]'
}

fail() {
  printf 'linuxcloth container package build: %s\n' "$*" >&2
  exit 1
}

stage_path=
output_path=
container_runtime=${CONTAINER_RUNTIME:-}

while (($#)); do
  case "$1" in
    --stage)
      (($# >= 2)) || fail '--stage requires a path'
      stage_path=$2
      shift 2
      ;;
    --output)
      (($# >= 2)) || fail '--output requires a path'
      output_path=$2
      shift 2
      ;;
    --runtime)
      (($# >= 2)) || fail '--runtime requires a value'
      container_runtime=$2
      shift 2
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      fail "unknown argument: $1"
      ;;
  esac
done

[[ -n "$stage_path" ]] || fail '--stage is required'
[[ -n "$output_path" ]] || fail '--output is required'
stage_path=$(realpath "$stage_path")
output_path=$(realpath -m "$output_path")
[[ -d "$stage_path" ]] || fail "staging root does not exist: $stage_path"
install -d -m 0755 "$output_path"

if [[ -z "$container_runtime" ]]; then
  if command -v docker >/dev/null 2>&1; then
    container_runtime=docker
  elif command -v podman >/dev/null 2>&1; then
    container_runtime=podman
  else
    fail 'docker or podman is required'
  fi
fi
case "$container_runtime" in
  docker|podman) ;;
  *) fail 'container runtime must be docker or podman' ;;
esac
command -v "$container_runtime" >/dev/null 2>&1 ||
  fail "container runtime was not found: $container_runtime"

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
debian_image='docker.io/library/debian@sha256:63a496b5d3b99214b39f5ed70eb71a61e590a77979c79cbee4faf991f8c0783e'
fedora_image='docker.io/library/fedora@sha256:6c75d5bf57cb0fa5aa4b92c6a83c86c791644496d9ac230de7711f5b8ec3b898'

# The single-quoted script is intentionally expanded by the container shell.
# shellcheck disable=SC2016
"$container_runtime" run --rm --platform linux/amd64 \
  --volume "$repo_root:/repo:ro" \
  --volume "$stage_path:/stage:ro" \
  --volume "$output_path:/output" \
  "$debian_image" bash -ceu '
    . /etc/os-release
    [[ "$ID" == debian && "$VERSION_ID" == 12 ]]
    apt-get update >/dev/null
    DEBIAN_FRONTEND=noninteractive apt-get install --yes \
      appstream desktop-file-utils dpkg-dev file >/dev/null
    /repo/eng/package-deb.sh --stage /stage --output /output
  '

# The single-quoted script is intentionally expanded by the container shell.
# shellcheck disable=SC2016
"$container_runtime" run --rm --platform linux/amd64 \
  --volume "$repo_root:/repo:ro" \
  --volume "$stage_path:/stage:ro" \
  --volume "$output_path:/output" \
  "$fedora_image" bash -ceu '
    . /etc/os-release
    [[ "$ID" == fedora && "$VERSION_ID" == 44 ]]
    dnf install --assumeyes appstream cpio desktop-file-utils file rpm-build >/dev/null
    /repo/eng/package-rpm.sh --stage /stage --output /output
  '

printf 'linuxcloth target-family packages: %s\n' "$output_path"
