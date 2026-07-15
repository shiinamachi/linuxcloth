# Fedora-family packaging notes

The manifests contain candidate RPM names rather than a `.spec` because the
application publish layout and executable names are not final. A future spec
should consume a self-contained publish, own no user XDG data, and run no Windows
image build or network download in `%post`.

The `qemu-system-x86` metapackage is intentional: current Fedora packages split
the QXL device, SPICE chardev/UI, and core emulator into subpackages. If a future
spec replaces the metapackage with a minimal set, derive and test that set against
the [supported Fedora release](https://packages.fedoraproject.org/pkgs/qemu/qemu-system-x86/).

After installation, run the same capability checks described for Debian-family
packages. In addition, test the distribution SELinux domain interaction with
KVM, Bubblewrap namespaces, private XDG paths, and Unix QMP/SPICE/passt sockets.
Do not disable SELinux or add a broad allow rule to make a package pass.

Fedora may offer versioned virtio Windows driver media, but it is not a host
runtime dependency. Its exact version, digest, signature, source, and notices
belong in image metadata and the image-build workflow.
