# Operations guide

linuxcloth is a technical preview with connected CLI and Avalonia desktop entry
points. Do not deploy it for financial activity until the release blockers in
`threat-model.md` and a real Windows 11/KVM end-to-end test are complete.

## Host capabilities

The supported target is x86_64 Linux with:

- CPU virtualization enabled and read/write access to `/dev/kvm`;
- `qemu-system-x86_64`, `qemu-img`, Q35-compatible Secure Boot OVMF descriptors,
  `swtpm`, Bubblewrap, and `remote-viewer`;
- `passt` for network-enabled sessions and `xorriso` while building an image;
- unprivileged user namespaces or the distribution's supported Bubblewrap setup;
- enough memory and storage for Windows 11 and a sparse base image;
- a user-provided Windows 11 x64 ISO and virtio-win ISO while building an image.

linuxcloth does not require libvirt, a TAP interface, a bridge, root execution,
or a setuid application binary. Never work around `/dev/kvm` access by running the
whole desktop application as root.

Package names are distribution-specific. The reviewed Debian-family and
Fedora-family mappings live under `packaging/`. The current host probe checks
platform, KVM access, executable presence, firmware descriptors, and runtime
socket capability; release testing must additionally execute the exact QEMU,
Bubblewrap, passt, swtpm, and viewer features because package contents vary.

## Build and package entry points

Initialize the official catalog submodule before a source build, then restore,
build, and test the locked solution.

```text
git submodule update --init --recursive
dotnet restore linuxcloth.slnx --locked-mode
dotnet build linuxcloth.slnx --no-restore
dotnet test linuxcloth.slnx --no-build --no-restore
```

The release staging pipeline publishes self-contained `linux-x64` CLI and
desktop apphosts plus a single-file `win-x64` GuestBridge. Build and validate it
from a clean x86_64 checkout:

```text
eng/publish-linux.sh --output artifacts/stage --version 0.1.0
eng/validate-package-tree.sh --root artifacts/stage --strict-tools
eng/package-deb.sh --stage artifacts/stage --output artifacts/packages
eng/package-rpm.sh --stage artifacts/stage --output artifacts/packages
```

On a cross-distribution build host, replace the last two commands with
`eng/package-in-containers.sh --stage artifacts/stage --output artifacts/packages`;
it uses the digest-pinned Debian 12 and Fedora 44 builders used by CI.

The installed host payload lives below `/usr/lib/linuxcloth`; `/usr/bin/linuxcloth`
and `/usr/bin/linuxcloth-desktop` are relative symbolic links to the two apphosts.
GuestBridge is installed at
`/usr/lib/linuxcloth/guest/linuxcloth-guest-bridge.exe`. The AppStream component
and icon use ID `io.github.shiinamachi.linuxcloth`; the desktop launcher ID is
`io.github.shiinamachi.linuxcloth.desktop`.

Package-tree validation rejects Windows media, generated disk images, keys,
OVMF variable/TPM state, unexpected Windows executables, non-normalized or
special file modes, and escaping symbolic links. It verifies a SHA-256 manifest
covering every regular payload file except the manifest itself and can
also run `file`, `desktop-file-validate`, and `appstreamcli` in strict mode. DEB
and RPM packaging has no installation script and performs no download, VM
creation, group modification, or privilege grant.

Build each release-family package in its oldest supported distribution because
self-contained .NET does not remove glibc and native desktop ABI dependencies.
CI installs and smoke-tests the finished packages in digest-pinned Debian 12 and
Fedora 44 containers, generates SPDX and CycloneDX SBOMs, and moves OIDC signing
authority into a separate non-PR attestation job. These attestations do not
replace an operator-managed DEB/RPM repository or package signing key.

## Managed data

| Data | Default location | Backup policy |
|---|---|---|
| Configuration | `$XDG_CONFIG_HOME/linuxcloth` | Optional; no credentials should be stored. |
| Catalog and images | `$XDG_DATA_HOME/linuxcloth` | Back up sealed images only while no session/build is active. |
| Download/cache data | `$XDG_CACHE_HOME/linuxcloth` | Do not back up; safe to recreate. |
| Active sessions | `$XDG_RUNTIME_DIR/linuxcloth/sessions` | Never back up or synchronize. |

When `XDG_RUNTIME_DIR` is absent, linuxcloth uses a per-UID directory below the
system temporary directory. Managed directories are mode 0700 and symbolic-link
application directories are rejected.

## Image lifecycle

The operator supplies licensed Windows installation media and a Windows 11
amd64 virtio-win ISO. In the desktop app choose **기준 이미지 준비**;
the dialog detects the exact Q35 Secure Boot OVMF code/variables pair, accepts
the two ISOs and GuestBridge path, and supports safe cancellation and resume
from a preserved staging directory. The equivalent CLI flow is:

```text
linuxcloth doctor
linuxcloth image build start windows-11 \
  --windows-iso /absolute/path/Windows11.iso \
  --virtio-win-iso /absolute/path/virtio-win.iso \
  --guest-bridge /usr/lib/linuxcloth/guest/linuxcloth-guest-bridge.exe
```

The image builder validates bounded ISO contents with confined `xorriso`, then
opens an interactive SPICE Windows installer. Select the desired Windows edition
and blank virtio disk; load the Windows 11 amd64 `vioscsi` driver from the
virtio-win media if Setup does not show the disk. The generated answer file
creates a unique local administrator, and first logon verifies and installs the
pinned GuestBridge plus virtio drivers before shutting down.

linuxcloth then boots the base without installation media and supplies a
one-time nonce/hash probe. The image is promoted only when GuestBridge reports
the expected executable hash and Windows architecture/build/edition provenance,
then requests shutdown. A cancellation or recoverable failure preserves the
staging directory. For CLI recovery use `image build recover` before `resume` if
the durable state says an installer or verification process was active.

Build the sparse `base.qcow2` directly at the registry staging path; copying it
through a normal file API can inflate it. Registration also requires an OVMF
variables template, a bounded swtpm state template, and the system OVMF code
path. Promotion hashes all artifacts and atomically seals the image.

Run full registry verification before launch. A distribution OVMF update changes
the tracked external firmware file and should make verification fail; review the
firmware update and rebuild or deliberately register a new image rather than
editing metadata.

Base maintenance must use a separate maintenance workflow and publish a new
registered image. Normal overlays must never be committed into a base.

Verify a registered image before investigating launch behavior:

```text
linuxcloth image list
linuxcloth image verify windows-11
```

Search the pinned catalog and launch a disposable session with a registered
image:

```text
linuxcloth catalog search "우리은행"
linuxcloth run WooriBank --image windows-11
```

Networking is enabled by default because GuestBridge and Spork need the pinned
release and service endpoints. `--no-network` is available for offline diagnosis;
`--enable-clipboard` is an explicit per-session opt-in and prints a warning.
Until the egress broker is implemented, a network-enabled guest can reach the
host's private/LAN, link-local, and metadata destinations.

## Session lifecycle and recovery

The intended application order is:

1. recover or preserve stale session journals;
2. verify host capabilities, selected image, catalog trust, and service IDs;
3. create the overlay and copy OVMF/TPM state;
4. atomically stage the read-only guest config;
5. start swtpm and optional passt, then confined QEMU and the viewer;
6. wait for a fixed-format virtio-serial GuestReady message containing the exact
   current session UUID;
7. on close, request guest shutdown, stop sidecars, prove process exit, and delete
   the session directory.

Both desktop startup and CLI `run` perform step 1 and refuse a new launch when a
previous session cannot be recovered safely. `linuxcloth cleanup` exposes the
same recovery pass as a manual diagnostic and retry path.

`Running` means GuestBridge validated the config and reached the point just
before launching the pinned Bootstrap. It does not mean that Bootstrap/Spork
finished or the requested website opened. The default readiness timeout is
three minutes; mismatch, malformed input, timeout, and early process exit fail
the start and trigger conservative cleanup.

Normal-session QEMU, `swtpm`, `passt`, and the generated overlay-only `qemu-img`
command run through Bubblewrap. The `passt` sandbox deliberately shares the host
network namespace to provide outbound connectivity; QEMU, `swtpm`, and
`qemu-img` do not. `remote-viewer` remains outside Bubblewrap because its desktop
socket/DBus boundary has not yet been implemented.

GuestBridge downloads only the Spork `v1.20.5` Bootstrap from the pinned release
path, permits redirects only to GitHub release assets, verifies exact size and
SHA-256, uses Windows trust validation with revocation and a pinned signer
certificate fingerprint, and then starts it with pinned Spork zip hashes. A pin
update must change and review all values together.

Do not recursively delete a stale session merely because its UI is gone. Recovery
must validate the journal's boot ID, PID, start ticks, and executable before it
signals a process. Invalid, mismatched, or ambiguous records are intentionally
preserved. Inspect them offline and retain the process logs and `session.json`
until ownership is established.

A crash can occur after a sidecar starts but before its identity is durably
journaled. An ambiguous `StartingNetwork` record then cannot be proven safe to
signal or delete, so desktop and CLI launches remain blocked even after repeated
automatic/manual recovery. Do not bypass this by deleting the directory while a
process may still be alive. Stable release requires a supervisor/cgroup or
pre-start process lease that closes this ownership window.

Session file deletion is logical cleanup, not forensic erasure. Use encrypted
host storage when residual blocks or snapshots are in the threat model.

## Diagnostics

Runtime stdout/stderr files may contain guest, package, path, or network details.
Keep them inside the private runtime directory, do not upload them automatically,
and inspect/redact them before sharing. Never collect user credentials, browser
profiles, certificates, or full URLs with sensitive query strings.

Before reporting a launch defect, record distribution and kernel version, QEMU
and OVMF package versions, KVM availability, image metadata (without media),
catalog commit/hash, and the recovery disposition. Do not attach the Windows
image, overlay, TPM state, or config disk.

## Current deployment gates

- No AppArmor or SELinux policy is claimed as supported.
- Network-enabled sessions do not yet enforce a private/LAN destination deny
  policy.
- `remote-viewer` is not confined and still receives the desktop environment.
- The session-bound GuestReady message is a readiness signal, not cryptographic
  guest attestation or proof that Spork/site launch succeeded.
- The verified config catalog is not passed to Bootstrap/Spork, so host UI and
  guest execution cannot yet be proven to use identical catalog bytes.
- A crash before a newly started process identity is journaled can leave a
  permanently ambiguous session that fail-closed recovery cannot remove.
- Image creation still requires interactive Windows edition/disk selection; it
  is not the originally proposed unattended WinPE/DISM pipeline.
- The bundled catalog is digest-pinned, but network catalog updates remain
  disabled and packages still require release-operator signing.
- A Windows 11 KVM end-to-end run has not yet been recorded.
