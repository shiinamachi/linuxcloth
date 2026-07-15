# ADR-0011: Gate running state on session-bound guest readiness

- Status: Accepted
- Date: 2026-07-15

## Decision

After GuestBridge resolves and validates exactly one guest config, it writes one
bounded `linuxcloth-ready-v1 <session-id>\n` message to the named Windows
virtio-serial device. The host reads the corresponding private Unix socket and
enters `Running` only when the fixed syntax and current session UUID match within
the configured timeout.

Malformed data, another session UUID, early closure, or timeout fails startup.
The session host then follows the same owned-process shutdown and artifact
cleanup path as any other start failure.

## Consequences

- QMP's running state is no longer treated as proof that GuestBridge started and
  consumed the intended config.
- The signal is sent before Bootstrap launch. It does not assert that Bootstrap
  or Spork completed, that a site opened, or that the Windows guest is trusted.
- There is no shared secret or signature, so this is session binding and
  readiness, not cryptographic guest attestation.
