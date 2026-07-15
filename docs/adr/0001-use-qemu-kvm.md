# ADR-0001: Use QEMU/KVM

- Status: Accepted
- Date: 2026-07-15

linuxcloth uses an unprivileged `qemu-system-x86_64` process with KVM acceleration and QMP control instead of extending the legacy LXD/Wine experiment. Each normal session writes to a disposable qcow2 overlay backed by a sealed base image.

This gives Windows driver compatibility and an explicit, testable session lifecycle without requiring a system libvirt daemon.

