# Packaging status

This directory records host dependency mappings and security integration
requirements. It intentionally does not contain an installable package or desktop
entry yet: the CLI/desktop executable names and self-contained publish layout
have not been finalized.

The dependency manifests assume an x86_64 host and a self-contained linuxcloth
publish, so they do not select a system .NET runtime. Package automation must add
the exact native dependencies reported by the final publish and must test the
result in a clean VM.

Do not grant the linuxcloth executable setuid, broad Linux capabilities, or root
execution. `/dev/kvm` access should use the distribution's normal device/group or
logind policy. Bubblewrap must use the distribution-supported unprivileged-user-
namespace or setuid configuration.

Windows media, product keys, generated qcow2 images, OVMF variable state, TPM
state, virtio driver media, and SPICE Windows guest tools must not be embedded in
linuxcloth application packages unless their upstream license and redistribution
terms are separately reviewed. Windows installation media and activated images
are never redistributable project assets.
