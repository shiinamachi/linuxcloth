# 0018: Prepare a managed Secure Boot variable template by capability

## Status

Accepted

## Context

Some distributions ship Q35 x86_64 OVMF code with `secure-boot` and
`requires-smm` support but intentionally provide an empty variable template.
Their QEMU firmware descriptor therefore cannot advertise `enrolled-keys`.
Treating the distribution identifier as unsupported hides the actual missing
capability, while accepting the empty template would silently disable the
Secure Boot guarantee required for a Windows 11 base image.

## Decision

linuxcloth continues to prefer a system descriptor that already advertises
`secure-boot`, `enrolled-keys`, and `requires-smm`. When no such descriptor is
available, the desktop may use any bounded system descriptor that still proves
Q35 x86_64 `secure-boot` and `requires-smm` capability as a source.

If the maintained `virt-fw-vars` executable is present, the desktop invokes it
with an argument array to copy that source template into the private XDG cache,
enroll its standard Microsoft Secure Boot certificate set, and enable Secure
Boot. The source firmware under `/usr/share` is never modified. The output must
be a regular file, match the source template size, stay below 16 MiB, and pass
the existing strict firmware resolver through a generated private descriptor.
The NVRAM file is mode 0600 and its descriptor is promoted only after the NVRAM
file is complete.

The private descriptor directory is an additional resolver source, not a
replacement for system descriptors. A missing tool, incompatible source
firmware, or failed generation leaves the firmware capability unavailable and
is reported through Doctor. Distribution identity does not grant or deny the
capability.

## Consequences

- Hosts with pre-enrolled distribution firmware retain their existing path.
- Hosts with Secure Boot-capable empty OVMF variables can become ready without
  root access or changes below `/usr` after installing a maintained enrollment
  tool.
- The generated NVRAM template is cache state and must never be committed or
  distributed as a base image.
- `virt-fw-vars` and the distribution OVMF package remain trusted host-side
  dependencies; failures are conservative and do not relax `enrolled-keys`.
