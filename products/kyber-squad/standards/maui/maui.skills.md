---
id: standards/maui/skills
title: "maui skills"
doc-type: skill
status: draft
technology: maui
owner: unassigned
last-reviewed: 2026-11-22
---

# maui skills

## What this folder covers

- Standards (`maui.standards.md`): safe area and theming under Naming and layout; MVVM and
  binding rules, dependency injection lifetimes, Shell navigation, CollectionView and app
  lifecycle under Coding standards.
- Environment diagnosis for a failing MAUI build (below).

## Environment doctor

Use when a build fails from SDK, workload, emulator or tooling setup, not a code bug.

1. `dotnet tool install -g Redth.Net.Maui.Check`, then `maui-check --non-interactive`; read its output before any manual fix.
2. `dotnet workload list` must show maui, maui-android, maui-ios, maui-maccatalyst (and maui-windows on Windows). Fix with `dotnet workload restore`; if that is not enough, `dotnet workload install maui-android maui-ios maui-maccatalyst`.
3. `dotnet --version` and `dotnet sdk check` must match the `<TargetFrameworks>` in the project; never hard-code a version.
4. Android: `$ANDROID_SDK_ROOT` set; `sdkmanager --list | grep build-tools` (PowerShell: `sdkmanager --list | Select-String build-tools`) to verify build tools; `java -version` and `$JAVA_HOME` must agree on the JDK the toolchain expects (17 for .NET 8/9/10).
5. iOS (macOS): `xcodebuild -version` and `xcode-select --print-path`. If maui-check reports Xcode incompatible, update Xcode.
   Accepting the Android SDK licenses (`sdkmanager --licenses`) or the Xcode license (`sudo xcodebuild -license accept`), and installing Xcode's command-line tools (`xcode-select --install`), need a person at the machine: an agent does not run them. Stop and report the one the owner must run.
6. Clean (`dotnet clean`, remove `bin/` and `obj/`) and build each target framework separately to isolate platform errors.
7. No emulator: `emulator -list-avds`, start one with `emulator -avd <name>`, `adb devices`; `adb kill-server && adb start-server` if empty.
8. Reload the IDE solution after workload or SDK changes.
9. Still failing: `dotnet build --verbosity diagnostic 2>&1 | tee build-diag.log` and search `error MSB` / `error NU`. Escalate (known-good environment or an issue) also when maui-check passes but a clean build still fails, or the failure began after an OS or Xcode update (needs a new workload or SDK). If an empty new MAUI project also fails, file an issue at dotnet/maui with `dotnet --info` and `maui-check` output.

## Hand-off

When ceding work to delivery, provide the publish and build commands and the RID or
workload needs.
