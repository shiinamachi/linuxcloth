---
name: linuxcloth-design-system
description: linuxcloth Avalonia 데스크톱 UI를 의미 기반 색상·간격·타이포그래피 토큰과 재사용 스타일로 구현한다. XAML 화면, 공통 컨트롤, 테마, 아이콘, 카드, 버튼 또는 상태 표현을 만들거나 시각 디자인을 변경할 때 사용한다.
---

# Linuxcloth Design System

`Styles/ThemeResources.axaml`을 의미 토큰의 단일 원본으로, `Styles/Controls.axaml`을 공통 표현 규칙으로 사용한다.

## 작업 순서

1. `App.axaml`, `Styles/ThemeResources.axaml`, `Styles/Controls.axaml`을 먼저 읽는다.
2. 기존 의미 토큰과 클래스 스타일로 요구사항을 충족할 수 있는지 확인한다.
3. 새 의미가 필요할 때만 라이트·다크 양쪽 ThemeDictionary에 같은 키를 추가한다.
4. 두 화면 이상에서 반복되는 구조는 `Controls/`의 UserControl 또는 공통 스타일로 추출한다.
5. 상호작용 상태와 접근성 속성을 추가하고 Headless 테스트를 갱신한다.
6. UI 검증 스크립트와 좁은 관련 테스트를 실행한다.

## 규칙

- 테마 리소스 밖의 XAML에 `#RRGGBB` 또는 `#AARRGGBB` 색상을 쓰지 않는다.
- 색의 용도는 `Surface`, `Text`, `Border`, `Accent`, `Success`, `Warning`, `Danger`, `Info`, `Focus` 의미로 이름 짓는다.
- 화면 XAML에서는 `DynamicResource`로 브러시를 참조한다.
- 운영체제의 시스템/라이트/다크 선택을 따르고 `RequestedThemeVariant="Dark"`를 강제하지 않는다.
- 본문 14, 보조 텍스트 11 이상, 섹션 제목 18–20, 화면 제목 24–28 논리 단위를 기준으로 한다.
- 간격은 4/8/12/16/24/32, 모서리는 8/12/16 논리 단위 체계를 우선한다.
- 핵심 동작은 `primary`, 보조 동작은 `secondary`, 저강도 동작은 `ghost` 클래스를 사용한다.
- 앱 크롬 아이콘은 `StreamGeometry`와 `PathIcon`을 사용한다. 유니코드 기호나 이모지를 아이콘으로 쓰지 않는다.
- 서비스 로고는 공식 모양을 보존하기 위해 `CatalogLogoBackgroundBrush` 위에 표시한다.
- 색만으로 상태를 구분하지 않고 텍스트 또는 아이콘을 함께 제공한다.

## 확인

```bash
.agents/skills/linuxcloth-ui-verification/scripts/verify-ui.sh
```

새 토큰을 추가했다면 라이트·다크 값과 대비 목적을 최종 보고에 기록한다.
