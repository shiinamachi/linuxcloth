# linuxcloth threat model

This document describes the controls present in the repository, not the intended
end state alone. It must be updated when launch orchestration or trust policy
changes.

## Assets

- host files, browser profiles, SSH keys, credentials, and local services;
- Windows session credentials, cookies, certificates, and installed packages;
- the integrity of registered Windows base images and per-image machine identity;
- catalog service identity and the guest bootstrap code selected for execution;
- ownership of QEMU, swtpm, passt, and viewer processes during crash recovery.

## Trust boundaries

The Linux kernel, KVM, the signed distribution, linuxcloth host code, and the
selected system QEMU/Bubblewrap/OVMF packages are trusted. The Windows guest,
bank security software, websites, catalog package URLs, downloaded bootstrap
code, and imported WSB files are untrusted.

The logged-in user is allowed to configure and delete their own linuxcloth data.
A compromised host kernel, host root account, malicious distribution package,
firmware attack, and physical disk forensics are outside the current boundary.
VM-detection evasion and Windows licensing bypasses are explicitly out of scope.

## Implemented controls

| Area | Current control |
|---|---|
| QEMU filesystem | Bubblewrap exposes only system runtime, `/dev/kvm`, exact read-only base/OVMF files, and the writable session directory; the environment and home directory are hidden. |
| QEMU networking | QEMU has a private network namespace and reaches a host-side `passt` socket. Guest-facing TCP/UDP forwards and gateway remapping are disabled. |
| VM storage | Normal sessions use a qcow2 backing overlay plus copied OVMF variables and swtpm state. Registered base assets are hashed, bounded, symlink-rejected, and sealed read-only. |
| Display/control | SPICE and QMP use Unix sockets inside the private session directory. SPICE file transfer is disabled and clipboard is disabled by default. RDP is not implemented. |
| Host integration | Generated normal WSB rejects mapped folders; QEMU exposes only the generated read-only config disk. Audio, host folder, USB redirection, microphone, camera, and printer integration are not configured. |
| Catalog parsing | XML DTDs and external resolution are disabled; size, identifier, category, and URL fields are bounded and validated. Snapshots retain original bytes and content hashes. |
| Guest request | Normal WSB accepts only the fixed Express command and validated service IDs. GuestBridge requires exactly one valid bounded manifest and checks its catalog digest when a catalog is present. |
| Process execution | Host and guest process arguments are passed as arrays. User data is not concatenated into a host shell command. |
| Crash recovery | A durable journal records boot-aware process identities. Recovery validates identity before QMP or signals and preserves ambiguous sessions. |

## Important limitations and required controls

1. **Bootstrap authenticity:** GuestBridge currently downloads the release
   `latest` PowerShell script from a fixed HTTPS URL and executes it. There is no
   pinned version, content digest, or publisher-signature verification. The
   manifest hash is an integrity check for files on one config disk, not an
   authenticity signature. A pinned and verified SporkBootstrap artifact is a
   release blocker.
2. **Catalog authenticity:** snapshots record SHA-256 and upstream metadata, but
   they are not signed. The lower-level updater accepts HTTP as well as HTTPS.
   Product integration must use HTTPS, constrain the official origin, verify an
   expected commit or signed manifest, and retain a last-known-good snapshot.
3. **LAN egress:** the current `passt` options prevent inbound forwarding but do
   not deny guest access to private ranges, host interface addresses, link-local
   services, or cloud metadata. A policy-enforcing egress broker and tests are
   required before claiming local-network isolation.
4. **Sidecar confinement:** `swtpm`, `passt`, `remote-viewer`, and `qemu-img` run
   as the desktop user outside QEMU's Bubblewrap namespace. They receive a small
   environment and argument list, but still have ambient filesystem access.
5. **Pre-launch enforcement:** image verification, catalog trust checks, session
   recovery, config staging, and confined QEMU startup exist as components. The
   application must call all of them in the required order and fail closed.
6. **Guest readiness:** the QEMU virtio-serial channel is created, but the current
   GuestBridge does not publish authenticated readiness/status messages to the
   host. QMP running state only proves that QEMU is running.
7. **Diagnostics:** per-process stdout/stderr logs live in the session directory.
   A bounded retention and redaction policy is required before diagnostic export.
8. **Mandatory access control:** no supported AppArmor or SELinux profile is
   shipped yet. Bubblewrap reduces QEMU exposure but is not a replacement for a
   reviewed distribution policy covering every host-side process.
9. **End-to-end evidence:** unit tests exercise parsers, command construction,
   image registration, lifecycle, and recovery. A real Windows 11/KVM test of
   installation, GuestBridge startup, Spork launch, shutdown, and artifact
  deletion remains required.
## Security release checks

- Verify the selected image immediately before launch and refuse any mismatch.
- Confirm QEMU is wrapped by Bubblewrap and never inherits the desktop user's
  home or full environment.
- Assert that QMP and SPICE have no TCP listeners and remain beneath a mode-0700
  runtime directory.
- Test network-disabled sessions and reject a missing `passt` socket when network
  is requested.
- Demonstrate blocked access to host, RFC1918, link-local, and metadata addresses
  after the egress broker is implemented.
- Kill the UI, QEMU, viewer, passt, and swtpm at each startup state and verify that
  recovery either proves cleanup or preserves evidence without signaling an
  unrelated process.
- Verify the registered base hash is unchanged after every normal session.
- Run a Windows end-to-end matrix without real credentials or financial
  transactions.
