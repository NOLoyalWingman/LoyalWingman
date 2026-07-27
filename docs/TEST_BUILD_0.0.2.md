# Loyal Wingman 0.0.2 compatibility test install card

## Requirements and install

- Nuclear Option 0.34
- BepInEx 5
- Blueprinter (`com.nikkorap.blueprinter`, minimum 1.8.21)
- Kestrel / FQ-106 (`blueprinter.kestrel`, minimum 2.2.0)

Copy only `LoyalWingman.dll` to `Nuclear Option\BepInEx\plugins\LoyalWingman\`. No ZIP extraction or PDB is required. `NOComponentsWIP` and BOTE are not runtime dependencies.

## Runtime checklist

- Confirm plugin load with no `TypeLoad` or Harmony errors.
- Confirm the cradle loadout UI loads.
- Release all four cradle rounds.
- Check map commands and missions.
- Check switching, carrier cruise, and RTB.
- Check rearm and recovery.
- Check a provider port if one is available.
- Quit cleanly and retain `BepInEx\LogOutput.log` plus `%USERPROFILE%\AppData\LocalLow\Shockfront\NuclearOption\Player.log`.

This compatibility prerelease is SP-first. Dedicated multiplayer and extended provider, CLX, multi-port, and combat runtime coverage remain unaccepted.
