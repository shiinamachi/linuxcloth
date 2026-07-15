# linuxcloth

linuxcloth는 공식 TableCloth 카탈로그와 Spork 실행 계약을 사용해 Linux에서 일회용 Windows 11 VM으로 금융·공공 웹 서비스를 여는 데스크톱 앱입니다.

## 핵심 원칙

- 공식 카탈로그 원본은 수정하지 않습니다.
- 카탈로그가 가리키는 설치 파일은 Linux 호스트에서 실행하지 않습니다.
- 일반 세션은 읽기 전용 기준 이미지와 세션별 qcow2 overlay를 사용합니다.
- VM 종료 후 overlay, UEFI 변수, TPM 상태, config disk와 socket을 정리합니다.
- 호스트 폴더, 클립보드, USB, 카메라, 마이크와 인바운드 네트워크는 기본적으로 차단합니다.
- Windows 미디어와 활성화된 기준 이미지는 배포하지 않습니다.

## 개발 환경

- x86_64 Linux
- .NET SDK 10
- QEMU/KVM, OVMF, swtpm, passt, virt-viewer
- Windows 11 x64 ISO와 유효한 라이선스(사용자 제공)

```bash
dotnet restore linuxcloth.slnx
dotnet build linuxcloth.slnx --no-restore
dotnet test linuxcloth.slnx --no-build --no-restore
```

구체적인 설치 및 실행 방법은 구현과 함께 `docs/`에 유지합니다. 개발 규칙은 [AGENTS.md](AGENTS.md), 주요 기술 결정은 [docs/adr](docs/adr)에서 확인할 수 있습니다.

## 라이선스

linuxcloth 소스 코드는 별도 표기가 없는 한 GNU Affero General Public License v3.0 이상 조건으로 제공됩니다. 공식 TableCloth 카탈로그와 기타 제3자 구성 요소에는 각각의 라이선스가 적용됩니다.

