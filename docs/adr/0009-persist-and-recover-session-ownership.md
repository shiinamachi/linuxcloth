# ADR-0009: Persist and recover session ownership

- Status: Accepted
- Date: 2026-07-15

## Decision

Each session durably journals its state and every owned process identity before
progressing. An identity combines PID, Linux boot ID, process start ticks, and
the expected executable. Recovery revalidates all fields before sending a
signal, prefers QMP guest shutdown and QMP quit for QEMU, and only then escalates
to local termination.

Artifacts are deleted only after every recorded process is proven stopped. An
invalid journal, identity mismatch, unrecorded-QEMU ambiguity, or failed stop is
preserved for operator inspection instead of being guessed away.

## Consequences

- Reused PIDs are not sufficient to make linuxcloth signal an unrelated process.
- Crash recovery is intentionally conservative and can leave storage behind.
- Recovery must be integrated at every application startup and exposed through
  an operator command before it is an end-user guarantee.

Desktop startup and CLI `run` now perform this recovery gate before allowing a
new session, and `linuxcloth cleanup` exposes the same process as a manual
diagnostic and retry path.

A crash between starting a process and durably recording its identity can still
leave an ambiguous `StartingNetwork` journal. Recovery intentionally preserves
that session and blocks a new launch; a supervisor/cgroup or pre-start process
lease is required to make this window automatically recoverable.
