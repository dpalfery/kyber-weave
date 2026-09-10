# Unattended Ubuntu install for the CodeBurn dev VM

This directory contains a cloud-init `user-data` + `meta-data` pair that tells the Ubuntu 24.04 Server installer to configure itself without any user prompts. After it finishes, you reboot into GNOME and run the one-line provisioner.

Default credentials in `user-data`: **`codeburn` / `codeburn`**. Change them before using anywhere that matters.

## Build the CIDATA ISO (on your Mac)

```bash
cd windows/Scripts/autoinstall
hdiutil makehybrid -o codeburn-cidata.iso \
  -hfs -joliet -iso -default-volume-name CIDATA .
```

That produces `codeburn-cidata.iso` (around 2 KB) with the two YAML files at the root, labelled `CIDATA`.

## Hook it into UTM

1. Create the VM as usual (Virtualize → Linux → Ubuntu Server arm64 ISO).
2. Before first boot, open the VM's Settings → **Drives** → **New Drive** → pick **Removable** → **Import**, and select `codeburn-cidata.iso`.
3. Boot. The Ubuntu installer auto-detects the CIDATA volume, reads the autoinstall config, and runs the install without prompts. Takes 15-20 minutes depending on disk speed.
4. Reboot into the installed system, log in as `codeburn`, then:

   ```bash
   bash ~/provision.sh
   ```

   (The autoinstall drops the script to `~/provision.sh`. It installs Rust + Node + the codeburn CLI, clones the repo, and sets up the windows/ npm deps.)

5. `cd ~/codeburn/windows && npm run tauri dev`.

## Why not automate the provisioner run too

cloud-init's `late-commands` runs in the installer environment, which doesn't have a GNOME session for the tray icon to land in. We deliberately stop short of running `npm run tauri dev` from within autoinstall so the tray shows up on your first real login instead of a detached systemd unit.

## Skipping autoinstall

If you'd rather click through the Ubuntu installer normally, ignore this directory entirely. The `provision-linux.sh` script in the parent directory works the same way whether the OS was installed unattended or by hand.
