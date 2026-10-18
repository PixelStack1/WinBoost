# Installing WinBoost (EN)

## Install
1. Download `WinBoostSetup.exe` from the [Releases page](https://github.com/PixelStack1/WinBoost/releases). Requires Windows 10 (2004+) or Windows 11, 64-bit.
2. Run it. SmartScreen may say "Windows protected your PC": click **More info** → **Run anyway** (the app is not code-signed).
3. Accept the administrator prompt (WinBoost changes system settings and needs it).
4. Pick your language, keep the Desktop shortcut ticked if you want it, and finish. WinBoost goes to `C:\Program Files\WinBoost` and can start right away.

## First run
1. Click **Refresh** — WinBoost reads your PC's current state.
2. Open **Profiles** and pick **Recommended** (zero risk) or **Competitive**, or use **Boost my PC** on the Dashboard.
3. A restore point is created before every change. The switch stays on.
4. Click **Apply**. Some tweaks (HAGS, timer…) need a **reboot**.

## Rolling back
- Switch a tweak off and press **Apply**, or click **Revert all**.
- Last resort: *Control Panel → Recovery → Open System Restore*.

## Uninstall
*Settings → Apps → WinBoost → Uninstall*. Click **Revert all** first if you want all original settings back. The uninstaller closes WinBoost if it's open, removes the auto-reapply tasks, then asks whether to delete your settings, logs and backups (`%LocalAppData%\WinBoost`) — answer No to keep them.

## Build it yourself
Requirements: Windows 10 2004+ / 11 x64, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (x64).
```powershell
cd C:\path\to\winboost
powershell -ExecutionPolicy Bypass -File .\build.ps1
```
Output: `Desktop\WinBoost\WinBoost.exe` (only visible file; ~440 runtime files beside it are marked hidden — WinUI 3 cannot be a single-file exe) + a Desktop shortcut. Optional installer: `ISCC.exe WinBoost.iss` (Inno Setup 6).
