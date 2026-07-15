# ADR-0010: Pin and verify the Spork release

- Status: Accepted
- Date: 2026-07-15

## Decision

linuxcloth accepts one reviewed Spork release at a time. The shared contract pins
the release version, Bootstrap URL, exact byte length, SHA-256, signer certificate
DER SHA-256, portable Spork zip URL template, and per-architecture zip hashes.

The generated Express WSB checks the downloaded Bootstrap's size, digest,
Authenticode status, and signer fingerprint. The normal GuestBridge path also
restricts HTTPS redirects to GitHub release assets, obtains a read-locked file,
uses `WinVerifyTrust` with revocation checking, and launches the verified
executable directly with an argument list. It does not use a `latest` endpoint,
`Invoke-Expression`, or a manifest-provided artifact URL.

## Consequences

- A release update is an intentional code change that reviews and replaces all
  pins together.
- A valid TLS response or Authenticode signature alone is insufficient; the
  exact reviewed content and signer must both match.
- The policy does not replace signing and provenance for linuxcloth itself, nor
  does it provide an independent transparency log for the upstream release.
