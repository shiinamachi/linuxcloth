# ADR-0004: Preserve the official catalog

- Status: Accepted
- Date: 2026-07-15

Official `Catalog.xml`, `sites.xml`, images, and notices remain byte-for-byte upstream data. Derived search indexes are disposable caches, while Linux verification results live in a separate compatibility overlay keyed by service ID.

