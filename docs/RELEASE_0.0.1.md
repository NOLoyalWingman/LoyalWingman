# Loyal Wingman 0.0.1 Preview

Loyal Wingman is a preview plugin targeting Nuclear Option 0.33.

## Install

Install BepInEx 5, Blueprinter (`com.nikkorap.blueprinter`, minimum 1.8.21), and Kestrel / FQ-106 (`blueprinter.kestrel`, minimum 2.2.0). Copy the single release asset, `LoyalWingman.dll`, to `Nuclear Option\BepInEx\plugins\LoyalWingman\`. No ZIP or PDB is required.

To uninstall, remove `LoyalWingman.dll` from that folder.

## Preview features

- FQ-106 cradle release, follow/loiter, recovery, and map controls.
- Per-wingman air-to-air, SEAD, anti-ship, CAS, and strike mission controls.
- Built-in aircraft switching. Combat is disabled by default and must be enabled in the plugin configuration before use.

## Known limitations

This is an SP-first preview. Dedicated multiplayer is not accepted, and extended CLX, multi-port, and combat runtime matrices remain pending. Do not infer complete runtime support from this preview.

## Support and checksums

For support, provide `BepInEx\LogOutput.log` and `%USERPROFILE%\AppData\LocalLow\Shockfront\NuclearOption\Player.log`. Verify each downloaded DLL against the separately published SHA-256 checksum; maintainers generate it with `scripts/PackageRelease.ps1`.
