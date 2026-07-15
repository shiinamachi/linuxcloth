# linuxcloth agent instructions

These instructions apply to every agent and every file in this repository.

## Required workflow

1. Reason internally in English. Communicate with the user only in Korean.
2. Keep changes in small, independently recoverable scopes.
3. As soon as one scope is complete and verified, commit it even if the overall task is unfinished.
4. Use Conventional Commits for every commit message (for example, `feat: add catalog snapshot validation`).
5. Before each commit, inspect the diff and run the narrowest relevant verification. Never include unrelated user changes.
6. Do not rewrite, squash, amend, or discard existing commits or user changes unless the user explicitly requests it.
7. Agents working in parallel must coordinate file ownership. Only the primary agent creates commits unless ownership of a commit is explicitly delegated.

## Version management

- Manage development runtimes with mise and pin every runtime to an exact version in the repository `mise.toml`; never use floating aliases such as `latest` or `lts`, or partial versions and ranges.
- Run `mise install` after checkout and prefer `mise exec -- <command>` for repository commands so the pinned runtimes are used.
- When changing the .NET SDK version, update `mise.toml`, `global.json`, and CI configuration together so every environment selects the same exact version.

## Project direction

- The product name is **linuxcloth**.
- Build a Linux-native catalog UI and CLI that launch disposable Windows 11 sessions through QEMU/KVM.
- Consume the official TableCloth catalog without modifying its source data. Keep Linux compatibility information in a separate overlay.
- Delegate package installation and service launch inside Windows to the official Spork/SporkBootstrap contract; never execute catalog packages on the Linux host.
- Use per-session qcow2 overlays and delete session state after shutdown. Never commit an overlay into the base image during a normal session.
- Do not mount host folders, enable clipboard sharing, USB redirection, microphone, camera, or inbound networking by default.
- Use argument arrays rather than shell-composed command strings for external processes.
- Treat catalog data, downloaded artifacts, `.wsb` files, and guest software as untrusted input.
- Do not implement VM-detection evasion, Windows activation bypasses, or redistribution of Windows media/images.

## Engineering quality

- Keep domain logic independent from UI and process execution so it can be unit tested.
- Prefer secure defaults, explicit state transitions, atomic writes, least privilege, and idempotent cleanup.
- Add or update tests with each behavior change. Run formatting, build, and relevant tests before marking a scope complete.
- Record material architecture decisions in `docs/adr/`.
- Never store credentials, fixed remote-access passwords, signing secrets, Windows media, or generated VM images in Git.
