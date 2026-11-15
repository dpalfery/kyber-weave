# Models

**What to tell the user.** Each squad agent names a *tier*, a kind of model it needs, such as
`fast` for quick lookups or `reviewer` for careful review. Each harness maps every tier to one
concrete model. A *preset* is a saved mapping the user can apply to a harness.

## What to look up first

1. `kyber-weave squad preset list` shows each preset and which harness uses it ("used by").
2. A harness with a preset: `kyber-weave squad preset show <name> --target <install name>` prints
   tier to model.
3. A harness without one uses the bundled models: read that harness's column in
   `~/.kyber-weave/squad/profiles/<version>.yml`, where `<version>` is what `kyber-weave --version`
   prints without any `+build` suffix. If the file is missing, say the bundled models could not be
   read; do not guess them.

## The question

- *What*: the models each harness uses per tier (show a short table: tier, model).
- *Why*: models set the cost, speed and quality of every run; a model the user has no access to
  makes those agents fail.
- *Options*: keep the current models, or change them for a named harness (the
  `kyber-weave-model-profile` skill builds a preset with the user and applies it).
- *Cost and undo*: a preset is a file under `~/.kyber-weave/squad/presets/`, applied by
  rewriting that harness's agent files; `kyber-weave squad preset clear --target <install name>`
  (add `--global` for a global install) returns to the bundled models.
- *Found*: which harnesses use a preset and which use the bundled models.
- *Recommend*: keep the current models unless a listed model is one the user cannot use or pay
  for.
