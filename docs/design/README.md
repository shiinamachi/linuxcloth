# linuxcloth desktop design concepts

These images are product-design references for the Avalonia desktop application.
They preserve the current user flows while presenting them as a consistent,
work-focused desktop utility in both Korean and English.

## Visual direction

- Use a restrained Windows utility aesthetic: `#F8F8F8` application canvas,
  white and light-gray panels, subtle `#E1E1E1` borders, and system blue
  `#0078D4` for selection and primary actions.
- Prefer flat surfaces, 2–4 px corner radii, thin separators, compact controls,
  and clear Segoe UI / Noto Sans typography over decorative cards or marketing
  artwork.
- Use 4/8/12/16/24/32 spacing, a 24–28 px page title, 18–20 px section titles,
  14 px body copy, and at least 11 px secondary text.
- Keep application chrome and status icons vector-like. Catalog service artwork
  is shown on a neutral white logo surface.
- Use text plus an icon for success, warning, danger, and progress states. Color
  is never the only state indicator.
- Show the language selector in the window chrome. Korean and English variants
  keep the same hierarchy and control placement while allowing text to reflow.
- Keep implementation terms in a collapsed technical-details region. License
  confirmation, disposable-session deletion, and recovery consequences remain
  visible at the decision point.

## Responsive reference

The primary design canvas is a 1440×900 logical desktop window. The catalog
uses its wide three-column layout: category rail, service grid, and details
panel. The service-details concept additionally demonstrates the medium-width
dismissible drawer. Designs should continue to collapse vertically at the
supported 720×480 minimum without hiding the primary action.

## Screen manifest

| Screen | Korean | English | Source surface |
|---|---|---|---|
| Startup | [Korean](01-startup-ko.png) | [English](01-startup-en.png) | `ShellWindow` startup panel |
| Setup inputs | [Korean](02-setup-ready-ko.png) | [English](02-setup-ready-en.png) | `SetupWizardView.IsReady` |
| Setup progress | [Korean](03-setup-progress-ko.png) | [English](03-setup-progress-en.png) | `SetupWizardView.IsRunning` |
| Setup blocker | [Korean](04-setup-blocked-ko.png) | [English](04-setup-blocked-en.png) | `SetupWizardView.IsBlocked` |
| Catalog | [Korean](05-catalog-ko.png) | [English](05-catalog-en.png) | `MainWindow` wide layout |
| Service details | [Korean](06-service-details-ko.png) | [English](06-service-details-en.png) | `ServiceDetails` medium drawer |
| Recovery | [Korean](07-recovery-ko.png) | [English](07-recovery-en.png) | `RecoveryView` |
| Close confirmation | [Korean](08-close-confirmation-ko.png) | [English](08-close-confirmation-en.png) | `ActiveOperationCloseDialog` |

The raster files are visual references, not localization sources. Implemented
copy must come from locale resources so text can reflow, expose correct screen
reader language metadata, and remain sharp at every render scale.

## Image-generation prompt set

All files use the built-in image generation path and the `ui-mockup` use case.
The following shared prompt is combined with the screen-specific content and
locale copy listed below.

```text
Use case: ui-mockup
Asset type: shippable Linux desktop application screen for linuxcloth
Primary request: create a polished, realistic work-focused desktop utility UI
Style/medium: high-fidelity flat Avalonia desktop UI, not concept art and not a browser page
Composition/framing: straight-on 1440×900 logical desktop app window, fully visible, crisp 16:10 landscape composition
Color palette: #F8F8F8 canvas, white and light-gray panels, #E1E1E1 borders, #1A1A1A text, #0078D4 primary blue, restrained semantic green/amber/red
Typography: Segoe UI and Noto Sans; compact but readable; clear 24–28 px page title, 18–20 px section headings, 14 px body, at least 11 px metadata
Materials/textures: flat digital surfaces, thin dividers, minimal shadow, 2–4 px corner radii
Constraints: preserve the requested information architecture; practical keyboard-friendly controls; vector-like monochrome icons; no gradients; no glassmorphism; no oversized cards; no marketing hero; no people; no device mockup; no browser chrome; no watermark; render every requested string verbatim exactly once and add no other visible copy
```

### 01 Startup

- Korean: `linuxcloth`, `준비 중…`, `한국어`
- English: `linuxcloth`, `Getting ready…`, `English`
- Center a compact brand label, status text, and thin indeterminate progress bar
  inside the otherwise empty application canvas.

### 02 Setup inputs

- Korean: `Windows 환경 준비`, `필요한 항목을 선택하면 설치부터 확인까지 한 번에 진행합니다.`, `나중에`, `시스템 준비`, `준비됨`, `다시 확인`, `설치 파일`, `Windows 11`, `파일 확인 완료`, `파일 선택`, `Windows 장치 드라이버`, `자동 준비됨`, `로컬 파일 선택`, `이 Windows 버전을 사용할 수 있는 라이선스가 있습니다`, `다음 실행을 위해 파일 위치 기억`, `고급 설정`, `기술 세부정보`, `Windows 환경 준비하기`, `한국어`
- English: `Prepare a Windows environment`, `Choose the required items to install and verify everything in one flow.`, `Later`, `System readiness`, `Ready`, `Check again`, `Installation files`, `Windows 11`, `File verified`, `Choose file`, `Windows device drivers`, `Prepared automatically`, `Choose local file`, `I have a license to use this Windows edition`, `Remember file locations for next time`, `Advanced settings`, `Technical details`, `Prepare Windows environment`, `English`

### 03 Setup progress

- Korean: `Windows 환경을 준비하고 있습니다`, `앱을 닫아도 다음 실행에서 안전한 지점부터 계속할 수 있습니다.`, `시스템 확인`, `필수 구성 요소`, `설치 파일 확인`, `Windows 설치`, `환경 확인`, `마무리`, `완료`, `진행 중`, `대기`, `Windows를 자동으로 설치하고 있습니다.`, `설치 화면 보기`, `안전하게 중단`, `한국어`
- English: `Preparing the Windows environment`, `You can close the app and continue from a safe point next time.`, `System check`, `Required components`, `Installation file check`, `Windows installation`, `Environment verification`, `Finish`, `Complete`, `In progress`, `Waiting`, `Installing Windows automatically.`, `View installer`, `Stop safely`, `English`

### 04 Setup blocker

- Korean: `Windows 환경 준비`, `Windows 사용 권한을 확인하세요`, `Windows 환경을 만들기 전에 사용 가능한 라이선스를 확인해 주세요.`, `기술 세부정보`, `오류 코드: SETUP-INPUT-LICENSE`, `입력 화면으로 돌아가기`, `한국어`
- English: `Prepare a Windows environment`, `Confirm your Windows license`, `Confirm that you have a valid license before creating a Windows environment.`, `Technical details`, `Error code: SETUP-INPUT-LICENSE`, `Return to inputs`, `English`

### 05 Catalog

- Korean: `linuxcloth`, `준비됨`, `Windows 환경`, `접속하려는 사이트를 선택하거나, 검색창에서 검색어를 입력하세요.`, `서비스 검색`, `전체`, `브라우저`, `메신저`, `미디어`, `도구`, `업무`, `기본 환경`, `서비스 상세`, `호환 확인됨`, `닫으면 이번 실행의 파일과 변경사항이 삭제됩니다.`, `실행하기`, `준비됨 · 서비스를 선택하세요`, `한국어`
- English: `linuxcloth`, `Ready`, `Windows environment`, `Choose a site to open or enter a keyword in the search box.`, `Search services`, `All`, `Browsers`, `Messaging`, `Media`, `Tools`, `Productivity`, `Default environment`, `Service details`, `Compatibility verified`, `Files and changes from this session are deleted when you close it.`, `Launch`, `Ready · Choose a service`, `English`
- Use the wide category rail, compact service icon grid, and persistent details
  panel. Service tiles use neutral geometric placeholder icons rather than
  third-party trademarks.

### 06 Service details

- Korean: `서비스 상세`, `호환 확인됨`, `업무`, `닫으면 이번 실행의 파일과 변경사항이 삭제됩니다.`, `Windows 환경`, `기본 환경`, `기술 세부정보`, `실행하기`, `서비스 상세정보 닫기`, `한국어`
- English: `Service details`, `Compatibility verified`, `Productivity`, `Files and changes from this session are deleted when you close it.`, `Windows environment`, `Default environment`, `Technical details`, `Launch`, `Close service details`, `English`
- Show the medium catalog behind a dim scrim with a 400 px dismissible details
  drawer aligned to the right.

### 07 Recovery

- Korean: `이전 작업 정리가 필요합니다`, `이전 Windows 환경의 안전한 정리가 끝날 때까지 새 서비스를 열거나 환경을 만들 수 없습니다.`, `기술 세부정보`, `복구 다시 시도`, `한국어`
- English: `Previous work needs cleanup`, `You cannot open a new service or create an environment until the previous Windows environment is safely cleaned up.`, `Technical details`, `Retry recovery`, `English`

### 08 Close confirmation

- Korean: `linuxcloth — 작업 중단 확인`, `진행 중인 작업을 중단할까요?`, `Windows 환경 만들기는 안전하게 중단하고 나중에 다시 시작할 수 있도록 현재 상태를 보존합니다.`, `계속 작업`, `안전하게 중단하고 닫기`, `한국어`
- English: `linuxcloth — Confirm stop`, `Stop the current task?`, `The current state is preserved so you can safely stop creating the Windows environment and resume later.`, `Keep working`, `Stop safely and close`, `English`
- Present a centered 480×220 modal over a dimmed setup-progress window. Keep
  the safe default action secondary and the explicit stop action primary.
