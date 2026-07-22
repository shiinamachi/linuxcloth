# ADR-0019: Track confined payload processes

- Status: Accepted
- Date: 2026-07-22

## Decision

When a generated command declares an expected identity executable, the process
launcher accepts either the initially started process or a bounded descendant
of that process with the exact executable path. Descendant discovery follows
the live Linux `/proc` parent-child tree, revalidates each direct parent
relationship, and examines at most 64 processes.

The initially started wrapper remains responsible for exit-code and bounded-log
collection. Runtime termination targets the identified payload, while durable
session and image-build state records the payload PID, boot ID, start ticks, and
executable path. Crash recovery therefore continues to revalidate and signal
the actual QEMU or helper process rather than trusting a generic wrapper PID.

This is required for Bubblewrap confinement with PID namespaces: Bubblewrap can
remain as one or more supervisor processes instead of replacing its original
PID with QEMU, swtpm, or passt.

## Consequences

- Bubblewrap supervision no longer causes a false five-second identity timeout.
- A same-name process elsewhere on the host cannot satisfy launch identity; it
  must be a validated descendant of the process linuxcloth started.
- The existing boot-aware recovery identity remains tied to the actual payload.
- Process-tree discovery is Linux-specific, bounded, and fails closed if the
  expected executable cannot be identified.
