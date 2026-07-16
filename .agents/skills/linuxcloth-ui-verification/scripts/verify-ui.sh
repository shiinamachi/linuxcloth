#!/usr/bin/env bash
set -euo pipefail

repository_root="$(git rev-parse --show-toplevel)"
cd "$repository_root"

failures=0

if rg -n '#[0-9A-Fa-f]{6,8}' src/LinuxCloth.Desktop --glob '*.axaml' --glob '!**/Styles/ThemeResources.axaml'; then
  echo "테마 리소스 밖에서 직접 색상을 발견했습니다." >&2
  failures=1
fi

if rg -n 'RequestedThemeVariant="Dark"|Text="⌕"|기술 미리보기|패키지 이름이 아니라 실제 실행|Linux 검증 정보는 공식 데이터' src/LinuxCloth.Desktop; then
  echo "기본 UI에서 금지된 테마 또는 문구 패턴을 발견했습니다." >&2
  failures=1
fi

if (( failures != 0 )); then
  exit 1
fi

mise exec -- dotnet test tests/LinuxCloth.Desktop.Tests/LinuxCloth.Desktop.Tests.csproj --no-restore
