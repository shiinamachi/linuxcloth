# Operations guide

linuxcloth is currently a component-level implementation. Do not deploy it for
financial activity until the release blockers in `threat-model.md` and the
Windows end-to-end test are complete.

## Host capabilities

The supported target is x86_64 Linux with:

- CPU virtualization enabled and read/write access to `/dev/kvm`;
- `qemu-system-x86_64`, `qemu-img`, Q35-compatible Secure Boot OVMF descriptors,
  `swtpm`, `passt`, Bubblewrap, and `remote-viewer`;
- unprivileged user namespaces or the distribution's supported Bubblewrap setup;
- enough memory and storage for Windows 11 and a sparse base image;
- `wimlib` and `xorriso` only while building a Windows image.

linuxcloth does not require libvirt, a TAP interface, a bridge, root execution,
or a setuid application binary. Never work around `/dev/kvm` access by running the
whole desktop application as root.

Package names are distribution-specific. The reviewed Debian-family and
Fedora-family mappings live under `packaging/`. The current host probe checks
platform, KVM access, executable presence, firmware descriptors, and runtime
socket capability; release testing must additionally execute the exact QEMU,
Bubblewrap, passt, swtpm, and viewer features because package contents vary.

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

The operator supplies licensed Windows installation media. Build the sparse
`base.qcow2` directly at the registry staging path; copying it through a normal
file API can inflate it. Registration also requires an OVMF variables template,
a bounded swtpm state template, and the system OVMF code path. Promotion hashes
all artifacts and atomically seals the image.

Run full registry verification before launch. A distribution OVMF update changes
the tracked external firmware file and should make verification fail; review the
firmware update and rebuild or deliberately register a new image rather than
editing metadata.

Base maintenance must use a separate maintenance workflow and publish a new
registered image. Normal overlays must never be committed into a base.

## Session lifecycle and recovery

The intended application order is:

1. recover or preserve stale session journals;
2. verify host capabilities, selected image, catalog trust, and service IDs;
3. create the overlay and copy OVMF/TPM state;
4. atomically stage the read-only guest config;
5. start swtpm and optional passt, then confined QEMU and the viewer;
6. on close, request guest shutdown, stop sidecars, prove process exit, and delete
   the session directory.

Do not recursively delete a stale session merely because its UI is gone. Recovery
must validate the journal's boot ID, PID, start ticks, and executable before it
signals a process. Invalid, mismatched, or ambiguous records are intentionally
preserved. Inspect them offline and retain the process logs and `session.json`
until ownership is established.

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

- No CLI or desktop packaging entry point is declared in `packaging/` until the
  executable publish layout is finalized.
- No AppArmor or SELinux policy is claimed as supported.
- Network-enabled sessions do not yet enforce a private/LAN destination deny
  policy.
- Bootstrap and catalog authenticity policy is incomplete.
- A Windows 11 KVM end-to-end run has not yet been recorded.
