---
id: todo/kyberdash-lightsaber-status-icon
title: Restore the KyberDash lightsaber status icon and blade glow
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-11
---

# Restore the KyberDash lightsaber status icon and blade glow

The owner clarified on 2026-10-11 that the requested status treatment was the
lightsaber icon with a blade that glows red when status is red and green when
status is green. Replacing the lightsaber with a red or green circle does not
meet that request. Restore the lightsaber status icon in the tray.

## Acceptance criteria

- Preserve the recognizable lightsaber silhouette, including its hilt and blade.
- Show a red blade glow for red status and a green blade glow for green status.
- Do not substitute a colored circle for the lightsaber.
- Render the existing engine-provided status through the shared API, following
  the [display-layer rule](../rules/kyberdash-display-layer.md).
- Verify both colors in the running tray at its actual menu-bar size, including
  light and dark appearance, and obtain owner visual acceptance before closeout.

This todo records deferred visual correction; it does not change status semantics.
