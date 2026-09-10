# Flathub submission

These files publish CodeBurn Desktop on Flathub. The manifest repacks the
Linux deb from GitHub Releases, so no source build is needed.

## First-time submission

1. Fork `https://github.com/flathub/flathub` (uncheck "fork only master").
2. Create a branch off `new-pr` named `org.agentseal.CodeBurn`.
3. Copy the three `org.agentseal.CodeBurn.*` files from this directory into the
   repo root of that branch.
4. Open a pull request against the `new-pr` branch of `flathub/flathub`.
5. A reviewer responds on the PR. After approval Flathub creates a dedicated
   `flathub/org.agentseal.CodeBurn` repo; future updates are PRs there.

## Local test build (any Linux machine or VM)

```sh
flatpak install flathub org.flatpak.Builder
flatpak run org.flatpak.Builder --force-clean --user --install \
  --install-deps-from=flathub --repo=repo builddir org.agentseal.CodeBurn.yaml
flatpak run org.agentseal.CodeBurn
```

## Each desktop release

Update in `org.agentseal.CodeBurn.yaml`:
- the deb `url` (new `desktop-vX.Y.Z` tag)
- its `sha256` (`gh api repos/getagentseal/codeburn/releases/tags/desktop-vX.Y.Z -q '.assets[] | select(.name | endswith(".deb")) | .digest'`)

and add a `<release>` entry in `org.agentseal.CodeBurn.metainfo.xml`.
Then PR the `flathub/org.agentseal.CodeBurn` repo; merging publishes.
