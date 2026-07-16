# Desktop copy glossary

| Internal concept | Default user term | Technical details only |
|---|---|---|
| base image | Windows 환경 | image ID, qcow2 paths |
| KVM | 가상화 | KVM, `/dev/kvm` |
| QEMU | Windows 실행 | QEMU executable and arguments |
| OVMF | Windows 시작 | Secure Boot OVMF descriptor |
| SPICE / remote-viewer | Windows 화면 | SPICE and viewer diagnostics |
| GuestBridge | Windows 연결 | GuestBridge path and version |
| virtio-win | Windows 장치 드라이버 | vioscsi, NetKVM, hashes |
| disposable overlay | 이번 실행의 Windows 환경 | qcow2 overlay path |
| stop session | 닫고 삭제 | cleanup diagnostics |

Use `서비스 열기` for the primary catalog action and `Windows 환경 만들기`
for setup. Never shorten a destructive action to a neutral `닫기` when the
operation deletes the current environment's changes.
