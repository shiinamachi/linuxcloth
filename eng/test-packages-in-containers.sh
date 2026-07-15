#!/usr/bin/env bash
set -euo pipefail

usage() {
  printf '%s\n' \
    'Usage: eng/test-packages-in-containers.sh --packages PATH' \
    '       [--runtime docker|podman]'
}

fail() {
  printf 'linuxcloth package smoke: %s\n' "$*" >&2
  exit 1
}

package_path=
container_runtime=${CONTAINER_RUNTIME:-}

while (($#)); do
  case "$1" in
    --packages)
      (($# >= 2)) || fail '--packages requires a path'
      package_path=$2
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

[[ -n "$package_path" ]] || fail '--packages is required'
package_path=$(realpath "$package_path")
[[ -d "$package_path" ]] || fail "package directory does not exist: $package_path"
[[ -f $(find "$package_path" -maxdepth 1 -type f -name 'linuxcloth_*_amd64.deb' -print -quit) ]] ||
  fail 'an amd64 DEB package is required'
[[ -f $(find "$package_path" -maxdepth 1 -type f -name 'linuxcloth-*.x86_64.rpm' -print -quit) ]] ||
  fail 'an x86_64 RPM package is required'

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

debian_image='docker.io/library/debian@sha256:63a496b5d3b99214b39f5ed70eb71a61e590a77979c79cbee4faf991f8c0783e'
fedora_image='docker.io/library/fedora@sha256:6c75d5bf57cb0fa5aa4b92c6a83c86c791644496d9ac230de7711f5b8ec3b898'

run_smoke() {
  local family=$1
  local image=$2

  "$container_runtime" run --rm --platform linux/amd64 \
    --volume "$package_path:/packages:ro" \
    "$image" bash -s -- "$family" <<'CONTAINER_SCRIPT'
set -euo pipefail

family=$1
case "$family" in
  debian)
    . /etc/os-release
    [[ "$ID" == debian && "$VERSION_ID" == 12 ]]
    package=$(find /packages -maxdepth 1 -type f -name 'linuxcloth_*_amd64.deb' -print -quit)
    [[ -n "$package" ]]
    apt-get update
    DEBIAN_FRONTEND=noninteractive apt-get install --yes file "$package"
    dpkg --verify linuxcloth
    ;;
  fedora)
    . /etc/os-release
    [[ "$ID" == fedora && "$VERSION_ID" == 44 ]]
    package=$(find /packages -maxdepth 1 -type f -name 'linuxcloth-*.x86_64.rpm' -print -quit)
    [[ -n "$package" ]]
    dnf install --assumeyes file "$package"
    rpm --verify linuxcloth
    ;;
  *)
    printf 'unsupported package family: %s\n' "$family" >&2
    exit 1
    ;;
esac

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export HOME=/tmp/linuxcloth-smoke-home
export XDG_CACHE_HOME=$HOME/.cache
export XDG_CONFIG_HOME=$HOME/.config
export XDG_DATA_HOME=$HOME/.local/share
export XDG_RUNTIME_DIR=$HOME/runtime
install -d -m 0700 "$HOME" "$XDG_CACHE_HOME" "$XDG_CONFIG_HOME" \
  "$XDG_DATA_HOME" "$XDG_RUNTIME_DIR"

linuxcloth --version
linuxcloth catalog search WooriBank >/dev/null

while IFS= read -r -d '' candidate; do
  if file -b "$candidate" | grep -q '^ELF '; then
    link_output=$(ldd "$candidate" 2>&1) || {
      printf 'ldd failed for %s:\n%s\n' "$candidate" "$link_output" >&2
      exit 1
    }
    if grep -q 'not found' <<<"$link_output"; then
      printf 'unresolved ELF dependency for %s:\n%s\n' "$candidate" "$link_output" >&2
      exit 1
    fi
  fi
done < <(find /usr/lib/linuxcloth -type f -print0)

case "$family" in
  debian) apt-get remove --yes linuxcloth ;;
  fedora) dnf remove --assumeyes linuxcloth ;;
esac
[[ ! -e /usr/bin/linuxcloth ]]
[[ ! -e /usr/lib/linuxcloth ]]
CONTAINER_SCRIPT
}

run_smoke debian "$debian_image"
run_smoke fedora "$fedora_image"

printf 'linuxcloth DEB/RPM install smoke tests passed\n'
