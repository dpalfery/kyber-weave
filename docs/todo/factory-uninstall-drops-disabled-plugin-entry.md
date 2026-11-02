---
id: todo/factory-uninstall-drops-disabled-plugin-entry
title: "The harness's own uninstall deletes a pre-existing enabledPlugins: false entry"
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-11-02
---

# The harness's own uninstall deletes a pre-existing enabledPlugins: false entry

Measured on Factory Droid CLI 1.1.0: `droid plugin uninstall` removes the `enabledPlugins` entry
outright rather than setting it to `false`, and it leaves the marketplace registration in place.
`droid plugin marketplace remove` then deletes the registration. The teardown KyberDash drives is
those two commands, in that order
(`dash/src/capture/harnesses/factory-plugin/marketplace.ts`, `marketplaceUninstallCommands`).

So an owner who had that plugin listed as **disabled** before the install has that key removed by
the harness's uninstall rather than restored to `false`. The install wrote `true`; the uninstall
removes the entry; the owner's original `false` is gone, and nothing in the receipt records it —
the receipt holds the marketplace name, the plugin identity, the tree root and the file digests
(`dash/src/capture/harnesses/factory-plugin/install.ts`, the `Receipt` type), not the harness-side
state the install found.

That puts the teardown against the restore criterion
[ADR 0031](../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md) states: no entry
the owner had is lost or altered. The criterion is the semantic one — the owner's settings file
changes by the harness's hand, so byte-identity does not hold — and the harness's own uninstall
does not satisfy it on its own.

**What a decision must settle.** How the uninstaller restores an entry the owner had.

- Capture the harness-side state before the install and restore it after the uninstall, through
  the harness's own commands if there is one that re-adds a disabled entry, and otherwise through
  the one command that does. The state would have to be read at install time and recorded, which
  the receipt does not do today.
- Report it and leave it to the owner: the uninstall prints the exact `droid plugin …` command
  that restores the disabled entry. That is the residue rule ADR 0031 already states for residue
  the harness leaves behind, applied to this case.
- Declare the criterion unachievable for this entry and say so plainly in the ADR, so the
  criterion states what it actually promises.

**Deliberately not done here.** The uninstall path was not changed and the receipt was not
extended. ADR 0031 already requires the uninstaller to handle this case itself rather than assume
the harness's commands satisfy the criterion, and the mechanism is a decision about the harness's
own settings file — the one file KyberDash has promised never to write.
