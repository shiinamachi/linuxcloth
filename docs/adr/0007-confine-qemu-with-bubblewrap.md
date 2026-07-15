# ADR-0007: Confine QEMU with Bubblewrap

- Status: Superseded in part by [ADR-0012](0012-confine-session-helpers.md)
- Date: 2026-07-15

## Decision

Every normal QEMU process is launched through the system `bwrap` executable. The
namespace exposes the distribution runtime read-only, `/dev/kvm`, the selected
base image and OVMF code image read-only, and exactly one writable session
directory. It clears the inherited environment, hides the user's home directory,
creates a private `/tmp`, and gives QEMU a private network namespace.

The host-side `passt` process connects to QEMU through a pathname Unix socket in
the session directory. QMP and SPICE also use pathname Unix sockets; no TCP
management or display listener is created.

## Consequences

- A compromised QEMU process does not receive ambient access to the user's home
  directory or the host network namespace.
- QEMU still depends on the Linux kernel, KVM, Bubblewrap, and distribution
  runtime as trusted computing base.
- `swtpm`, `passt`, `remote-viewer`, and image-preparation tools are separate
  host processes and are not yet placed in equivalent sandboxes.
- `passt --no-map-gw` and disabled port forwarding do not prevent guest egress to
  private, link-local, or metadata addresses. A policy-enforcing egress broker is
  required before linuxcloth can claim LAN isolation.

ADR-0012 supersedes the first statement for normal-session `swtpm`, `passt`, and
overlay-only `qemu-img`. `remote-viewer` remains outside Bubblewrap, and the LAN
egress limitation remains unchanged.
