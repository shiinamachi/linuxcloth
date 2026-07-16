# Desktop UI principles

## Product task

The desktop helps a user find a service, confirm support, open it in a disposable
Windows environment, and close and delete that environment. Readiness and setup
support this task without replacing it with infrastructure terminology.

## Visual direction

- Prefer a TableCloth-like Windows utility aesthetic over marketing-style
  desktop chrome: light gray canvas (`#F8F8F8`), flat panels, small corner
  radii, and system-blue selection (`#26A0DA` / `#0078D4`).
- Catalog layout follows the upstream CatalogPage pattern: left category list,
  instruction + search row, icon-under-label service grid, and a utilitarian
  detail form rather than launcher cards or dark navigation rails.
- Establish hierarchy with spacing and typography before decorative surfaces.
- Follow the system light or dark theme.
- Use vector application chrome and preserve official catalog logos.
- Use motion only when it explains a state or navigation change.

## Information boundary

Show safety, licensing, data deletion, availability, and the next action in the
default flow. Put implementation names, paths, hashes, package repositories, and
raw diagnostics in a collapsed `기술 세부정보` or `설치 세부정보` section.

The repository Skills under `.agents/skills/` define the mandatory copy, design,
responsive, and verification workflows for future changes.
