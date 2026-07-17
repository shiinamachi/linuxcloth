# ADR-0016: Pin and verify the setup virtio artifact

- Status: Accepted
- Date: 2026-07-17
- Supersedes: the setup UI and virtio media decisions in ADR-0013

## Decision

The desktop setup flow requires only a user-provided, appropriately licensed
Windows 11 x64 ISO. It obtains the Windows 11 amd64 virtio-win ISO from a
release-bundled manifest unless the user explicitly selects a local fallback.
The manifest pins an immutable Fedora People archive URL, version, exact byte
length, SHA-256 digest, and required `vioscsi` and `NetKVM` paths. Mutable
`latest` and `stable` URLs are rejected.

The downloader disables automatic redirects and follows at most three redirects
itself. Every target must remain HTTPS on the explicit Fedora People allowlist.
It streams into a mode-0600 temporary file below the private XDG cache, enforces
the exact length while reading, verifies SHA-256, and only then atomically moves
the file into the versioned cache. Invalid cached or partial files are removed.
The existing Bubblewrap-confined ISO validator still inspects required driver
paths before an image build, and the image builder independently revalidates the
media at its trust boundary.

The automatic download starts only after the user chooses **Windows 환경
준비하기**. A cached artifact is verified and reused. Network or verification
failure leaves setup in a retryable blocked state with the local ISO picker as
the recovery action.

## Consequences

- The normal desktop flow has one file input and one primary preparation action.
- Release review must update the immutable URL, version, exact length, SHA-256,
  required paths, tests, and this decision together.
- The cache is reproducible and disposable; it is not part of image state or a
  readiness authority.
- Source and packaged desktop builds must include `setup-artifacts/virtio-win.json`.
- The CLI continues to require an explicit local virtio-win ISO for expert and
  offline automation.
