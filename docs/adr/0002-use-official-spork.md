# ADR-0002: Use the official Spork execution contract

- Status: Accepted
- Date: 2026-07-15

linuxcloth passes catalog service identifiers to the official Express bootstrap/Spork flow inside the Windows guest. It does not duplicate package installation logic and never executes catalog packages on the Linux host.

