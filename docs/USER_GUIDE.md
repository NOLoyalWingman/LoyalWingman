# Loyal Wingman user guide

## Requirements and installation

Loyal Wingman 0.0.2 compatibility prerelease requires Nuclear Option 0.34, BepInEx 5, Blueprinter 1.8.21 or later, and FQ-106 / Kestrel 2.2.0 or later. Copy the single `LoyalWingman.dll` release asset to `Nuclear Option\BepInEx\plugins\LoyalWingman\`. No ZIP extraction or PDB is required.

The plugin, HUD messages, and Combat are enabled by default. To disable Combat, edit `BepInEx\config\discord9.loyalwingman.cfg` before starting the game:

```ini
[Combat]
Enable = false
```

## Cradle loadouts and release

Equip **FQ-106 Loyal Wingman Four-Round External Cradle** in a Cargo Bay. In the aircraft loadout screen, selecting one or more cradles opens **CRADLE LOADOUTS** so each cradle can be planned before launch. Choose **CRADLE n**, then **ROUND 1** through **ROUND 4**; **EMPTY** means no round is planned. Use each hardpoint dropdown to select its equipment, **RESET** to restore that cradle's plan, and **COLLAPSE** to close the panel.

There is no current UI feature literally named **Mission Profile**. Cradle loadout planning is pre-launch equipment data. A2A, CAP, SEAD, ASUW, CAS, and STRIKE are active map missions assigned after release.

Press Space to fire the selected native cradle round. Each accepted round creates one managed FQ. The roster supports row selection, Ctrl-click selection, and F5 to select all.

## Map commands and keys

The direct-mode labels are **TAKE CONTROL**, **AUTO**, **FOLLOW**, **LOITER**, and **CRUISE**. **CRUISE** requires a clicked map point; that point becomes the loiter point. **LOITER** clears the saved point. There is no HERE button.

- Space: fire the selected native cradle round.
- F5: select all roster entries on the map page.
- F8: assign the leader's first native hostile-aircraft target to one best eligible defender; it may assign none.
- F10/F11: switch among available aircraft.

With Combat enabled (the default), the active missions are:

| Command | Target or action |
| --- | --- |
| A2A | Selected hostile aircraft |
| CAP | Clicked point |
| SEAD | Hostile SAM |
| ASUW | Hostile surface ship |
| CAS | Clicked search center |
| STRIKE | One or more native target-list hostile ground targets |
| RTB | Return the selected wingmen |
| STAND DOWN | Stop their current activity |
| CLEAR MISSION | Clear one shared active mission from the selected wingmen |

Combat should be enabled for all six combat missions. A command can be rejected when its target, ammunition, weapon, authority, player-control, or RTB state is not valid.

## Switching and recovery

**TAKE CONTROL** requires exactly one selected wingman. F10/F11 also switch when an eligible aircraft is available. In single-player, Tiltwing carrier cruise can operate when its conditions are met.

The ordinary Cargo Bay cradle workflow supports its normal recovery flow. Optional provider-owned ports may support their own recovery integration, but availability depends on the provider. There is no built-in Tarantula Slingload Hook port; do not assume CLX, provider, or multi-port availability.

## Multiplayer and limitations

Loyal Wingman is SP-first. Active map missions and aircraft switching are single-player only. Dedicated multiplayer is not accepted. Extended provider/CLX, multi-port, and combat-matrix coverage remains limited.

## Troubleshooting

For support, include both `BepInEx\LogOutput.log` and `%USERPROFILE%\AppData\LocalLow\Shockfront\NuclearOption\Player.log`.

Common visible rejection messages include **SELECT A HOSTILE AIRCRAFT**, **SELECT A HOSTILE SAM**, **SELECT A HOSTILE SURFACE SHIP**, **SELECT ONE SHARED MISSION**, and **COMMAND UNAVAILABLE**. Check the selection, target type, loadout, and current control or RTB state before trying again.
