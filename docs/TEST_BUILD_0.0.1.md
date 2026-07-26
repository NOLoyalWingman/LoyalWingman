# Loyal Wingman 0.0.1 Preview install card

## Requirements

- Nuclear Option 0.33
- BepInEx 5
- Blueprinter (`com.nikkorap.blueprinter`, minimum 1.8.21)
- Kestrel / FQ-106 (`blueprinter.kestrel`, minimum 2.2.0)

`NOComponentsWIP` and BOTE are not runtime dependencies. Loyal Wingman includes its own self-contained switch backend.

## Install and uninstall

1. Install the requirements above.
2. Copy the single release asset, `LoyalWingman.dll`, to `Nuclear Option\BepInEx\plugins\LoyalWingman\`.
3. No ZIP extraction beyond that DLL and no PDB are required.

To uninstall, remove `Nuclear Option\BepInEx\plugins\LoyalWingman\LoyalWingman.dll` (and the now-empty folder if desired).

## Preview scope

This is an SP-first preview for Tarantula/FQ-106 workflows. Full dedicated multiplayer acceptance and extended CLX, multi-port, and combat runtime matrices remain pending.

For support, include the complete `BepInEx\LogOutput.log` and `%USERPROFILE%\AppData\LocalLow\Shockfront\NuclearOption\Player.log`.
