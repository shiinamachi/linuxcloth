# Desktop accessibility checklist

- Give every interactive control a stable `AutomationId`.
- Give icon-only controls an accessible name and help text when the action is
  not obvious.
- Keep Tab order aligned with visual reading order.
- Support Ctrl+F and Ctrl+K for catalog search and Escape for adaptive details.
- Return focus to the service list after dismissing details.
- Announce progress politely and blocking errors assertively.
- Pair status colors with text or a recognizable status mark.
- Keep general text contrast at least 4.5:1 and essential non-text contrast at
  least 3:1.
- Keep icon actions at least 32×32 logical units and primary actions at least
  40 logical units high.
- Ensure dialogs and drawers do not obscure the focused control.
- Verify the automation tree with Accerciser and the primary journey with Orca
  on Linux before release.
