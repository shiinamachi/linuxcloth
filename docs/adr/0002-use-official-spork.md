# ADR-0002: Use the official Spork execution contract

- Status: Accepted
- Date: 2026-07-15
- Extended by: [ADR-0010](0010-pin-and-verify-spork-release.md)

linuxcloth passes catalog service identifiers to the official Express bootstrap/Spork flow inside the Windows guest. It does not duplicate package installation logic and never executes catalog packages on the Linux host.

The current upstream command accepts service IDs and pinned Spork artifact hashes
but no verified catalog file/hash argument. Until that contract is extended,
linuxcloth cannot prove that Spork used the same catalog snapshot as the host UI.
