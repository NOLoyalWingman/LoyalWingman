# Loyal Wingman 0.0.1 install card

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

## Supported scope

This stable release is SP-first for the ordinary Cargo Bay FQ-106 workflow. Dedicated multiplayer is not accepted, and extended CLX, provider, multi-port, and combat runtime matrices remain limited.

For support, include the complete `BepInEx\LogOutput.log` and `%USERPROFILE%\AppData\LocalLow\Shockfront\NuclearOption\Player.log`.
