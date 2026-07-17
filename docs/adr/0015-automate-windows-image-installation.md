# ADR-0015: Automate Windows image installation

- Status: Accepted
- Date: 2026-07-17
- Supersedes: the interactive Windows installation portion of ADR-0013

## Decision

linuxcloth analyzes `sources/install.wim` or `sources/install.esd` before an
image build. The Windows ISO is inspected with network-disabled,
Bubblewrap-confined xorriso and wimlib-imagex processes. A single supported
Windows 11 amd64 image is selected automatically; media with multiple supported
images requires an explicit image-index choice before the build begins. The
selected index, edition ID, and display name are stored in the durable image
build state and checked against the GuestBridge report after installation.

The provisioning ISO contains a complete `windowsPE` answer-file pass. It loads
the Windows 11 amd64 `vioscsi` driver, wipes only disk 0, creates deterministic
UEFI/GPT system, reserved, Windows, and recovery partitions, and installs the
reviewed WIM/ESD image index into the Windows partition. QEMU command generation
rejects any installer topology other than one staging-owned writable qcow2 disk
and three read-only installation-media drives.

Installation and verification virtual machines run without opening a viewer.
They retain a local SPICE endpoint for an explicit diagnostic viewer action,
are bounded by phase-specific timeouts, and continue to use durable process
identity and QMP recovery. A viewer closing can no longer terminate an otherwise
healthy installation.

The per-run local administrator password is generated in memory and written only
to the private, temporary provisioning media. First-logon provisioning removes
AutoLogon registry secrets and cached Panther answer files before shutdown. The
host provisioning directory and ISO are deleted before base-only verification.

## Consequences

- Windows Setup no longer asks for an edition, storage driver, or target disk
  during a normal image build.
- `WindowsImageBuildState` schema version 2 records the approved installation
  image. Older staging manifests fail closed and are preserved for diagnosis.
- wimlib-imagex is a required image-planning dependency rather than an optional
  diagnostic tool.
- Localized media keep their own Windows Setup language defaults; linuxcloth
  does not force an unavailable WinPE language pack.
- A mismatch between the selected edition and the verified guest edition blocks
  image promotion.
