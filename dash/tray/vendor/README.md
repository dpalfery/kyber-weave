# Patched GLib for the Linux tray build

`glib-0.18.5/` is the complete source from
[`glib-0.18.5.crate`](https://static.crates.io/crates/glib/glib-0.18.5.crate),
whose SHA-256 is
`233daaf6e83ae6a12a52055f568f9d7cf4671dabb78ff9560ab6da230ce00ee5`.
The crate's `LICENSE` and `COPYRIGHT` remain in the source directory.

The sole source change is the two-line
[upstream VariantStrIter fix](https://github.com/gtk-rs/gtk-rs-core/pull/1343):
the pointer passed to `g_variant_get_child` is mutable and is supplied as
`&mut p`. This addresses [GHSA-wrw7-89jp-8q8g](https://github.com/advisories/GHSA-wrw7-89jp-8q8g)
while Tauri's Linux GTK dependency still requires the GLib 0.18 series.

Remove the Cargo patch and this directory when a stable Tauri update resolves
the Linux tray dependency graph to `glib >= 0.20.0`, and verify the Linux tray
build and tests before removing them.
