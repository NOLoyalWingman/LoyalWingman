# Loyal Wingman 0.0.1

Loyal Wingman 0.0.1 is the initial stable release for Nuclear Option 0.33. Maintainer runtime acceptance has been completed.

## Install

Install BepInEx 5, Blueprinter (`com.nikkorap.blueprinter`, minimum 1.8.21), and Kestrel / FQ-106 (`blueprinter.kestrel`, minimum 2.2.0). Copy the single release asset, `LoyalWingman.dll`, to `Nuclear Option\BepInEx\plugins\LoyalWingman\`. No ZIP or PDB is required.

To uninstall, remove `LoyalWingman.dll` from that folder.

## Features

- FQ-106 cradle release, follow/loiter, recovery, and map controls.
- Per-wingman air-to-air, SEAD, anti-ship, CAS, and strike mission controls.
- Built-in aircraft switching. Plugin features, status messages, and combat are enabled by default; combat can be disabled in the plugin configuration.

## Known limitations

This release is SP-first. Dedicated multiplayer is not accepted, and extended CLX, provider, multi-port, and combat runtime matrices remain limited. Do not infer complete runtime support from the supported ordinary Cargo Bay workflow.

## Support and checksums

For support, provide `BepInEx\LogOutput.log` and `%USERPROFILE%\AppData\LocalLow\Shockfront\NuclearOption\Player.log`. Verify each downloaded DLL against the separately published SHA-256 checksum; maintainers generate it with `scripts/PackageRelease.ps1`.
