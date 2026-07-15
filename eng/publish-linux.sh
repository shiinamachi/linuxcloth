#!/usr/bin/env bash
set -euo pipefail

usage() {
  printf '%s\n' \
    'Usage: eng/publish-linux.sh --output PATH [--version VERSION]' \
    '       [--configuration Release] [--skip-lock-verification]'
}

fail() {
  printf 'linuxcloth publish: %s\n' "$*" >&2
  exit 1
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "required command was not found: $1"
}

verify_sha256() {
  local file_path=$1
  local expected=$2
  local description=$3
  local actual
  actual=$(sha256sum "$file_path" | awk '{ print $1 }')
  [[ "$actual" == "$expected" ]] ||
    fail "$description SHA-256 mismatch (expected $expected, got $actual)"
}

merge_publish_tree() {
  local source_root=$1
  local destination_root=$2
  local source_path relative_path destination_path

  while IFS= read -r -d '' source_path; do
    relative_path=${source_path#"$source_root"/}
    destination_path="$destination_root/$relative_path"

    if [[ -d "$source_path" ]]; then
      install -d -m 0755 "$destination_path"
    elif [[ -f "$source_path" && ! -L "$source_path" ]]; then
      install -d -m 0755 "$(dirname "$destination_path")"
      if [[ -e "$destination_path" ]]; then
        cmp --silent "$source_path" "$destination_path" ||
          fail "publish outputs disagree on shared file: $relative_path"
      else
        cp --preserve=mode,timestamps "$source_path" "$destination_path"
      fi
    else
      fail "publish output contains an unsupported file type: $source_path"
    fi
  done < <(find "$source_root" -mindepth 1 -print0 | LC_ALL=C sort -z)
}

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
output_path=
version=
configuration=Release
verify_locks=1

while (($#)); do
  case "$1" in
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
    --configuration)
      (($# >= 2)) || fail '--configuration requires a value'
      configuration=$2
      shift 2
      ;;
    --skip-lock-verification)
      verify_locks=0
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

[[ -n "$output_path" ]] || fail '--output is required'
[[ "$configuration" =~ ^[A-Za-z0-9._-]+$ ]] || fail 'configuration contains unsupported characters'
[[ $(uname -s) == Linux ]] || fail 'the host publish target is Linux only'
[[ $(uname -m) == x86_64 ]] || fail 'the supported host architecture is x86_64 only'

require_command awk
require_command cmp
require_command chmod
require_command dotnet
require_command find
require_command git
require_command install
require_command mkdir
require_command realpath
require_command sha256sum
require_command sort
require_command touch

revision=$(git -C "$repo_root" rev-parse --verify HEAD)
[[ "$revision" =~ ^[0-9a-f]{40}$ ]] || fail 'could not determine the source revision'
source_tree_dirty=false
if [[ -n $(git -C "$repo_root" status --porcelain=v1 --untracked-files=normal) ]]; then
  source_tree_dirty=true
fi

if [[ -z "$version" ]]; then
  exact_tag=$(git -C "$repo_root" describe --tags --match 'v[0-9]*' --exact-match 2>/dev/null || true)
  if [[ -n "$exact_tag" ]]; then
    version=${exact_tag#v}
  else
    version="0.0.0+git.${revision:0:12}"
  fi
fi
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]] ||
  fail 'version must be a SemVer-compatible value without whitespace'

source_date_epoch=${SOURCE_DATE_EPOCH:-$(git -C "$repo_root" show -s --format=%ct HEAD)}
[[ "$source_date_epoch" =~ ^[0-9]+$ ]] || fail 'SOURCE_DATE_EPOCH must be an integer'

catalog_root="$repo_root/vendor/TableClothCatalog"
[[ -f "$catalog_root/docs/Catalog.xml" ]] ||
  fail 'the TableClothCatalog submodule is not initialized (missing docs/Catalog.xml)'
[[ -f "$catalog_root/LICENSE" ]] || fail 'the TableClothCatalog license is missing'
catalog_revision=$(git -C "$catalog_root" rev-parse --verify HEAD)
[[ "$catalog_revision" =~ ^[0-9a-f]{40}$ ]] || fail 'could not determine the catalog revision'

dotnet_path=$(command -v dotnet)
dotnet_root=${DOTNET_ROOT:-$(dirname "$(readlink -f "$dotnet_path")")}
dotnet_sdk_version=$(dotnet --version)
[[ "$dotnet_sdk_version" == 10.0.302 ]] ||
  fail "the pinned .NET SDK 10.0.302 is required (selected $dotnet_sdk_version)"
[[ -f "$dotnet_root/LICENSE.txt" ]] || fail ".NET runtime license was not found below $dotnet_root"
[[ -f "$dotnet_root/ThirdPartyNotices.txt" ]] ||
  fail ".NET runtime third-party notices were not found below $dotnet_root"

output_path=$(realpath -m "$output_path")
[[ "$output_path" != / ]] || fail 'refusing to replace the filesystem root'
[[ ! -e "$output_path" ]] || fail "output path already exists: $output_path"
output_parent=$(dirname "$output_path")
mkdir -p "$output_parent"
work_dir=$(mktemp -d "$output_parent/.linuxcloth-publish.XXXXXX")
trap 'rm -rf -- "$work_dir"' EXIT

publish_root="$work_dir/publish"
stage_root="$work_dir/rootfs"
install -d -m 0755 "$publish_root" "$stage_root/usr/lib/linuxcloth" \
  "$stage_root/usr/lib/linuxcloth/guest" "$stage_root/usr/bin" \
  "$stage_root/usr/share/applications" "$stage_root/usr/share/metainfo" \
  "$stage_root/usr/share/icons/hicolor/scalable/apps" \
  "$stage_root/usr/share/licenses/linuxcloth/third-party" \
  "$stage_root/usr/share/doc/linuxcloth" "$stage_root/usr/share/linuxcloth"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export NUGET_XMLDOC_MODE=skip
export SOURCE_DATE_EPOCH="$source_date_epoch"

cli_project="$repo_root/src/LinuxCloth.Cli/LinuxCloth.Cli.csproj"
desktop_project="$repo_root/src/LinuxCloth.Desktop/LinuxCloth.Desktop.csproj"
guest_project="$repo_root/guest/LinuxCloth.GuestBridge/LinuxCloth.GuestBridge.csproj"

runtime_restore_properties=(
  -p:NuGetLockFilePath="$work_dir/runtime-restore-disabled.lock"
  -p:RestorePackagesWithLockFile=false
)

if ((verify_locks)); then
  # Verify the checked-in managed dependency graph first. Runtime-specific SDK
  # packs are then restored with lock-file writes redirected away from the
  # source tree; their version is selected by the pinned SDK in global.json.
  dotnet restore "$repo_root/linuxcloth.slnx" --locked-mode
fi

common_publish=(
  --configuration "$configuration"
  --no-restore
  --self-contained true
  -p:ContinuousIntegrationBuild=true
  -p:DebugSymbols=false
  -p:DebugType=None
  -p:Deterministic=true
  -p:PublishReadyToRun=false
  -p:Version="$version"
)

dotnet restore "$cli_project" --runtime linux-x64 "${runtime_restore_properties[@]}"
dotnet restore "$desktop_project" --runtime linux-x64 "${runtime_restore_properties[@]}"
dotnet publish "$cli_project" "${common_publish[@]}" --runtime linux-x64 \
  --output "$publish_root/cli"
dotnet publish "$desktop_project" "${common_publish[@]}" --runtime linux-x64 \
  --output "$publish_root/desktop"

# GuestBridge shares project references with the host but uses a different RID.
# Restore it only after both host publishes so one target never overwrites the
# other's assets file before publication.
dotnet restore "$guest_project" --runtime win-x64 "${runtime_restore_properties[@]}"
dotnet publish "$guest_project" "${common_publish[@]}" --runtime win-x64 \
  --output "$publish_root/guest"

runtime_pack_version=$(awk -F'"' \
  '/runtimepack\.Microsoft\.NETCore\.App\.Runtime\.linux-x64/ { print $4; exit }' \
  "$publish_root/cli/linuxcloth.deps.json")
[[ "$runtime_pack_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] ||
  fail 'could not determine the published .NET runtime pack version'
grep -Fq "\"version\": \"[$runtime_pack_version, $runtime_pack_version]\"" \
  "$repo_root/guest/LinuxCloth.GuestBridge/obj/project.assets.json" ||
  fail 'the GuestBridge runtime pack differs from the host runtime pack'

merge_publish_tree "$publish_root/cli" "$stage_root/usr/lib/linuxcloth"
merge_publish_tree "$publish_root/desktop" "$stage_root/usr/lib/linuxcloth"

# The portable runtime pack includes an optional LTTng tracepoint provider that
# links against the obsolete liblttng-ust.so.0 ABI. Debian 12 and Fedora 44 no
# longer provide that ABI, and the application does not use LTTng diagnostics.
# Omit the optional provider instead of shipping an unresolved or unsafe ABI
# compatibility symlink. LTTng tracepoint tracing is therefore not a packaged
# feature until a compatible reviewed provider is available.
rm -f "$stage_root/usr/lib/linuxcloth/libcoreclrtraceptprovider.so"

guest_executable="$publish_root/guest/linuxcloth-guest-bridge.exe"
[[ -f "$guest_executable" ]] || fail 'GuestBridge publish did not produce linuxcloth-guest-bridge.exe'
install -m 0644 "$guest_executable" \
  "$stage_root/usr/lib/linuxcloth/guest/linuxcloth-guest-bridge.exe"

[[ -x "$stage_root/usr/lib/linuxcloth/linuxcloth" ]] || fail 'CLI apphost is missing or not executable'
[[ -x "$stage_root/usr/lib/linuxcloth/linuxcloth-desktop" ]] ||
  fail 'desktop apphost is missing or not executable'
ln -s ../lib/linuxcloth/linuxcloth "$stage_root/usr/bin/linuxcloth"
ln -s ../lib/linuxcloth/linuxcloth-desktop "$stage_root/usr/bin/linuxcloth-desktop"

install -m 0644 "$repo_root/packaging/linux/io.github.shiinamachi.linuxcloth.desktop" \
  "$stage_root/usr/share/applications/io.github.shiinamachi.linuxcloth.desktop"
install -m 0644 "$repo_root/packaging/linux/io.github.shiinamachi.linuxcloth.metainfo.xml" \
  "$stage_root/usr/share/metainfo/io.github.shiinamachi.linuxcloth.metainfo.xml"
install -m 0644 "$repo_root/packaging/linux/io.github.shiinamachi.linuxcloth.svg" \
  "$stage_root/usr/share/icons/hicolor/scalable/apps/io.github.shiinamachi.linuxcloth.svg"

install -m 0644 "$repo_root/LICENSE" "$stage_root/usr/share/licenses/linuxcloth/LICENSE"
install -m 0644 "$repo_root/THIRD_PARTY_NOTICES.md" \
  "$stage_root/usr/share/licenses/linuxcloth/THIRD_PARTY_NOTICES.md"
install -m 0644 "$catalog_root/LICENSE" \
  "$stage_root/usr/share/licenses/linuxcloth/TableClothCatalog-LICENSE"
install -m 0644 "$dotnet_root/LICENSE.txt" \
  "$stage_root/usr/share/licenses/linuxcloth/dotnet-LICENSE.txt"
install -m 0644 "$dotnet_root/ThirdPartyNotices.txt" \
  "$stage_root/usr/share/licenses/linuxcloth/dotnet-ThirdPartyNotices.txt"
verify_sha256 "$repo_root/third_party/notices/Avalonia-LICENSE.txt" \
  d983e7bf294f9770bfcf7695466cad5c126e497653a0594499f388c8e4a49eb6 \
  'Avalonia license'
verify_sha256 "$repo_root/third_party/notices/Avalonia-NOTICE.md" \
  dd6f80852d16320c865a2e403ffd1a8b07a0e34c5b37122c0bc2343639ff68e6 \
  'Avalonia notice'
verify_sha256 "$repo_root/third_party/notices/MicroCom-LICENSE.txt" \
  6ee769c9ac4dac9abb16b98b1341e9528ff9f4ab685481410d3376d14148f3a9 \
  'MicroCom license'
verify_sha256 "$repo_root/third_party/notices/Tmds.DBus.Protocol-LICENSE.txt" \
  d7cca2dc9211a140ce3e1dbfd90ac911d49268b5ddc2162a06a79ea85d1d67f1 \
  'Tmds.DBus.Protocol license'
verify_sha256 "$repo_root/third_party/notices/SkiaSharp-HarfBuzzSharp-LICENSE.txt" \
  bc2eb4f37d574f9b1b67da2b17e14d151618850527d78f7323239e7518df5b77 \
  'SkiaSharp/HarfBuzzSharp license'
verify_sha256 "$repo_root/third_party/notices/SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt" \
  98acf9d4d6083959988c884f630cdff760f94bfeb9acf57774653e08c23d1e45 \
  'SkiaSharp/HarfBuzzSharp third-party notices'
verify_sha256 "$repo_root/third_party/notices/Inter-OFL.txt" \
  262481e844521b326f5ecd053e59b98c8b2da78c8ee1bdbb6e8174305e54935a \
  'Inter font license'
install -m 0644 "$repo_root/third_party/notices/Avalonia-NOTICE.md" \
  "$stage_root/usr/share/licenses/linuxcloth/third-party/Avalonia-NOTICE.md"
install -m 0644 "$repo_root/third_party/notices/Avalonia-LICENSE.txt" \
  "$stage_root/usr/share/licenses/linuxcloth/third-party/Avalonia-LICENSE.txt"
install -m 0644 "$repo_root/third_party/notices/MicroCom-LICENSE.txt" \
  "$stage_root/usr/share/licenses/linuxcloth/third-party/MicroCom-LICENSE.txt"
install -m 0644 "$repo_root/third_party/notices/Tmds.DBus.Protocol-LICENSE.txt" \
  "$stage_root/usr/share/licenses/linuxcloth/third-party/Tmds.DBus.Protocol-LICENSE.txt"
install -m 0644 "$repo_root/third_party/notices/SkiaSharp-HarfBuzzSharp-LICENSE.txt" \
  "$stage_root/usr/share/licenses/linuxcloth/third-party/SkiaSharp-HarfBuzzSharp-LICENSE.txt"
install -m 0644 "$repo_root/third_party/notices/SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt" \
  "$stage_root/usr/share/licenses/linuxcloth/third-party/SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt"
install -m 0644 "$repo_root/third_party/notices/Inter-OFL.txt" \
  "$stage_root/usr/share/licenses/linuxcloth/third-party/Inter-OFL.txt"
install -m 0644 "$repo_root/third_party/notices/SOURCES.md" \
  "$stage_root/usr/share/licenses/linuxcloth/third-party/SOURCES.md"
install -m 0644 "$repo_root/packaging/deb/copyright" \
  "$stage_root/usr/share/doc/linuxcloth/copyright"

printf '%s\n' \
  '{' \
  '  "schemaVersion": 1,' \
  "  \"version\": \"$version\"," \
  "  \"sourceRevision\": \"$revision\"," \
  "  \"sourceTreeDirty\": $source_tree_dirty," \
  "  \"catalogRevision\": \"$catalog_revision\"," \
  "  \"sourceDateEpoch\": $source_date_epoch," \
  "  \"dotnetSdkVersion\": \"$dotnet_sdk_version\"," \
  "  \"dotnetRuntimePackVersion\": \"$runtime_pack_version\"," \
  '  "hostRuntimeIdentifier": "linux-x64",' \
  '  "guestRuntimeIdentifier": "win-x64",' \
  '  "repository": "https://github.com/shiinamachi/linuxcloth"' \
  '}' >"$stage_root/usr/share/linuxcloth/build-info.json"

find "$stage_root" -type d -exec chmod 0755 {} +
find "$stage_root" -type f -exec chmod 0644 {} +
chmod 0755 "$stage_root/usr/lib/linuxcloth/linuxcloth" \
  "$stage_root/usr/lib/linuxcloth/linuxcloth-desktop"
if [[ -f "$stage_root/usr/lib/linuxcloth/createdump" ]]; then
  chmod 0755 "$stage_root/usr/lib/linuxcloth/createdump"
fi
find "$stage_root" -exec touch -h -d "@$source_date_epoch" {} +

(
  cd "$stage_root"
  find . -type f ! -path './usr/share/linuxcloth/files.sha256' -print0 |
    LC_ALL=C sort -z |
    xargs -0 sha256sum >usr/share/linuxcloth/files.sha256
)
touch -d "@$source_date_epoch" "$stage_root/usr/share/linuxcloth/files.sha256"

"$repo_root/eng/validate-package-tree.sh" --root "$stage_root"

mv "$stage_root" "$output_path"
printf 'linuxcloth staging tree: %s\n' "$output_path"
printf 'version: %s\nsource revision: %s\ncatalog revision: %s\n' \
  "$version" "$revision" "$catalog_revision"
