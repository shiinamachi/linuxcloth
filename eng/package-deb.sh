#!/usr/bin/env bash
set -euo pipefail

usage() {
  printf '%s\n' 'Usage: eng/package-deb.sh --stage PATH --output PATH [--version VERSION]'
}

fail() {
  printf 'linuxcloth deb package: %s\n' "$*" >&2
  exit 1
}

stage_path=
output_path=
version=

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
    --version)
      (($# >= 2)) || fail '--version requires a value'
      version=$2
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
for command_name in dpkg-deb find install md5sum mkdir realpath sed touch; do
  command -v "$command_name" >/dev/null 2>&1 || fail "required command was not found: $command_name"
done

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
stage_path=$(realpath "$stage_path")
output_path=$(realpath -m "$output_path")
[[ -d "$stage_path" ]] || fail "staging root does not exist: $stage_path"
"$repo_root/eng/validate-package-tree.sh" --root "$stage_path"

build_info="$stage_path/usr/share/linuxcloth/build-info.json"
if [[ -z "$version" ]]; then
  version=$(sed -n 's/^  "version": "\([^"]*\)",$/\1/p' "$build_info")
fi
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]] ||
  fail 'package version is missing or invalid'
debian_version=${version//-/\~}

source_date_epoch=${SOURCE_DATE_EPOCH:-$(sed -n 's/^  "sourceDateEpoch": \([0-9]*\),$/\1/p' "$build_info")}
[[ "$source_date_epoch" =~ ^[0-9]+$ ]] || fail 'SOURCE_DATE_EPOCH is missing or invalid'
export SOURCE_DATE_EPOCH="$source_date_epoch"

mkdir -p "$output_path"
work_dir=$(mktemp -d "$output_path/.linuxcloth-deb.XXXXXX")
trap 'rm -rf -- "$work_dir"' EXIT
package_root="$work_dir/root"
install -d -m 0755 "$package_root/DEBIAN"
cp -a "$stage_path/." "$package_root/"

installed_size=$(du -sk "$package_root/usr" | awk '{print $1}')
sed -e "s/@VERSION@/$debian_version/g" -e "s/@INSTALLED_SIZE@/$installed_size/g" \
  "$repo_root/packaging/deb/control.in" >"$package_root/DEBIAN/control"
chmod 0644 "$package_root/DEBIAN/control"

(
  cd "$package_root"
  find usr -type f -print0 | LC_ALL=C sort -z | xargs -0 md5sum >DEBIAN/md5sums
)
chmod 0644 "$package_root/DEBIAN/md5sums"
find "$package_root" -exec touch -h -d "@$source_date_epoch" {} +

package_path="$output_path/linuxcloth_${debian_version}_amd64.deb"
temporary_package="$work_dir/linuxcloth.deb"
dpkg-deb --root-owner-group --uniform-compression -Zxz -z9 --build \
  "$package_root" "$temporary_package" >/dev/null
dpkg-deb --info "$temporary_package" >/dev/null

install -d -m 0755 "$work_dir/extracted"
dpkg-deb --extract "$temporary_package" "$work_dir/extracted"
"$repo_root/eng/validate-package-tree.sh" --root "$work_dir/extracted"

install -m 0644 "$temporary_package" "$package_path"
sha256sum "$package_path"
