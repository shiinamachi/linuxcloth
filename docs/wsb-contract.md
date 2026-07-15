# Express WSB 계약

linuxcloth는 공식 TableCloth의 2026-07-05 간소화 Express 계약을 사용합니다. 카탈로그에서 검증된 서로 다른 `ServiceId` 1~32개만 공백 구분 값으로 고정된 `--site-ids` 인수에 전달하며, 임의 URL이나 임의 PowerShell 명령을 받지 않습니다. 호환 WSB는 검증된 ID로만 `$siteIds` literal을 생성하고, 일반 Linux VM 경로의 GuestBridge는 `launch.json`의 typed ID 목록에서 같은 인수 배열을 만듭니다.

일반 모드가 허용하는 `LogonCommand`는 공식 고정 명령과 정확히 일치해야 합니다. `MappedFolders`는 읽기 전용이어도 거부합니다. 네트워크·클립보드·메모리 정책은 XML에 명시하며, 생성된 WSB와 `launch.json`의 서비스 및 보안 플래그가 다르면 config 디렉터리를 공개하지 않습니다.

`launch.json`, SHA-256 sidecar, 정규화된 `express.wsb`, 선택적 공식 `Catalog.xml`은 같은 부모 디렉터리에서 완전히 기록·검증한 뒤 원자적으로 게시합니다. `Catalog.xml`은 재직렬화하지 않고 원본 바이트를 복사합니다.

## GuestBridge 적용 범위

GuestBridge는 준비된 드라이브를 검색해 정확히 하나의 유효한 manifest만 허용합니다. manifest와 sidecar는 크기를 제한하고 고정 시간 비교로 확인하며, `Catalog.xml`이 있으면 manifest의 카탈로그 SHA-256과 일치해야 합니다. 여러 유효 후보가 있거나 reparse point인 파일은 거부합니다.

이 SHA-256 sidecar는 같은 config 디스크 안의 우발적 손상이나 불일치를 검출하지만 서명은 아닙니다. 공격자가 config 디스크 전체를 바꿀 수 있다면 manifest와 sidecar를 함께 바꿀 수 있습니다. config 디스크 생성 주체와 세션 디렉터리를 호스트의 신뢰 경계 안에 두어야 합니다.

호스트의 일반 세션은 검증된 `Catalog.xml`을 항상 포함하지만, GuestBridge가 이를 확인한 뒤 Bootstrap에 전달하는 값은 서비스 ID와 고정 Spork artifact 정보뿐입니다. 현재 upstream Bootstrap/Spork 계약에는 검증한 catalog 경로나 digest 인수가 없으므로, Spork가 호스트 UI와 동일한 catalog snapshot을 사용했다고 증명할 수 없습니다.

## 고정 Spork 릴리스

현재 허용하는 릴리스는 Spork `v1.20.5` 하나입니다. `SporkBootstrap_1.20.5.0_Release_x64.exe`의 공식 GitHub release URL, 정확한 크기, SHA-256, 서명자 인증서 DER SHA-256을 코드에 함께 고정합니다. Bootstrap에는 x64/arm64 Spork portable zip의 고정 URL template과 각 SHA-256 map을 인수 배열로 전달합니다. `latest` endpoint, `Invoke-Expression`, manifest가 제공한 다운로드 URL은 사용하지 않습니다.

- Bootstrap 크기: `5,185,888` byte
- Bootstrap URL: `https://github.com/yourtablecloth/TableCloth/releases/download/v1.20.5/SporkBootstrap_1.20.5.0_Release_x64.exe`
- Bootstrap SHA-256: `AD953BBBECE1D2E72898164DA2E5D152A15D2E1EBBAF330A089AA1E8775CC498`
- 서명자 인증서 DER SHA-256: `892C4996A8E6AD504275B228C04269B708D98455BBBB86202BEF073E9A8D320A`
- Spork zip URL template: `https://github.com/yourtablecloth/TableCloth/releases/download/v1.20.5/Spork_1.20.5.0_Release_{arch}_Portable.zip`
- Spork x64 zip SHA-256: `F8E8FE7DFDCCB7CFFD971CF153C5C83C848A6B1ECC39F13BDE5895702CF156AF`
- Spork arm64 zip SHA-256: `D61B2BF93D11711E592C4ADF7528C0CE4D690A4F561783E26884577F98C60351`

생성된 호환 WSB의 PowerShell 명령도 다운로드 뒤 크기, SHA-256, 유효한 Authenticode 서명과 서명자 인증서 fingerprint를 모두 확인해야 실행합니다. 일반 Linux VM 흐름에서 실제 실행을 담당하는 GuestBridge는 다음의 더 엄격한 절차를 사용합니다.

1. 자동 redirect를 끄고 고정 `https://github.com/yourtablecloth/TableCloth/releases/download/…` URL에서 요청합니다.
2. 기본 HTTPS 포트만 허용하며 redirect는 최대 두 번, `release-assets.githubusercontent.com`만 허용합니다.
3. HTTP 200, 선택적 `Content-Length`, 실제 byte 수와 SHA-256을 고정 값과 비교합니다.
4. 검증된 파일을 read-only lease로 잠근 상태에서 Windows `WinVerifyTrust`로 전체 체인과 revocation을 검사하고 서명자 인증서 fingerprint를 고정 값과 비교합니다.
5. 검증을 모두 통과한 실행 파일만 `ProcessStartInfo.ArgumentList`로 실행하고 임시 디렉터리를 정리합니다.

릴리스를 갱신하려면 Bootstrap과 Spork zip의 모든 URL·크기·digest·서명자 값을 함께 검토하고 코드 및 테스트를 변경해야 합니다. 이 검증은 고정 artifact의 공급망 위험을 줄이지만, linuxcloth 배포 패키지 자체의 서명이나 GitHub 계정 침해에 대한 독립적인 transparency log를 대신하지 않습니다.

## GuestReady 채널

GuestBridge는 config를 하나로 확정하고 manifest를 검증한 직후 Windows virtio-serial 장치 `\\.\org.linuxcloth.guestbridge.0`에 다음 한 줄을 기록합니다.

```text
linuxcloth-ready-v1 <32자리 소문자 session UUID>\n
```

호스트는 세션 디렉터리의 Unix socket에서 최대 64 byte만 읽고, 고정 prefix·개행·UUID 형식과 현재 세션 UUID가 모두 일치해야 `Running` 상태로 전환합니다. 기본 대기 제한은 3분이며 다른 세션 ID, 잘못된 형식, 조기 종료와 timeout은 시작 실패로 처리한 뒤 소유 프로세스와 세션 artifact를 정리합니다.

이 메시지는 **GuestBridge가 config를 검증하고 Bootstrap 실행 직전까지 도달했다는 준비 신호**입니다. Bootstrap 또는 Spork 완료와 사이트가 열렸다는 확인이 아니며, 별도 비밀값이나 서명이 없는 세션 결합 신호이므로 암호학적으로 인증된 상태 채널이라고 부르지 않습니다.
