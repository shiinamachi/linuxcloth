# ADR-0017: Read UDF installation media with 7-Zip

- Status: Accepted
- Date: 2026-07-22
- Supersedes: the installation-media reader portions of ADR-0015 and ADR-0016

## Decision

linuxcloth uses the distribution-provided `7z` executable to inspect Windows and
virtio-win installation media and to extract `sources/install.wim` or
`sources/install.esd` for edition analysis. This supports both ISO9660 and UDF
trees, including official Windows 11 media whose ISO9660 tree contains only a
placeholder while the installation files live in UDF.

Every probe names one fixed required entry, and every extraction names one fixed
WIM/ESD entry. The commands use argument arrays, disable wildcard matching, and
run inside the existing network-disabled Bubblewrap boundary with only the
selected read-only media and a private analysis directory exposed. Media remain
size-bounded and SHA-256 hashed. `xorriso` remains required only to create the
temporary provisioning ISO owned by linuxcloth.

Doctor and the Debian/Fedora image-build package plans require `7z` from the
distribution's `7zip` package. Durable image-build state schema version 3 records
the resolved 7-Zip executable path so resume uses the same explicit toolchain.

## Consequences

- Current Microsoft UDF Windows 11 x64 ISOs pass the same boot-image and WIM/ESD
  policy as older ISO9660-readable media.
- Parsing untrusted UDF is delegated to the host distribution's maintained 7-Zip
  package inside a least-privilege sandbox.
- Staging manifests from schema version 2 cannot be resumed and fail closed;
  their files remain preserved for diagnosis or explicit cleanup.
