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
- the integrity of privileged host package installation decisions and first-run
  state used to guide the operator.

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
| QEMU networking | QEMU has a private network namespace and reaches a host-side `passt` socket. The generated `passt` command disables guest-facing TCP/UDP forwards and gateway remapping. Network-disabled sessions do not require or start `passt`. |
| VM storage | Normal sessions use a qcow2 backing overlay plus copied OVMF variables and swtpm state. Registered base assets are hashed, bounded, symlink-rejected, and sealed read-only. |
| Display/control | SPICE and QMP use Unix sockets inside the private session directory. SPICE file transfer is disabled and clipboard is disabled by default. RDP is not implemented. |
| Host integration | Generated normal WSB rejects mapped folders; QEMU exposes only the generated read-only config disk. Audio, host folder, USB redirection, microphone, camera, and printer integration are not configured. |
| Catalog parsing | XML DTDs and external resolution are disabled; size, identifier, category, and URL fields are bounded and validated. The bundled `Catalog.xml` and PNG tree are bound to a pinned upstream commit and compiled SHA-256 values. Snapshots retain original XML bytes and last-known-good pointers. |
| Guest request | Normal WSB accepts only the fixed Express command and validated service IDs. GuestBridge requires exactly one valid bounded manifest and checks its catalog digest when a catalog is present. |
| Bootstrap supply chain | Spork `v1.20.5` Bootstrap URL, size, SHA-256, Authenticode signer certificate fingerprint, portable zip URL template, and per-architecture zip hashes are pinned. GuestBridge applies a strict HTTPS redirect allowlist, byte/hash checks, `WinVerifyTrust`, signer matching, and a read lock before direct execution. |
| Guest readiness | After config validation, GuestBridge writes a bounded fixed-format message containing the current session UUID over virtio-serial. The host requires the exact UUID before entering `Running`; timeout or mismatch enters conservative failed-start cleanup and preserves artifacts if cleanup cannot be proven. |
| Helper confinement | Normal-session `swtpm`, `passt`, and overlay-only `qemu-img` commands run through strict Bubblewrap wrappers. `swtpm` and `qemu-img` have private network namespaces; `passt` shares the host network only to provide egress. Session endpoints are restricted to mode 0600. |
| Process execution | Generated external process arguments are passed as arrays, and normal-session wrappers compare complete expected helper commands. User data is not concatenated into a host shell command. |
| Pre-launch enforcement | Desktop and CLI launch paths run conservative stale-session recovery and block launch on unresolved records, resolve validated service IDs, run host prerequisites, verify the registered image immediately before use, prepare the confined overlay/config, and then start the session host. |
| Crash recovery | A durable journal records boot-aware process identities. Desktop initialization and CLI `run` recover before launch, validate identity before QMP or signals, and preserve ambiguous sessions. `linuxcloth cleanup` is the manual diagnostic/retry path. |
| First-run authority | Desktop routing is derived from recovery, Doctor, image verification, packaged GuestBridge/OVMF resolution, and durable build state. Private setup JSON restores UI state only and cannot mark an unsafe host ready. |
| Host package installation | Arch/Debian/Fedora remediation names come from packaged manifests, but capability probes remain the readiness authority. PackageKit resolves and simulates the exact plan before an explicit user action; PackageKit/polkit owns privilege elevation. The desktop never runs a shell, sudo, pkexec, pacman, apt, or dnf. |
| Setup orchestration | A private, atomic `SetupRun` journal records the current operation and immutable input fingerprints. Restart rechecks actual host, media, build, and image state; completion history cannot override readiness. |
| Installation media | The user-selected Windows ISO and optional local virtio fallback receive size-bounded, network-disabled Bubblewrap-confined 7-Zip probes across ISO9660/UDF and SHA-256 hashing. Only fixed required entries can be probed or extracted. WIM/ESD analysis uses a private per-run directory in the disk-backed XDG cache and deletes it after analysis. The normal driver ISO comes from a release-pinned immutable URL and is length/hash verified before atomic private-cache promotion. Media are not uploaded. |
| Unattended installation | The answer file pins the reviewed WIM/ESD index, loads `vioscsi`, and wipes only the single staging-owned writable qcow2. Installation media are read-only. Per-run AutoLogon and cached answer-file secrets are removed before verification and sealing. |
| Managed build inputs | Desktop image builds require the packaged GuestBridge path and a descriptor-selected Secure Boot OVMF pair. When system firmware is Secure Boot-capable but has empty variables, a detected `virt-fw-vars` may derive a size-bounded, mode-0600 Microsoft-key template in the private XDG cache without modifying `/usr`; the generated descriptor must pass the same resolver. Arbitrary GuestBridge and firmware file selections are rejected before the builder runs. |

## Important limitations and required controls

1. **Package and catalog provenance:** compiled digest pins detect a modified
   bundled catalog and image tree, and the runtime network updater is not wired
   into the product path. The digest is not an upstream signature, however, and
   release packages are not yet covered by an operator-managed package signing
   key. Network catalog updates must remain disabled until an HTTPS origin and
   signed-manifest policy covers XML, images, and notices atomically.
2. **LAN egress:** the current `passt` options prevent inbound forwarding but do
   not deny guest access to private ranges, host interface addresses, link-local
   services, or cloud metadata. A policy-enforcing egress broker and tests are
   required before claiming local-network isolation.
3. **Recovery ownership window:** a crash can occur after a process starts but
   before its identity is durably journaled. An ambiguous `StartingNetwork`
   record is intentionally preserved and blocks later desktop/CLI launches; the
   same evidence prevents `cleanup` from resolving it. A supervisor/cgroup or
   pre-start process lease is needed for automatic crash cleanup across this
   window.
4. **Viewer confinement:** normal-session and image-builder `remote-viewer`
   processes still run as the desktop user with the desktop environment needed
   to reach X11/XWayland or Wayland. `swtpm`, `passt`, and generated `qemu-img`
   commands are now confined, but the viewer retains ambient user access.
5. **Catalog execution binding:** the host stages its verified `Catalog.xml` and
   GuestBridge checks the manifest digest, but Bootstrap receives only service
   IDs and pinned Spork artifact information. There is no verified catalog
   path/hash argument, so the host UI and Spork cannot be proven to use identical
   catalog bytes.
6. **Readiness semantics:** the session-bound GuestReady message has no secret or
   digital signature. It proves only that the guest process resolved the config
   and reached the point immediately before Bootstrap launch; it does not prove
   Bootstrap/Spork success, site launch, or an uncompromised guest.
7. **Diagnostics:** per-process stdout/stderr logs live in the session directory.
   A bounded retention and redaction policy is required before diagnostic export.
8. **Mandatory access control:** no supported AppArmor or SELinux profile is
   shipped yet. Bubblewrap reduces QEMU exposure but is not a replacement for a
   reviewed distribution policy covering every host-side process.
9. **End-to-end evidence:** automated tests exercise parsers, command
   construction, downloader/signature decisions, image registration, lifecycle,
   readiness, and recovery. A real Windows 11/KVM test of installation,
   GuestBridge startup, the pinned Bootstrap and Spork launch, shutdown, and
   artifact deletion has not been recorded and remains a release blocker.
10. **Package manager trust:** PackageKit and the configured distribution
    repositories are inside the host package trust boundary. The preview reduces
    accidental changes but does not make a malicious repository or compromised
    PackageKit backend trustworthy. Repository addition, key acceptance, EULA
    handling, and package-manager repair remain explicit operator tasks.

## Security release checks

- Verify the selected image immediately before launch and refuse any mismatch.
- Confirm QEMU is wrapped by Bubblewrap and never inherits the desktop user's
  home or full environment.
- Assert that QMP and SPICE have no TCP listeners and remain beneath a mode-0700
  runtime directory.
- Test network-disabled sessions and reject a missing `passt` socket when network
  is requested.
- Reject a Bootstrap with the wrong URL/redirect host, size, digest, Authenticode
  chain, or signer certificate, and review all pins together on version updates.
- Require the exact session-bound GuestReady message and verify that mismatch,
  timeout, and early guest exit trigger conservative failed-start cleanup.
- Bind the verified host catalog file and digest to Spork execution before
  claiming host/guest catalog identity.
- Crash after each process start but before identity persistence and prove that a
  supervisor/lease can still identify or safely reap the exact process.
- Demonstrate blocked access to host, RFC1918, link-local, and metadata addresses
  after the egress broker is implemented.
- Kill the UI, QEMU, viewer, passt, and swtpm at each startup state and verify that
  recovery either proves cleanup or preserves evidence without signaling an
  unrelated process.
- Verify the registered base hash is unchanged after every normal session.
- Run a Windows end-to-end matrix without real credentials or financial
  transactions.
