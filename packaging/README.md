# Linux packaging

The packaging pipeline creates a deterministic, self-contained x86_64 staging
tree and turns it into installable DEB and RPM packages. It publishes the CLI and
Avalonia desktop app for `linux-x64`, publishes GuestBridge for `win-x64`, and
merges byte-identical host runtime files to avoid shipping two copies.

```text
eng/publish-linux.sh --output artifacts/stage --version 0.1.0
eng/validate-package-tree.sh --root artifacts/stage --strict-tools
eng/package-deb.sh --stage artifacts/stage --output artifacts/packages
eng/package-rpm.sh --stage artifacts/stage --output artifacts/packages
```

When both host package toolchains are not installed, build each format in the
same digest-pinned target-family containers used by CI:

```text
eng/package-in-containers.sh --stage artifacts/stage --output artifacts/packages
eng/test-packages-in-containers.sh --packages artifacts/packages
```

The staging tree installs applications below `/usr/lib/linuxcloth`, stable
launchers below `/usr/bin`, desktop/AppStream metadata below `/usr/share`, and the
pinned GuestBridge at
`/usr/lib/linuxcloth/guest/linuxcloth-guest-bridge.exe`. Every regular file is
covered by `/usr/share/linuxcloth/files.sha256`, except the manifest itself;
`build-info.json` records the source revision, catalog revision, exact .NET SDK
and runtime-pack versions, runtime identifiers, and build epoch. Local builds
from a modified checkout are marked with
`sourceTreeDirty: true`; release CI is expected to build a clean checkout.

The publisher first verifies every checked-in NuGet lock file. Runtime-specific
SDK packs are restored with lock-file output redirected outside the source tree;
their version is selected by the exact, no-roll-forward SDK in `global.json`, and
their shipped bytes are covered by the package SHA-256 manifest. A publish
therefore never rewrites a tracked `packages.lock.json`.

The portable runtime's optional `libcoreclrtraceptprovider.so` is omitted because
it requires the obsolete `liblttng-ust.so.0` ABI unavailable on the tested
distributions. linuxcloth does not use that LTTng diagnostic provider; restoring
it requires a separately reviewed ABI-compatible runtime artifact.

Run a publish inside the oldest supported distribution for each release family.
Self-contained .NET removes the system .NET runtime dependency, but it does not
make glibc, native UI ABI, or DEB/RPM tool versions distribution-independent. CI
verifies two builds with the same declared toolchain have identical file
manifests and validates extracted DEB/RPM payloads. Reproducing package bytes at
a later date also requires the same distribution toolchain recorded by
provenance.

## Release evidence and signing boundary

CI generates SPDX JSON and CycloneDX JSON SBOMs from the final staging tree and
ships them beside `SHA256SUMS`. Non-pull-request builds use GitHub OIDC and
Sigstore to create both SLSA provenance and an SBOM attestation. Verify a
downloaded artifact against this repository with:

```text
gh attestation verify <artifact> --repo shiinamachi/linuxcloth
```

All workflow actions are pinned to immutable commits, and the Syft SBOM engine is
pinned to a specific release. The generated DEB and RPM files are not currently
embedded-signature packages: publishing through an APT or RPM repository still
requires a separately controlled offline distribution signing key and signed
repository metadata. Such private keys must never be stored in this repository
or copied into a general CI job.

Do not grant the linuxcloth executable setuid, broad Linux capabilities, or root
execution. `/dev/kvm` access should use the distribution's normal device/group or
logind policy. Bubblewrap must use the distribution-supported unprivileged-user-
namespace or setuid configuration.

Windows media, product keys, generated qcow2 images, OVMF variable state, TPM
state, virtio driver media, and SPICE Windows guest tools are rejected from the
staging tree. Windows installation media and activated images are never
redistributable project assets. Package installation has no maintainer script and
performs no download, VM creation, group modification, or privilege grant.
