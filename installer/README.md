# AnotherDSHL installer and updates

The installer uses WinUI 3's standard controls at 529 × 352 logical pixels.
It opens on a welcome page, then shows the install directory, shortcut choices,
file progress and completion. Branding uses the original Aeryo gradient SVG;
WinUI rasterizes it at the current display scale.

## Build

```powershell
.\installer\scripts\Build-Installer.ps1
# Optional: place temporary publish/cabinet files on another drive.
.\installer\scripts\Build-Installer.ps1 -WorkDirectory C:\Temp\AnotherDSHL-build
```

Requires .NET 8 SDK and Visual Studio C++ Build Tools. The output is an offline
`installer/artifacts/<version>-win-x64/AnotherDSHL-v<version>-win-x64-Setup.exe`
plus `Portable.zip` and `SHA256SUMS.txt`. The tiny native entry point extracts an LZX cabinet and
opens the WinUI installer. App and installer share one copy of .NET and Windows
App SDK runtime files. ReadyToRun and trimming are disabled; trimming would
require additional validation of WinUI and shortcut COM.

The `build-info.json` records the exact payload directory and measured sizes.
Keep that payload as the baseline for the next release; use the package that
was actually distributed, not a fresh rebuild of an older source version.

## Updates

“关于 → 启动器更新” checks this repository's GitHub Releases on demand, downloads
assets to a unique local cache and verifies their size and SHA-256 against
`SHA256SUMS.txt`. The prerelease checkbox defaults on for Alpha and Beta builds. Downloads require the user's
confirmation; installation has a separate “退出并更新” confirmation.

Checking, downloading and local-package preparation can be cancelled. Failed
operations remove their temporary cache. Verified downloads persist across
launcher restarts and can be installed later; files are checked again before
launching maintenance. Local packages are copied into a private cache and all
changed files are verified before confirmation. Downgrade packages are rejected.

For a registered installation, `.adup` differential packages are preferred.
If their exact base version does not match, the full Setup is used instead.
Unregistered/manual-extract builds use the full Setup. A local `.adup` can also
be selected from the About page.

After increasing the application project version, generate a differential:

```powershell
.\installer\scripts\Build-Installer.ps1 -BasePayloadDirectory C:\previous-release\payload
```

This adds `AnotherDSHL-v<version>-win-x64-Update.adup` to the build and checksum
list. Publish Setup, the optional `.adup`, and `SHA256SUMS.txt` together in the
same GitHub Release. An `.adup` is update data, not a standalone application.
The installed `AnotherDSHL.Installer.exe` handles updates and uninstalling;
it relocates to a temporary directory before changing its own runtime files.

The delta format includes the exact base version, target file manifest,
changed files and obsolete-file list. Application builds with this updater
must be installed first before they can consume future `.adup` packages.
It is intentionally product-specific and does not accept ZSnaper `.zup` files.
The first public release (`v0.0.1-beta`) contained only a portable ZIP, so the
second release ships full Setup and Portable assets, without a public delta.

## Safety and verification

- Installation and updates verify each application file and commit through a
  staging directory. Exceptions restore previous files and registration.
- Update preserves existing shortcut choices, unowned files and user settings.
- Updates reject wrong base versions, invalid paths, malformed ZIP contents,
  missing files and checksum failures. Uninstall only removes manifest-owned files.
- Running application instances in the selected directory must be closed;
  the installer does not terminate other instances.
- GitHub publication is a separate action; the build script does not publish.

```powershell
dotnet run --project installer\tests\Installer.Smoke.csproj -c Release
dotnet build installer\src\Installer.WinUI\Installer.WinUI.csproj -c Release -p:Platform=x64
& .\installer\src\Installer.WinUI\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64\AnotherDSHL.Installer.exe --preview
```

Smoke checks use isolated temporary files, registry keys and real Windows COM
shortcuts. They cover installation, differential updates, corrupted packages,
wrong base versions, rollback after metadata commit and preservation on uninstall.
`--preview` never installs files; it is for reviewing UI pages.
