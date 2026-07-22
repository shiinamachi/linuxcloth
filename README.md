# linuxcloth

linuxcloth는 공식 TableCloth 카탈로그와 Spork 실행 계약을 사용해 Linux에서 일회용 Windows 11 VM으로 금융·공공 웹 서비스를 여는 데스크톱 앱입니다.

> **현재 상태:** 핵심 구성 요소와 Linux 데스크톱/CLI 흐름은 구현되어 있지만 아직 기술 프리뷰입니다. 실제 Windows 11 + KVM 환경에서 설치부터 은행 사이트 실행·종료까지 이어지는 end-to-end 검증은 완료되지 않았으므로 실제 금융 업무에 사용하지 마세요. 남은 보안 게이트는 [위협 모델](docs/threat-model.md)에 기록합니다.

## 구현된 흐름

- 공식 카탈로그 커밋과 `Catalog.xml`/이미지 트리 digest를 고정하고, 원본 XML을 재직렬화하지 않은 채 last-known-good 스냅샷으로 관리합니다.
- Avalonia 데스크톱에서 카탈로그 검색, 호스트 검사, 기준 이미지 생성·재개, 일회용 세션 실행을 제공합니다.
- CLI에서 `doctor`, `catalog`, `image build`, `image verify`, `cleanup`, `run`을 제공합니다.
- 사용자가 Windows 11 ISO와 라이선스를 확인하면 고정·검증된 virtio-win 미디어 준비, 무인 Windows 설치, GuestBridge 검증, 기준 qcow2 봉인을 한 흐름으로 진행하며 중단된 작업을 안전하게 재개합니다.
- 일반 세션은 검증된 기준 이미지에서 qcow2 overlay를 만들고, QEMU·`swtpm`·`passt`·overlay 생성용 `qemu-img`를 Bubblewrap 경계 안에서 실행합니다.
- Windows GuestBridge는 Spork `v1.20.5`의 고정된 Bootstrap을 크기, SHA-256, Authenticode 신뢰 체인, 서명자 인증서 fingerprint로 검증합니다.
- GuestBridge가 config를 검증하면 세션 UUID가 포함된 고정 형식 메시지를 virtio-serial로 보내며, 호스트는 일치하는 메시지를 받아야 세션을 `Running`으로 전환합니다.

## 핵심 원칙

- 공식 카탈로그 원본은 수정하지 않습니다.
- 카탈로그가 가리키는 설치 파일은 Linux 호스트에서 실행하지 않습니다.
- 일반 세션은 읽기 전용 기준 이미지와 세션별 qcow2 overlay를 사용합니다.
- 정상 종료 또는 소유권을 입증한 복구 뒤 overlay, UEFI 변수, TPM 상태, config disk와 socket을 정리합니다. 소유권이 불명확하면 증거를 보존합니다.
- 호스트 폴더, 클립보드, USB, 카메라, 마이크와 인바운드 네트워크는 기본적으로 차단합니다.
- Windows 미디어와 활성화된 기준 이미지는 배포하지 않습니다.

현재 `passt`는 인바운드 포워딩을 막지만 private/LAN·link-local·metadata 주소로 향하는 게스트 egress를 차단하지 않습니다. 또한 `remote-viewer`는 아직 Bubblewrap으로 격리되지 않고, GuestReady는 Spork 또는 사이트 실행 성공을 뜻하지 않으며, 호스트가 검증한 카탈로그와 Spork가 실제 사용하는 카탈로그의 동일성도 보장하지 않습니다. 이 제한들과 실제 Windows/KVM end-to-end 미검증 때문에 현재 빌드를 보안 완성 릴리스로 간주하면 안 됩니다.

## 개발 환경

- x86_64 Linux
- .NET SDK 10
- QEMU/KVM, Q35 Secure Boot OVMF, swtpm, Bubblewrap, virt-viewer
- 네트워크 세션용 `passt`, 설치 미디어 분석용 `7z`·`wimlib-imagex`, 이미지 생성용 `xorriso`
- Windows 11 x64 ISO와 유효한 라이선스(사용자 제공)
- 고정 virtio-win ISO를 처음 받을 네트워크 연결 또는 Windows 11 amd64 virtio-win ISO(선택적 로컬 대체 파일)

처음 체크아웃한 뒤 공식 카탈로그 submodule을 가져옵니다.

```bash
git submodule update --init --recursive
```

빌드와 테스트:

```bash
dotnet restore linuxcloth.slnx
dotnet build linuxcloth.slnx --no-restore
dotnet test linuxcloth.slnx --no-build --no-restore
```

개발 빌드에서 CLI와 데스크톱을 시작할 수 있습니다.

```bash
dotnet run --project src/LinuxCloth.Cli -- doctor
dotnet run --project src/LinuxCloth.Desktop
```

배포용 staging tree와 DEB/RPM 생성 방법, 기준 이미지 생성 및 CLI 예시는 [운영 가이드](docs/operations.md)에 있습니다. 개발 규칙은 [AGENTS.md](AGENTS.md), 주요 기술 결정은 [ADR 목록](docs/adr/README.md), 공식 데이터 계약은 [카탈로그 계약](docs/catalog-contract.md)과 [Express WSB 계약](docs/wsb-contract.md)에서 확인할 수 있습니다.

## 라이선스

linuxcloth 소스 코드는 별도 표기가 없는 한 GNU Affero General Public License v3.0 이상 조건으로 제공됩니다. 공식 TableCloth 카탈로그와 기타 제3자 구성 요소에는 각각의 라이선스가 적용됩니다.
