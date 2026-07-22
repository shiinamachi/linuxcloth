# ADR-0020: Preserve image-build failure stages

- Status: Accepted
- Date: 2026-07-22

## Decision

Windows image construction reports process identity, Unix-socket readiness,
virtual-machine completion, and cleanup failures as distinct stages. Only the
timeout applied directly to the running installation or verification VM may be
described using that VM's configured duration.

Standard output and error pumps start immediately after the host wrapper
process launches. They remain bounded and private, but now also retain output
emitted before the expected confined payload identity is discovered.

## Consequences

- A five-second process identity failure is no longer mislabeled as the
  four-hour Windows installation limit.
- A fifteen-second endpoint failure names the process and missing Unix socket.
- Early Bubblewrap or payload diagnostics remain available after safe cleanup.
- The setup UI keeps a user-oriented recovery action while exact internal
  details remain in its technical-details section.
