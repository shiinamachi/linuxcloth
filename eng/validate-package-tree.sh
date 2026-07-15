#!/usr/bin/env bash
set -euo pipefail

usage() {
  printf '%s\n' \
    'Usage: eng/validate-package-tree.sh --root PATH [--compare PATH]' \
    '       [--strict-tools]'
}

fail() {
  printf 'linuxcloth package validation: %s\n' "$*" >&2
  exit 1
}

root_path=
compare_path=
strict_tools=0

while (($#)); do
  case "$1" in
    --root)
      (($# >= 2)) || fail '--root requires a path'
      root_path=$2
      shift 2
      ;;
    --compare)
      (($# >= 2)) || fail '--compare requires a path'
      compare_path=$2
      shift 2
      ;;
    --strict-tools)
      strict_tools=1
      shift
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

[[ -n "$root_path" ]] || fail '--root is required'
root_path=$(realpath "$root_path")
[[ -d "$root_path" ]] || fail "staging root does not exist: $root_path"

required_files=(
  usr/lib/linuxcloth/linuxcloth
  usr/lib/linuxcloth/linuxcloth-desktop
  usr/lib/linuxcloth/guest/linuxcloth-guest-bridge.exe
  usr/lib/linuxcloth/catalog/Catalog.xml
  usr/lib/linuxcloth/catalog/LICENSE
  usr/share/applications/io.github.shiinamachi.linuxcloth.desktop
  usr/share/metainfo/io.github.shiinamachi.linuxcloth.metainfo.xml
  usr/share/icons/hicolor/scalable/apps/io.github.shiinamachi.linuxcloth.svg
  usr/share/licenses/linuxcloth/LICENSE
  usr/share/licenses/linuxcloth/THIRD_PARTY_NOTICES.md
  usr/share/licenses/linuxcloth/TableClothCatalog-LICENSE
  usr/share/licenses/linuxcloth/dotnet-LICENSE.txt
  usr/share/licenses/linuxcloth/dotnet-ThirdPartyNotices.txt
  usr/share/licenses/linuxcloth/third-party/Avalonia-NOTICE.md
  usr/share/licenses/linuxcloth/third-party/Avalonia-LICENSE.txt
  usr/share/licenses/linuxcloth/third-party/MicroCom-LICENSE.txt
  usr/share/licenses/linuxcloth/third-party/Tmds.DBus.Protocol-LICENSE.txt
  usr/share/licenses/linuxcloth/third-party/SkiaSharp-HarfBuzzSharp-LICENSE.txt
  usr/share/licenses/linuxcloth/third-party/SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt
  usr/share/licenses/linuxcloth/third-party/Inter-OFL.txt
  usr/share/licenses/linuxcloth/third-party/SOURCES.md
  usr/share/doc/linuxcloth/copyright
  usr/share/linuxcloth/build-info.json
  usr/share/linuxcloth/files.sha256
)

for relative_path in "${required_files[@]}"; do
  [[ -f "$root_path/$relative_path" ]] || fail "required file is missing: $relative_path"
done
[[ -x "$root_path/usr/lib/linuxcloth/linuxcloth" ]] || fail 'CLI apphost is not executable'
[[ -x "$root_path/usr/lib/linuxcloth/linuxcloth-desktop" ]] ||
  fail 'desktop apphost is not executable'

[[ -L "$root_path/usr/bin/linuxcloth" ]] || fail 'CLI launcher must be a symbolic link'
[[ $(readlink "$root_path/usr/bin/linuxcloth") == ../lib/linuxcloth/linuxcloth ]] ||
  fail 'CLI launcher has an unexpected target'
[[ -L "$root_path/usr/bin/linuxcloth-desktop" ]] ||
  fail 'desktop launcher must be a symbolic link'
[[ $(readlink "$root_path/usr/bin/linuxcloth-desktop") == ../lib/linuxcloth/linuxcloth-desktop ]] ||
  fail 'desktop launcher has an unexpected target'

while IFS= read -r -d '' link_path; do
  case "${link_path#"$root_path"/}" in
    usr/bin/linuxcloth|usr/bin/linuxcloth-desktop) ;;
    *) fail "unexpected symbolic link: ${link_path#"$root_path"/}" ;;
  esac
  resolved_target=$(realpath -m "$(dirname "$link_path")/$(readlink "$link_path")")
  [[ "$resolved_target" == "$root_path"/* ]] || fail "symbolic link escapes staging root: $link_path"
done < <(find "$root_path" -type l -print0)

while IFS= read -r -d '' file_path; do
  relative_path=${file_path#"$root_path"/}
  [[ "$relative_path" != *$'\n'* ]] || fail 'staging tree contains a newline in a file name'
  lowercase_path=${relative_path,,}
  case "$lowercase_path" in
    *.pdb|*.iso|*.qcow2|*.qcow|*.vhd|*.vhdx|*.vmdk|*.wim|*.esd|*.pfx|*.p12|*.key|*.fd)
      fail "forbidden media, state, key, or debug artifact: $relative_path"
      ;;
    *.exe)
      [[ "$relative_path" == usr/lib/linuxcloth/guest/linuxcloth-guest-bridge.exe ]] ||
        fail "unexpected Windows executable: $relative_path"
      ;;
  esac
done < <(find "$root_path" -type f -print0)

[[ ! -e "$root_path/usr/lib/linuxcloth/libcoreclrtraceptprovider.so" ]] ||
  fail 'the unsupported legacy-LTTng tracepoint provider must not be packaged'

while IFS= read -r -d '' directory_path; do
  [[ $(stat -c '%a' "$directory_path") == 755 ]] ||
    fail "directory mode must be 0755: ${directory_path#"$root_path"/}"
done < <(find "$root_path" -type d -print0)

while IFS= read -r -d '' file_path; do
  relative_path=${file_path#"$root_path"/}
  expected_mode=644
  case "$relative_path" in
    usr/lib/linuxcloth/linuxcloth|usr/lib/linuxcloth/linuxcloth-desktop|usr/lib/linuxcloth/createdump)
      expected_mode=755
      ;;
  esac
  [[ $(stat -c '%a' "$file_path") == "$expected_mode" ]] ||
    fail "regular file mode must be 0$expected_mode: $relative_path"
done < <(find "$root_path" -type f -print0)

manifest_path="$root_path/usr/share/linuxcloth/files.sha256"
(
  cd "$root_path"
  sha256sum --check --strict usr/share/linuxcloth/files.sha256 >/dev/null
)

temporary_directory=$(mktemp -d)
trap 'rm -rf -- "$temporary_directory"' EXIT
(
  cd "$root_path"
  find . -type f ! -path './usr/share/linuxcloth/files.sha256' -print0 |
    LC_ALL=C sort -z |
    tr '\0' '\n' >"$temporary_directory/actual-files"
  sed -n 's/^[0-9a-f]\{64\}  //p' usr/share/linuxcloth/files.sha256 \
    >"$temporary_directory/manifest-files"
)
cmp --silent "$temporary_directory/actual-files" "$temporary_directory/manifest-files" ||
  fail 'file manifest does not describe the complete regular-file set'

validate_tool() {
  local tool_name=$1
  if command -v "$tool_name" >/dev/null 2>&1; then
    return 0
  fi
  ((strict_tools == 0)) || fail "validation command was not found: $tool_name"
  return 1
}

if validate_tool file; then
  file "$root_path/usr/lib/linuxcloth/linuxcloth" |
    grep -Eq 'ELF 64-bit LSB.*x86-64' || fail 'CLI apphost is not a Linux x86-64 ELF file'
  file "$root_path/usr/lib/linuxcloth/linuxcloth-desktop" |
    grep -Eq 'ELF 64-bit LSB.*x86-64' || fail 'desktop apphost is not a Linux x86-64 ELF file'
  file "$root_path/usr/lib/linuxcloth/guest/linuxcloth-guest-bridge.exe" |
    grep -Eq 'PE32\+ executable.*x86-64' || fail 'GuestBridge is not a Windows x86-64 PE file'
fi

if validate_tool desktop-file-validate; then
  desktop-file-validate \
    "$root_path/usr/share/applications/io.github.shiinamachi.linuxcloth.desktop"
fi
if validate_tool appstreamcli; then
  appstreamcli validate --no-net \
    "$root_path/usr/share/metainfo/io.github.shiinamachi.linuxcloth.metainfo.xml"
fi

if [[ -n "$compare_path" ]]; then
  compare_path=$(realpath "$compare_path")
  [[ -d "$compare_path" ]] || fail "comparison root does not exist: $compare_path"
  cmp --silent "$manifest_path" "$compare_path/usr/share/linuxcloth/files.sha256" ||
    fail 'staging manifests are not reproducible'
  [[ $(readlink "$compare_path/usr/bin/linuxcloth") == ../lib/linuxcloth/linuxcloth ]] ||
    fail 'comparison CLI launcher has an unexpected target'
  [[ $(readlink "$compare_path/usr/bin/linuxcloth-desktop") == ../lib/linuxcloth/linuxcloth-desktop ]] ||
    fail 'comparison desktop launcher has an unexpected target'
fi

printf 'linuxcloth package tree is valid: %s\n' "$root_path"
