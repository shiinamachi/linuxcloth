# ADR-0012: Confine generated session helpers

- Status: Accepted
- Date: 2026-07-15
- Supersedes in part: [ADR-0007](0007-confine-qemu-with-bubblewrap.md)

## Decision

Normal-session `swtpm`, `passt`, and overlay-creation `qemu-img` processes are
launched through Bubblewrap wrappers that accept only the complete generated
commands. They receive a cleared environment, system runtime read-only, a
private `/tmp`, and only the session resources required by that command.

`swtpm` and `qemu-img` have private network namespaces. `qemu-img` receives the
sealed base image read-only and the current session directory writable, and its
wrapper permits only creation of the disposable qcow2 backing overlay. `passt`
receives only the session directory but retains the host network namespace
because it is the VM's outbound network backend. Session sockets are restricted
to mode 0600 after creation.

## Consequences

- These helpers no longer have ambient access to the user's home directory.
- `passt` confinement limits files but does not filter private, link-local,
  metadata, or host destinations; a policy-enforcing egress layer is still
  required.
- `remote-viewer` remains outside Bubblewrap pending a reviewed desktop display
  and input socket policy.
