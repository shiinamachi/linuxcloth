# Express WSB 계약

linuxcloth는 공식 TableCloth의 2026-07-05 간소화 Express 계약을 사용합니다. 카탈로그에서 검증된 `ServiceId`만 공백 구분 값으로 `TABLECLOTH_SITE_IDS`에 전달하며, 임의 URL이나 임의 PowerShell 명령을 받지 않습니다.

일반 모드가 허용하는 `LogonCommand`는 공식 고정 명령과 정확히 일치해야 합니다. `MappedFolders`는 읽기 전용이어도 거부합니다. 네트워크·클립보드·메모리 정책은 XML에 명시하며, 생성된 WSB와 `launch.json`의 서비스 및 보안 플래그가 다르면 config 디렉터리를 공개하지 않습니다.

`launch.json`, SHA-256 sidecar, 정규화된 `express.wsb`, 선택적 공식 `Catalog.xml`은 같은 부모 디렉터리에서 완전히 기록·검증한 뒤 원자적으로 게시합니다. `Catalog.xml`은 재직렬화하지 않고 원본 바이트를 복사합니다.
