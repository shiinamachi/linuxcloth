# Debian-family packaging notes

`runtime-dependencies.txt` maps the executables and firmware needed by the current
runtime feature set. `image-build-dependencies.txt` is needed only by the image builder.
Treat these as an input to a future `debian/control`, not as a claim that every
listed Debian/Ubuntu release ships the same package contents.

`qemu-system-gui` is explicit because Debian makes graphical display modules a
separate/recommended package. SPICE remains the normal display, while the current
runtime also contains an optional GTK console path. Confirm package splits on the
[supported Debian suite](https://packages.debian.org/qemu-system-x86) rather than
relying on recommends.

The runtime must verify capabilities after installation:

- `/dev/kvm` is usable by the invoking desktop user;
- QEMU provides x86_64 KVM, Q35, qcow2, Unix QMP/SPICE, and the required sandbox
  options;
- an x86_64 QEMU firmware descriptor selects Q35 Secure Boot with enrolled keys
  and SMM;
- swtpm creates a mode-restricted Unix control socket;
- passt supports pathname stream sockets, `--no-map-gw`, and disabled forwards;
- Bubblewrap can create the required user, mount, PID, IPC, UTS, and network
  namespaces without running linuxcloth as root;
- remote-viewer accepts a SPICE Unix-socket URI.

Debian-family archives do not provide one portable package name for every Windows
virtio/SPICE guest-media release. Fetching those artifacts must be a separate,
version-pinned, hash/signature-verified workflow with license notices; do not add
an unverified `curl | shell` maintainer script.
