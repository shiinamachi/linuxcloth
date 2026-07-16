---
name: linuxcloth-responsive-hidpi
description: linuxcloth Avalonia 화면을 작은 논리 창과 혼합 DPI 환경에 맞게 재배치하고 검증한다. 창 크기, Grid 열, 카드 크기, 이미지, 팝업·드로어, RenderScaling 또는 반응형 화면 전환을 추가하거나 변경할 때 사용한다.
---

# Linuxcloth Responsive HiDPI

레이아웃은 논리 단위로 유지하고 실제 컨테이너 폭에 따라 구조를 전환한다. DPI 배율을 레이아웃 값에 곱하지 않는다.

## 현재 지원 기준

- 최소 창 크기: 720×480 논리 단위
- 카탈로그 Compact: 820 미만
- 카탈로그 Medium: 820 이상 1180 미만
- 카탈로그 Wide: 1180 이상
- 설정 화면 Compact: 900 미만
- 검증 배율: 100%, 125%, 150%, 200%
- 수동 혼합 모니터 검증: 100%↔200%, 125%↔200%

## 작업 순서

1. 대상 UserControl의 실제 `Bounds.Width`를 기준으로 전환되는지 확인한다.
2. Compact에서 보조 레일을 숨기고 검색·필터·진행 상태를 재배치한다.
3. Medium에서 상세정보를 고정 열 대신 닫을 수 있는 드로어로 표시한다.
4. Wide에서 분류·목록·상세 영역을 동시에 제공한다.
5. 텍스트 컨트롤은 자동 높이와 줄바꿈을 허용하고 카드에 폭과 높이를 동시에 고정하지 않는다.
6. 핵심 동작을 세로 스크롤로 모두 접근할 수 있게 한다.
7. Headless 테스트에서 각 폭 구간과 200% RenderScaling을 검증한다.

## HiDPI 규칙

- Avalonia 레이아웃 크기는 논리 단위로 지정한다.
- `RenderScaling`은 래스터 디코딩, 픽셀 캡처, 네이티브 픽셀 좌표, 배율별 캐시 키에만 사용한다.
- 앱 크롬은 벡터를 사용한다.
- 래스터 서비스 로고는 `(service-id, logical-size, render-scaling, theme)` 기준으로 다시 디코딩할 수 있어야 한다.
- 팝업과 드로어가 TopLevel 경계 밖으로 나가지 않게 한다.
- 모달과 드로어는 Escape로 닫고, 닫은 뒤 포커스를 연 컨트롤 또는 목록으로 돌려보낸다.

## 확인

```bash
mise exec -- dotnet test tests/LinuxCloth.Desktop.Tests/LinuxCloth.Desktop.Tests.csproj --no-restore
```

실기 검증이 필요한 변경이면 GNOME Wayland, KDE Wayland, X11과 혼합 모니터 결과를 별도로 기록한다.
