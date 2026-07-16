# ADR-0014: Adopt an adaptive desktop design system

- Status: Accepted
- Date: 2026-07-16

## Decision

The desktop uses semantic light and dark theme resources rather than screen-local
colors. `Styles/ThemeResources.axaml` owns color, spacing, radius, and vector-icon
tokens. `Styles/Controls.axaml` owns shared typography, button, card, notice,
badge, input, and technical-detail styles. The application follows the operating
system theme by default.

Desktop layouts respond to their content control's logical width. The catalog
uses compact, medium, and wide modes at 820 and 1180 logical units. The setup
flow replaces its step rail with a compact progress header below 900 logical
units. Both flows support a 720×480 minimum logical window and preserve vertical
scroll access to every primary action.

Layout measurements remain independent of `RenderScaling`. Render scaling is
reserved for physical-pixel operations such as raster decoding, captures,
native-coordinate exchange, and scale-aware bitmap caches. Application chrome
uses vector geometry. Raster catalog logos retain a neutral logo surface and
must be decoded again when a future scale-aware cache observes a scale change.

The default information architecture describes user outcomes: finding a
service, preparing a Windows environment, opening the service, and closing and
deleting the disposable environment. Implementation names and diagnostics are
kept in explicitly collapsed technical-detail regions. License obligations,
preview limitations, and deletion consequences remain visible at the decision
point.

Headless UI tests cover all layout modes, the 720×480 minimum, and 200% render
scaling. Stable automation identifiers, keyboard navigation, theme policy, and
copy policy are verification requirements for subsequent UI changes.

## Consequences

- New colors require matching light and dark semantic resources.
- Screen XAML cannot contain direct hexadecimal colors or force dark mode.
- Layout changes must preserve compact, medium where applicable, and wide tests.
- Technical information remains available for support without dominating the
  normal workflow.
- Mixed-DPI monitor movement, GNOME/KDE Wayland, X11, Orca, and visual contrast
  still require real-desktop verification in addition to Headless tests.
