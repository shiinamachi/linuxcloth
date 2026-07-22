# Architecture decision records

ADRs are immutable once accepted. A later decision supersedes an earlier ADR instead of silently rewriting it.

1. [Use QEMU/KVM](0001-use-qemu-kvm.md)
2. [Use the official Spork execution contract](0002-use-official-spork.md)
3. [Use SPICE as the default display](0003-use-spice-console.md)
4. [Preserve the official catalog](0004-preserve-official-catalog.md)
5. [Disable host integration by default](0005-disable-host-integration.md)
6. [Require user-provided Windows media](0006-require-user-windows-media.md)
7. [Confine QEMU with Bubblewrap](0007-confine-qemu-with-bubblewrap.md)
8. [Register sealed Windows base images](0008-register-sealed-base-images.md)
9. [Persist and recover session ownership](0009-persist-and-recover-session-ownership.md)
10. [Pin and verify the Spork release](0010-pin-and-verify-spork-release.md)
11. [Gate running state on session-bound guest readiness](0011-require-session-bound-guest-readiness.md)
12. [Confine generated session helpers](0012-confine-session-helpers.md)
13. [Derive first-run setup from verified readiness](0013-derive-first-run-setup-from-readiness.md)
14. [Adopt an adaptive desktop design system](0014-adopt-adaptive-desktop-design-system.md)
15. [Automate Windows image installation](0015-automate-windows-image-installation.md)
16. [Pin and verify the setup virtio artifact](0016-pin-virtio-setup-artifact.md)
17. [Read UDF installation media with 7-Zip](0017-read-udf-installation-media-with-7zip.md)
18. [Prepare managed Secure Boot variables](0018-prepare-managed-secure-boot-vars.md)
19. [Track confined payload processes](0019-track-confined-payload-processes.md)
20. [Preserve image-build failure stages](0020-preserve-image-build-failure-stages.md)
