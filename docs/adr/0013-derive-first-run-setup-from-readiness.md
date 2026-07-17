# ADR-0013: Derive first-run setup from verified readiness

- Status: Accepted
- Date: 2026-07-16
- Superseded in part by: ADR-0015 and ADR-0016

## Decision

The desktop starts through one shell that owns a single `DesktopRuntime`. Before
showing the catalog it recovers stale sessions, verifies registered images,
runs Doctor, resolves the packaged GuestBridge and the distribution firmware
descriptor pair, and discovers durable image-build staging state. Routing to
recovery, first-run setup, environment repair, or the catalog is derived from
that snapshot. A persisted `setupComplete` flag is not authoritative.

The setup UI is a five-step task: host inspection, distribution components,
Windows media, virtio media, and image creation. The user can select only a
licensed Windows 11 x64 ISO and a Windows 11 amd64 virtio-win ISO. linuxcloth
requires its packaged GuestBridge and the exact QEMU descriptor-selected Q35
Secure Boot OVMF code/NVRAM-template pair; neither is a general user input.

Supported Debian- and Fedora-family package plans come directly from the
packaging dependency manifests. The desktop resolves and previews changes
through PackageKit, and PackageKit obtains per-transaction polkit authorization.
The desktop process remains unprivileged. If PackageKit is unavailable, the UI
only displays a fixed package-manager command for the user to copy and run in a
terminal; linuxcloth does not execute `sudo`, `pkexec`, `apt`, or `dnf`.

Media are inspected immediately after selection with the existing bounded,
Bubblewrap-confined xorriso validator and hashed before the next step is
enabled. A moving virtio `stable` URL is not downloaded automatically. Automatic
download remains disabled until a reviewed release ships an exact URL, size,
SHA-256, version, and notice manifest.

`setup-state.json` restores UI position and optionally the two local media
paths. It is private and atomically replaced, stores no credentials, and never
overrides Doctor, image verification, recovery results, or durable build state.

## Consequences

- Removing a host package, changing KVM access, updating OVMF, corrupting an
  image, or leaving an unresolved session reopens the appropriate repair flow.
- A resumable durable image build takes precedence over the last saved UI step.
- Missing `passt` does not block image creation or offline readiness, but the
  catalog cannot start an online service until the network prerequisite passes.
- Source and third-party builds must package the reviewed GuestBridge and
  dependency manifests or present an explicit repair error.
- Package installation is a privileged system change initiated only after the
  user reviews the PackageKit simulation and explicitly approves it.
