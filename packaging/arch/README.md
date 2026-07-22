# Arch-family setup hints

These manifests provide package-remediation hints for the capability-driven
desktop setup. They do not make the distribution identifier a support boundary:
Doctor rechecks executable, KVM, firmware, and runtime behavior after any
installation and remains the readiness authority.

Arch's `edk2-ovmf` provides Q35 Secure Boot-capable firmware without pre-enrolled
keys. The `virt-firmware` package supplies `virt-fw-vars`, which linuxcloth can
use to generate a private Microsoft-key NVRAM template without modifying the
system firmware.

No Arch package artifact is currently produced by the release pipeline. Source
runs and manually installed release trees must still satisfy the normal
GuestBridge and package-tree integrity requirements.
