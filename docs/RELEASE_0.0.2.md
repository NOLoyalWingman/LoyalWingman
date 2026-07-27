# Loyal Wingman 0.0.2 compatibility prerelease

Loyal Wingman 0.0.2 is a compatibility prerelease for Nuclear Option 0.34, build 24403978. Runtime acceptance is still pending.

## Requirements and install

Install Nuclear Option 0.34, BepInEx 5, Blueprinter (`com.nikkorap.blueprinter`, minimum 1.8.21), and Kestrel / FQ-106 (`blueprinter.kestrel`, minimum 2.2.0). Copy the single `LoyalWingman.dll` release asset to `Nuclear Option\BepInEx\plugins\LoyalWingman\`. No ZIP extraction or PDB is required. Verify the DLL against its published SHA-256 checksum before installing.

## 0.34 API migrations

- Migrated `Weapon/WeaponStation.Rearm` to the amount/station API.
- Migrated `CombatAI.AnalyzeTarget` from `mobile` to the native 0.34 `maxRangeMultiplier: 100f` API.

## Verification

- 927 logic tests pass.
- Release build passes with deployment disabled.
- Native missile release contract passes against the 0.34 game assembly.

## Scope and pending acceptance

This prerelease is SP-first. The ordinary Cargo Bay four-round FQ cradle workflow is the primary target. Provider-owned ports are optional and depend on their provider contract; extended provider, CLX, multi-port, and combat runtime matrices are not accepted. Dedicated multiplayer is not accepted. Complete runtime acceptance on 0.34 remains pending.

For support, include `BepInEx\LogOutput.log` and `%USERPROFILE%\AppData\LocalLow\Shockfront\NuclearOption\Player.log`.
