# Express WSB 계약

linuxcloth는 공식 TableCloth의 2026-07-05 간소화 Express 계약을 사용합니다. 카탈로그에서 검증된 `ServiceId`만 공백 구분 값으로 `TABLECLOTH_SITE_IDS`에 전달하며, 임의 URL이나 임의 PowerShell 명령을 받지 않습니다.

일반 모드가 허용하는 `LogonCommand`는 공식 고정 명령과 정확히 일치해야 합니다. `MappedFolders`는 읽기 전용이어도 거부합니다. 네트워크·클립보드·메모리 정책은 XML에 명시하며, 생성된 WSB와 `launch.json`의 서비스 및 보안 플래그가 다르면 config 디렉터리를 공개하지 않습니다.

`launch.json`, SHA-256 sidecar, 정규화된 `express.wsb`, 선택적 공식 `Catalog.xml`은 같은 부모 디렉터리에서 완전히 기록·검증한 뒤 원자적으로 게시합니다. `Catalog.xml`은 재직렬화하지 않고 원본 바이트를 복사합니다.

## GuestBridge 적용 범위

GuestBridge는 준비된 드라이브를 검색해 정확히 하나의 유효한 manifest만 허용합니다. manifest와 sidecar는 크기를 제한하고 고정 시간 비교로 확인하며, `Catalog.xml`이 있으면 manifest의 카탈로그 SHA-256과 일치해야 합니다. 여러 유효 후보가 있거나 reparse point인 파일은 거부합니다.

이 SHA-256 sidecar는 같은 config 디스크 안의 우발적 손상이나 불일치를 검출하지만 서명은 아닙니다. 공격자가 config 디스크 전체를 바꿀 수 있다면 manifest와 sidecar를 함께 바꿀 수 있습니다. config 디스크 생성 주체와 세션 디렉터리를 호스트의 신뢰 경계 안에 두어야 합니다.

현재 GuestBridge는 서비스 ID를 환경 변수로 넣고 고정된 공식 HTTPS URL의 최신 PowerShell 준비 스크립트를 실행합니다. 임의 URL이나 명령을 manifest에서 받지는 않지만, 다운로드한 내용의 버전·해시·게시자 서명을 아직 검증하지 않습니다. 고정 버전의 서명된 SporkBootstrap 검증이 완료되기 전에는 공급망 인증이 완성된 것으로 간주하지 않습니다. 또한 virtio-serial 상태 채널은 QEMU에 생성되어 있으나 GuestBridge 진행 상태 전송은 아직 구현되지 않았습니다.
