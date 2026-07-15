# Fedora-family packaging notes

`linuxcloth.spec.in` is the binary-package recipe consumed by
`eng/package-rpm.sh`. The script creates a normalized source archive from the
self-contained staging tree, fixes the RPM build host/time metadata, extracts the
result, and verifies linuxcloth's SHA-256 inventory. The spec owns no user XDG
data and has no `%post` action, network download, or Windows image build.

The `qemu-system-x86` metapackage is intentional: current Fedora packages split
the QXL device, SPICE chardev/UI, and core emulator into subpackages. If a future
spec replaces the metapackage with a minimal set, derive and test that set against
the [supported Fedora release](https://packages.fedoraproject.org/pkgs/qemu/qemu-system-x86/).

After installation, run the same capability checks described for Debian-family
packages. In addition, test the distribution SELinux domain interaction with
KVM, Bubblewrap namespaces, private XDG paths, and Unix QMP/SPICE/passt sockets.
Do not disable SELinux or add a broad allow rule to make a package pass.

Automatic RPM dependency generation is disabled deliberately because the payload
contains both Linux ELF files and a Windows PE GuestBridge; otherwise PE imports
can become invalid Linux package requirements. Native libraries and host runtime
tools are therefore listed explicitly and must be reviewed against every target
Fedora release.

The spec marks the complete license directory as `%license` and declares the
material license families represented by the shipped application, catalog,
framework, native graphics libraries, and font. CI installs the final archive in
the digest-pinned Fedora 44 container, verifies every packaged ELF dependency,
runs CLI/catalog smoke tests, and removes the package again.

Fedora may offer versioned virtio Windows driver media, but it is not a host
runtime dependency. Its exact version, digest, signature, source, and notices
belong in image metadata and the image-build workflow.
