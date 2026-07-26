# Loyal Wingman

Loyal Wingman is a Nuclear Option 0.33 plugin for managing FQ-106 wingmen. Version 0.0.1 is a Preview release: it is SP-first, and dedicated multiplayer acceptance plus extended runtime coverage remain pending.

## Requirements

- BepInEx 5
- Blueprinter (`com.nikkorap.blueprinter`) 1.8.21 or newer
- FQ-106 / Kestrel (`blueprinter.kestrel`) 2.2.0 or newer

## Install and uninstall

Copy the single release asset, `LoyalWingman.dll`, to:

`Nuclear Option\BepInEx\plugins\LoyalWingman\`

No ZIP extraction or PDB is required. To uninstall, remove that DLL (and the empty folder if desired). Verify the DLL against the separately published SHA-256 checksum before installing.

## Preview scope

The ordinary Cargo Bay cradle provides four FQ rounds per physical cradle. Loyal Wingman manages release, roster entries, follow/loiter behavior, recovery, physical cradle/round loadouts, and map commands for supported FQ workflows. The built-in aircraft switch backend is self-contained. Provider-owned drone-carrier ports may integrate through their provider contract; this Preview does not claim complete port, CLX, multi-port, or extended combat-matrix support.

Combat is disabled by default. Enable it before starting the game in `BepInEx\config\discord9.loyalwingman.cfg`:

```ini
[Combat]
Enable = true
```

Core controls include Space for the selected native cradle round, F10/F11 for switching among available aircraft, F8 for single-wingman air-to-air assignment, and the Loyal Wingman map page for selection, follow/loiter, recovery, and mission commands. F5 selects the roster on the map page. See the in-game map controls and logs when testing a Preview build.

## AI assistance disclosure

This project was developed with substantial assistance from generative AI tools. AI was used for portions of the code, refactoring, tests, documentation, and, where applicable, project assets. Release changes are reviewed, built, and tested by the maintainer, who remains responsible for the published result. AI assistance does not replace third-party attribution or license requirements.

## Build, test, and package

Set the game directory once for the current PowerShell session, or pass it explicitly to MSBuild:

```powershell
$env:NUCLEAR_OPTION_DIR = 'C:\Path\To\Nuclear Option'
dotnet run --project tests\LoyalWingman.LogicTests\LoyalWingman.LogicTests.csproj -c Release
dotnet build LoyalWingman.csproj -c Release -p:Deploy=false
.\scripts\PackageRelease.ps1
```

Equivalent explicit build command:

```powershell
dotnet build LoyalWingman.csproj -c Release -p:Deploy=false -p:GameDir='C:\Path\To\Nuclear Option'
```

`PackageRelease.ps1` runs the focused logic tests, builds without deployment or debug symbols, stages only `LoyalWingman.dll`, and writes its SHA-256 checksum.

## Support

Include both logs when reporting an issue:

- `BepInEx\LogOutput.log`
- `%USERPROFILE%\AppData\LocalLow\Shockfront\NuclearOption\Player.log`

## Release and legal information

- [0.0.1 Preview release notes](docs/RELEASE_0.0.1.md)
- [Preview install card](docs/TEST_BUILD_0.0.1.md)
- [NOMNOM submission draft](docs/NOMNOM_SUBMISSION.md)
- [Changelog](CHANGELOG.md)
- [MIT License](LICENSE)
- [Third-party notices](THIRD_PARTY_NOTICES.md)

Portions of the self-contained switch implementation are derived from BOTE v1.4.1; see the third-party notice for attribution and license terms.
