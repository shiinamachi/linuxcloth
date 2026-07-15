# Distribution security integration

No AppArmor or SELinux policy is currently shipped or claimed as supported.
The implemented QEMU boundary is Bubblewrap plus QEMU's own `-sandbox` policy.

A future mandatory-access-control policy must be generated from observed access
on each supported distribution and reviewed narrowly. At minimum it should cover:

- QEMU access to `/dev/kvm`, exact read-only firmware/base files, and one writable
  runtime session directory;
- swtpm access to only its copied state, control socket, and logs;
- passt access to its Unix socket and explicitly approved network destinations;
- remote-viewer access to the display socket and required desktop services;
- qemu-img and image-building tools access to explicit staging and source-media
  paths only.

Do not ship example profiles that grant recursive access to `$HOME`, all of
`/dev`, all network families, or every file below `/usr`. Dynamic XDG paths,
distribution-specific executable paths, Bubblewrap's own policy, and graphical
session buses must be represented deliberately.

Until reviewed profiles exist, packaging must not state that AppArmor or SELinux
provides linuxcloth isolation. On enforced systems, a denial is an integration
defect to diagnose; disabling the host policy is not a supported workaround.
