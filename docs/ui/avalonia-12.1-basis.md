# Avalonia 12.1 basis

linuxcloth pins Avalonia 12.1.0 in `Directory.Packages.props`. Repository UI
guidance must be checked against that exact dependency and its installed API,
not copied from Avalonia 11 examples without verification.

The desktop relies on:

- theme variants and dynamic resources for system/light/dark support;
- logical layout units independent of render scaling;
- `TopLevel.RenderScaling` only for physical-pixel integration;
- vector `PathIcon` geometry for application chrome;
- automation properties and Linux AT-SPI integration;
- Avalonia Headless for deterministic layout and automation checks.

When the pinned Avalonia version changes, update this basis, central package
versions, all lock files, and any affected UI tests in the same scope.
