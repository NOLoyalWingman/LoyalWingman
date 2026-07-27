# Changelog

All notable changes to this project will be documented in this file.

## [0.0.2] - Unreleased

0.34 compatibility prerelease.

### Changed

- Migrated `Weapon/WeaponStation.Rearm` to the amount/station API.
- Migrated `CombatAI.AnalyzeTarget` from `mobile` to the native 0.34 `maxRangeMultiplier: 100f` API.
- Adapted the DynamicMap `VirtualMFD` slot binding and native TextMeshPro styling used by Nuclear Option 0.34.

### Verification

- 927 logic tests, Release build, and native missile release contract gate pass.
- Nuclear Option 0.34 runtime testing confirms native `VirtualMFD` slot binding, TextMeshPro label styling, and child `Highlight` overlay behavior.

## [0.0.1] - 2026-07-27

### Added

- Loyal Wingman FQ-106 cradle release, follow/loiter, recovery, and map command functionality.
- Per-wingman combat mission controls, including air-to-air, SEAD, anti-ship, CAS, and strike logic.

### Changed

- Plugin features, HUD status messages, and combat now default to enabled.

### Removed

- The Tarantula / QuadVTOL1 Slingload Hook prototype port option. Provider-owned ports remain supported; the ordinary Cargo Bay four-round FQ cradle is unchanged.

### Notes

- Dedicated multiplayer is not accepted. Extended multi-cradle and combat runtime coverage remains limited.
