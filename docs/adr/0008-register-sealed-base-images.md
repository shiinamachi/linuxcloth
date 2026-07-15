# ADR-0008: Register sealed Windows base images

- Status: Accepted
- Date: 2026-07-15

## Decision

Windows base images are built directly in a private staging directory under the
managed image registry, preserving qcow2 sparseness. Promotion records SHA-256,
length, and timestamp metadata for the base image and OVMF variable template, a
bounded deterministic digest of the swtpm template tree, and the identity of the
external OVMF code image. The complete staging directory is sealed read-only and
atomically renamed to its final image identifier.

Normal sessions create a qcow2 overlay and copy the OVMF variables and TPM state;
they never commit changes back to the registered base.

## Consequences

- Interrupted registration cannot publish a partially described image.
- Verification can detect changes to managed assets and distribution firmware.
- Unix mode bits are a guardrail, not an immutable or cryptographic signature.
  The owning user or root can still replace an image; launch orchestration must
  run full verification immediately before using it.
- Physical secure erasure of discarded overlays is not guaranteed on SSDs,
  copy-on-write filesystems, snapshots, or journaled storage.
