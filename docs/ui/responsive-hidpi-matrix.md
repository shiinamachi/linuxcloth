# Responsive and HiDPI matrix

## Logical layout modes

| Surface | Compact | Medium | Wide |
|---|---:|---:|---:|
| Catalog | `< 820` | `820–1179` | `>= 1180` |
| Setup | `< 900` | n/a | `>= 900` |

The minimum supported window is 720×480 logical units. Catalog compact mode
uses a two-row header and full-width service details. Medium mode uses a category
selector and dismissible details drawer. Wide mode shows category, catalog, and
details concurrently. Setup compact mode replaces the step rail with a progress
header.

## Verification matrix

| Logical window | 100% | 125% | 150% | 200% |
|---|---:|---:|---:|---:|
| 720×480 | required | required | required | required |
| 960×540 | required | required | required | required |
| 1280×720 | required | required | required | required |
| 1440×900 | required | required | required | required |

Headless tests cover the mode boundaries and 200% scaling. Before release, test
GNOME Wayland and KDE Wayland at all four scales, X11 at 100% and 200%, and
monitor movement at 100%↔200% and 125%↔200%.

Check clipping, unreachable actions, popup placement, focus return, fuzzy raster
logos, scale changes after monitor movement, and stale theme-specific imagery.
