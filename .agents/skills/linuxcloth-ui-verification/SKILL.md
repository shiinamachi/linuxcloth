---
name: linuxcloth-ui-verification
description: linuxcloth 데스크톱 UI 변경의 테마 규칙, 사용자 문구, 반응형 레이아웃, HiDPI, 키보드 및 자동화 접근성을 검증한다. Avalonia XAML이나 UI ViewModel 변경을 커밋하기 전, UI 회귀를 진단할 때 사용한다.
---

# Linuxcloth UI Verification

변경한 동작에 가장 가까운 테스트부터 실행하고, 커밋 전 전체 Desktop 테스트와 정적 정책 검사를 완료한다.

## 자동 검증

저장소 루트에서 다음을 실행한다.

```bash
.agents/skills/linuxcloth-ui-verification/scripts/verify-ui.sh
mise exec -- dotnet format linuxcloth.slnx --verify-no-changes --no-restore
```

스크립트는 테마 파일 밖의 직접 색상, 다크 테마 강제, 임시 유니코드 검색 아이콘, 개발 방법론 문구를 검사하고 Desktop 테스트를 실행한다.

## 접근성 검토

- 모든 상호작용 컨트롤에 안정적인 `AutomationId`를 부여한다.
- 아이콘 전용 버튼에 `AutomationProperties.Name`을 부여한다.
- 진행과 오류 상태에 적절한 `LiveSetting`을 사용한다.
- Tab 순서가 시각적 순서와 일치하는지 확인한다.
- Ctrl+F/Ctrl+K 검색, Escape 드로어 닫기, Enter 기본 동작을 확인한다.
- 색 없이도 준비/주의/오류 상태를 구분할 수 있는지 확인한다.
- Drawer를 닫았을 때 포커스가 목록이나 트리거로 복귀하는지 확인한다.

## 화면·배율 검토

- 720×480, 960×540, 1280×720, 1440×900에서 텍스트와 동작이 잘리지 않는지 확인한다.
- 100%, 125%, 150%, 200%에서 창 논리 크기가 유지되는지 확인한다.
- 시스템 라이트·다크 전환 후 직접 색상이나 이전 테마 자산이 남지 않는지 확인한다.
- 서비스 카드의 긴 한글·영문 이름, 오류, 경로가 컨테이너를 확장하거나 줄바꿈하는지 확인한다.
- 실기 환경에서는 GNOME/KDE Wayland, X11, 혼합 DPI 모니터 이동을 확인한다.

## 결과 보고

통과한 자동 테스트, 확인한 논리 크기·배율, 수동 검증하지 못한 환경을 분리해 기록한다. 수동 확인을 하지 않았다면 통과한 것으로 표현하지 않는다.
