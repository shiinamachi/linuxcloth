#!/usr/bin/env bash
set -euo pipefail

usage() {
  printf '%s\n' 'Usage: eng/package-rpm.sh --stage PATH --output PATH [--version VERSION]'
}

fail() {
  printf 'linuxcloth rpm package: %s\n' "$*" >&2
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
for command_name in cpio date find gzip install mkdir realpath rpm rpm2cpio rpmbuild sed tar; do
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
rpm_version=${version//+/.}
rpm_version=${rpm_version//-/\~}

source_date_epoch=${SOURCE_DATE_EPOCH:-$(sed -n 's/^  "sourceDateEpoch": \([0-9]*\),$/\1/p' "$build_info")}
[[ "$source_date_epoch" =~ ^[0-9]+$ ]] || fail 'SOURCE_DATE_EPOCH is missing or invalid'
export SOURCE_DATE_EPOCH="$source_date_epoch"

mkdir -p "$output_path"
work_dir=$(mktemp -d "$output_path/.linuxcloth-rpm.XXXXXX")
trap 'rm -rf -- "$work_dir"' EXIT
top_dir="$work_dir/rpmbuild"
install -d -m 0755 "$top_dir/BUILD" "$top_dir/BUILDROOT" "$top_dir/RPMS" \
  "$top_dir/SOURCES" "$top_dir/SPECS" "$top_dir/SRPMS"

source_directory="linuxcloth-rootfs-$rpm_version"
install -d -m 0755 "$work_dir/source/$source_directory"
cp -a "$stage_path/." "$work_dir/source/$source_directory/"
find "$work_dir/source/$source_directory" -exec touch -h -d "@$source_date_epoch" {} +
tar --sort=name --mtime="@$source_date_epoch" --owner=0 --group=0 --numeric-owner \
  -C "$work_dir/source" -cf - "$source_directory" |
  gzip -n -9 >"$top_dir/SOURCES/$source_directory.tar.gz"

changelog_date=$(LC_ALL=C date -u -d "@$source_date_epoch" '+%a %b %d %Y')
sed -e "s/@VERSION@/$rpm_version/g" -e "s/@SOURCE_DIRECTORY@/$source_directory/g" \
  -e "s/@CHANGELOG_DATE@/$changelog_date/g" \
  "$repo_root/packaging/rpm/linuxcloth.spec.in" >"$top_dir/SPECS/linuxcloth.spec"

rpmbuild --define "_topdir $top_dir" --define '_buildhost reproducible.invalid' \
  --define 'clamp_mtime_to_source_date_epoch 1' \
  --define 'use_source_date_epoch_as_buildtime 1' \
  --define "_source_date_epoch $source_date_epoch" \
  -bb "$top_dir/SPECS/linuxcloth.spec" >/dev/null

temporary_package=$(find "$top_dir/RPMS" -type f -name 'linuxcloth-*.rpm' -print -quit)
[[ -n "$temporary_package" ]] || fail 'rpmbuild did not produce a package'
rpm -qpi "$temporary_package" >/dev/null

install -d -m 0755 "$work_dir/extracted"
rpm2cpio "$temporary_package" | (cd "$work_dir/extracted" && cpio -idm --quiet --no-absolute-filenames)
"$repo_root/eng/validate-package-tree.sh" --root "$work_dir/extracted"

package_path="$output_path/$(basename "$temporary_package")"
install -m 0644 "$temporary_package" "$package_path"
sha256sum "$package_path"
