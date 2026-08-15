---
name: maui-dev/environment-doctor
description: Diagnose and repair .NET MAUI SDK, workload, Android, and Xcode setup issues using dotnet-maui-doctor workflow.
source: https://github.com/dotnet/skills/tree/main/plugins/dotnet-maui/skills/dotnet-maui-doctor
---

# Environment Doctor — .NET MAUI

Use this workflow whenever a MAUI build fails due to SDK, workload, emulator, or tooling configuration issues — not code bugs.

---

## Step 1 — Run the MAUI Check Tool

```bash
dotnet tool install -g Redth.Net.Maui.Check 2>/dev/null || true
maui-check --non-interactive
```

This is the authoritative first step. It detects missing workloads, wrong JDK versions, stale emulators, and Xcode incompatibilities. **Read its output before attempting any manual fix.**

---

## Step 2 — Check Installed Workloads

```bash
dotnet workload list
```

Expected output for a MAUI project:

```
maui
maui-android
maui-ios
maui-maccatalyst
maui-windows   # Windows only
```

If any are missing:

```bash
dotnet workload restore   # installs workloads declared in .csproj
# or
dotnet workload install maui-android maui-ios maui-maccatalyst
```

---

## Step 3 — Verify .NET SDK Version

```bash
dotnet --version
dotnet sdk check
```

Match the SDK to the project's `<TargetFrameworks>` in `.csproj`. For `net10.0-*` targets you need .NET 10 SDK. **Never hardcode a version number** — always check the project file first.

---

## Step 4 — Android Toolchain

```bash
# Check ANDROID_SDK_ROOT is set
echo $ANDROID_SDK_ROOT

# Verify build tools version
sdkmanager --list | grep "build-tools"

# Accept licenses if needed
sdkmanager --licenses
```

**JDK requirements:**
- Validate the JDK version required by your target .NET MAUI framework and Android toolchain (e.g., Microsoft OpenJDK 17 for .NET MAUI 10).
- Verify both your selected JDK version and `JAVA_HOME` match the version expected by the Android toolchain to avoid class file version mismatches.

```bash
# Verify the correct JDK is in use and JAVA_HOME is set
java -version
echo $JAVA_HOME
which java
```

---

## Step 5 — iOS / Xcode (macOS only)

```bash
# Check Xcode version
xcode-select --print-path
xcodebuild -version

# Ensure Xcode CLT is installed
xcode-select --install 2>/dev/null || echo "Already installed"

# Accept Xcode license
sudo xcodebuild -license accept
```

If `maui-check` reports "Xcode is not installed or incompatible":
- Update Xcode from the Mac App Store
- Run `sudo xcodebuild -license accept` after updating

---

## Step 6 — Clean and Rebuild

After fixing toolchain issues, always clean the cached build artifacts:

```bash
dotnet clean
rm -rf bin/ obj/

dotnet build -f net10.0-android
dotnet build -f net10.0-ios        # macOS only
dotnet build -f net10.0-maccatalyst  # macOS only
```

Build each TFM separately to isolate platform-specific errors.

---

## Step 7 — Android Emulator Issues

```bash
# List available AVDs
emulator -list-avds

# Start a specific emulator
emulator -avd <avd-name> &

# Check ADB device connection
adb devices
```

If no devices appear: restart ADB with `adb kill-server && adb start-server`.

---

## Step 8 — VS Code / Rider: Reload Project

After workload/SDK changes, IDEs cache stale data:
- **VS Code** (with C# Dev Kit and .NET MAUI extensions): Reload window / close and reopen solution
- **Rider**: File → Reload All Projects

---

## Step 9 — Collect Diagnostic Output

If `maui-check` passes but the build still fails, collect:

```bash
dotnet build --verbosity diagnostic 2>&1 | tee build-diag.log
```

Search the log for `error MSB` and `error NU` codes — these identify the exact MSBuild or NuGet issue.

---

## Step 10 — When to Escalate

Escalate to a known-good environment or file a GitHub issue if:
- `maui-check` passes, clean build still fails
- Error reproduces on a new empty MAUI project (not project-specific)
- Error started after an OS or Xcode update (likely requires new workload/SDK release)

File issues at: https://github.com/dotnet/maui/issues — include `dotnet --info` and `maui-check` output.

---

## Quick Reference

| Symptom | First command |
|---|---|
| "MAUI workload not found" | `dotnet workload restore` |
| "JDK version mismatch" | Verify JDK version matches target .NET MAUI framework and check `JAVA_HOME` |
| "Android SDK not found" | Check `ANDROID_SDK_ROOT`; run `sdkmanager --licenses` |
| "Xcode incompatible" | Update Xcode; `sudo xcodebuild -license accept` |
| "No devices found" | `adb kill-server && adb start-server`; check emulator started |
| Stale IDE errors after workload update | Reload window / close + reopen solution |

---

## References

- [dotnet-maui-check](https://github.com/Redth/dotnet-maui-check)
- [MAUI troubleshooting](https://learn.microsoft.com/dotnet/maui/troubleshooting)
- [MAUI workloads](https://learn.microsoft.com/dotnet/maui/get-started/installation)
