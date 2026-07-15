# 공식 카탈로그 계약

linuxcloth는 `vendor/TableClothCatalog`의 upstream 커밋 `7e3e6a8f54d5e273dad61667024e372cc2958dd9`를 고정합니다. 빌드에 포함된 `Catalog.xml`과 이미지 트리를 바이트 단위 원본으로 보존하며, 검색 모델과 Linux 호환성 정보는 파생 데이터로만 관리하고 원본 XML을 다시 쓰지 않습니다.

파서는 DTD와 외부 엔터티를 금지하고 16 MiB 제한, 서비스 ID 형식, HTTP(S) URL, 카테고리를 검증합니다. 기본 중복 ID 정책은 `Reject`입니다. 다만 현재 고정된 공식 커밋 `7e3e6a8`에는 동일한 `PayInfo` ID가 두 번 있어, 공식 스냅샷을 읽을 때만 `KeepFirst` 정책을 명시적으로 선택하고 진단 항목을 남깁니다. 이 정책은 원본 파일을 수정하지 않으면서 나머지 카탈로그를 사용할 수 있게 하며, 일반 입력의 중복 허용으로 확장되지 않습니다.

고정 bundle은 다음 값을 코드와 함께 검토합니다.

- `Catalog.xml` SHA-256: `6198D7F3ABB6744991D0A1A2400E75F1E8A588470EF9AB765B8B11354C3F968A`
- 이미지 트리 SHA-256: `C21D6D6E6C1CFE791DF913F497D1F0112D5BB669CD5355AF6D491ED4AC5CFC4A`

스냅샷 manifest는 원본 `Catalog.xml`의 SHA-256과 upstream 커밋을 기록합니다. 이미지 저장소도 같은 카탈로그 provenance에 결합하며 파일 수·개별 크기·전체 크기·경로를 제한하고 symbolic link를 거부합니다. 새 bundle은 XML digest와 이미지 트리 digest를 모두 검증한 뒤 원자적으로 승격하고, 손상된 현재 스냅샷은 이전 정상 스냅샷 포인터를 덮어쓰지 않습니다. CLI와 데스크톱 시작 시에는 설치물에 포함된 고정 bundle이 현재 last-known-good와 다를 때만 이 검증·승격 절차를 수행합니다.

## 신뢰와 무결성의 구분

현재 digest는 저장 후 변경 여부와 코드에 고정한 bundle과의 일치를 검출하지만 upstream 신원을 증명하는 디지털 서명은 아닙니다. 따라서 실행 파일과 패키지 자체의 신뢰할 수 있는 서명·provenance가 별도로 필요합니다.

저수준 `CatalogSnapshotUpdater`는 HTTP와 HTTPS를 모두 받을 수 있지만 CLI와 데스크톱의 공식 카탈로그 갱신 경로에는 연결되어 있지 않습니다. 제품은 현재 설치물에 포함된 고정 bundle만 자동 승격합니다. 향후 네트워크 갱신을 연결하려면 공식 HTTPS origin 제한, 예상 커밋 또는 서명된 manifest 검증, XML과 이미지/고지를 묶은 원자적 교체가 모두 필요합니다. 그 전에는 네트워크에서 받은 스냅샷을 공식 업데이트로 표시하지 않습니다.

카탈로그 안의 패키지 URL, 설치 인수, 확장 프로그램, 사용자 지정 부트스트랩은 실행 가능한 비신뢰 데이터입니다. Linux 호스트는 이를 다운로드하거나 실행하지 않고, 검증된 서비스 ID와 원본 카탈로그 바이트만 일회용 Windows 게스트에 전달합니다.

일반 세션의 config에는 검증한 `Catalog.xml`과 digest를 함께 넣고 GuestBridge가 둘의 일치를 확인합니다. 그러나 현재 고정 SporkBootstrap 인수는 Spork zip URL/hash와 서비스 ID만 받으며, 검증한 catalog 파일 경로나 digest를 Spork에 전달하지 않습니다. 따라서 호스트 UI와 Spork가 정확히 같은 카탈로그 바이트를 사용했다고 보장할 수 없습니다. upstream에 `--catalog-file`/`--catalog-sha256` 같은 후방 호환 계약이 추가되거나 동등하게 검증된 통합이 생기기 전에는 이 동일성을 주장하지 않습니다.

현재 스냅샷 저장소는 `Catalog.xml`, 결합된 PNG 이미지 asset, manifest와 `current`/`previous` 포인터를 관리합니다. 배포물에는 upstream `sites.xml`과 라이선스도 포함되지만, 런타임 저장소가 이 둘을 `Catalog.xml`/이미지와 함께 하나의 서명된 원자적 스냅샷으로 갱신하는 기능은 아직 구현되지 않았습니다.
