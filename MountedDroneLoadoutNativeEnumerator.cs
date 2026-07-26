using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using UnityEngine;

namespace LoyalWingman;

internal static class MountedDroneLoadoutNativeEnumerator
{
    internal static bool TryBuildRequestedLoadout(Player player, AircraftDefinition definition,
        NuclearOption.SavedMission.Loadout currentLoadout, Aircraft carrier, string[] requestedMountKeys,
        out NuclearOption.SavedMission.Loadout requestedLoadout, out string[] normalizedMountKeys, out string reason)
    {
        requestedLoadout = new NuclearOption.SavedMission.Loadout { weapons = new List<WeaponMount>() };
        normalizedMountKeys = Array.Empty<string>(); reason = "definition_invalid";
        try
        {
            Aircraft? prefab = definition == null || definition.unitPrefab == null ? null : definition.unitPrefab.GetComponent<Aircraft>();
            if (player == null || carrier == null || prefab == null || prefab.weaponManager == null || prefab.weaponManager.hardpointSets == null ||
                currentLoadout == null || currentLoadout.weapons == null) return false;
            HardpointSet[] sets = prefab.weaponManager.hardpointSets;
            if (requestedMountKeys == null || sets.Length != currentLoadout.weapons.Count || sets.Length != requestedMountKeys.Length)
            { reason = "slot_count_mismatch"; return false; }
            FactionHQ? hq = carrier.NetworkHQ;
            Airbase? airbase = hq == null ? null : hq.GetNearestAirbase(carrier.transform.position);
            if (airbase == null || airbase.CurrentHQ != hq) { reason = "airbase_unavailable"; return false; }
            normalizedMountKeys = new string[sets.Length];
            List<WeaponMount> available = new List<WeaponMount>();
            for (int slot = 0; slot < sets.Length; slot++)
            {
                HardpointSet? set = sets[slot];
                if (set == null) { reason = "hardpoint_invalid"; return Failed(out requestedLoadout, out normalizedMountKeys, reason); }
                string? requested = requestedMountKeys[slot];
                if (requested == null || !MountedDroneLoadoutLogic.TryNormalizeKey(requested, out string key))
                { reason = "requested_key_invalid"; return Failed(out requestedLoadout, out normalizedMountKeys, reason); }
                normalizedMountKeys[slot] = key;
                if (key.Length == 0) { requestedLoadout.weapons.Add(null!); continue; }
                WeaponChecker.GetAvailableWeaponsNonAlloc(player, set, airbase, hq, false, available);
                WeaponMount? selected = null;
                foreach (WeaponMount mount in available)
                {
                    if (mount == null || Unsupported(mount)) continue;
                    if (!MountedDroneLoadoutLogic.TryNormalizeKey(mount.jsonKey, out string mountKey))
                    { reason = "mount_key_invalid"; return Failed(out requestedLoadout, out normalizedMountKeys, reason); }
                    if (!string.Equals(mountKey, key, StringComparison.Ordinal)) continue;
                    if (selected != null && !ReferenceEquals(selected, mount))
                    { reason = "ambiguous_weapon_key"; return Failed(out requestedLoadout, out normalizedMountKeys, reason); }
                    selected = mount;
                }
                if (selected == null) { reason = "mount_not_available"; return Failed(out requestedLoadout, out normalizedMountKeys, reason); }
                requestedLoadout.weapons.Add(selected);
            }
            for (int slot = 0; slot < sets.Length; slot++)
                if (!WeaponChecker.MountAllowedConflict(sets[slot], requestedLoadout))
                { reason = "mount_conflict"; return Failed(out requestedLoadout, out normalizedMountKeys, reason); }
            reason = "ok"; return true;
        }
        catch { reason = "weapon_data_unavailable"; return Failed(out requestedLoadout, out normalizedMountKeys, reason); }
    }
    internal static bool TryEnumerate(Player player, AircraftDefinition definition, NuclearOption.SavedMission.Loadout currentLoadout,
                                      Aircraft carrier, out string[] currentMountKeys, out string[][] legalMountKeysBySlot,
                                      out string reason)
    {
        currentMountKeys = Array.Empty<string>(); legalMountKeysBySlot = Array.Empty<string[]>(); reason = "definition_invalid";
        try
        {
            Aircraft? prefab = definition == null || definition.unitPrefab == null ? null : definition.unitPrefab.GetComponent<Aircraft>();
            if (player == null || carrier == null || prefab == null || prefab.weaponManager == null || prefab.weaponManager.hardpointSets == null ||
                currentLoadout == null || currentLoadout.weapons == null) return false;
            HardpointSet[] sets = prefab.weaponManager.hardpointSets;
            if (sets.Length != currentLoadout.weapons.Count) { reason = "slot_count_mismatch"; return false; }
            FactionHQ? hq = carrier.NetworkHQ;
            Airbase? airbase = hq == null ? null : hq.GetNearestAirbase(carrier.transform.position);
            if (airbase == null || airbase.CurrentHQ != hq) { reason = "airbase_unavailable"; return false; }
            currentMountKeys = new string[sets.Length]; legalMountKeysBySlot = new string[sets.Length][];
            List<WeaponMount> available = new List<WeaponMount>();
            for (int slot = 0; slot < sets.Length; slot++)
            {
                HardpointSet? set = sets[slot];
                if (set == null) { reason = "hardpoint_invalid"; return Empty(out currentMountKeys, out legalMountKeysBySlot, reason); }
                WeaponMount? current = currentLoadout.weapons[slot];
                if (current != null)
                {
                    if (Unsupported(current) || !MountedDroneLoadoutLogic.TryNormalizeKey(current.jsonKey, out string currentKey))
                    { reason = "current_key_invalid"; return Empty(out currentMountKeys, out legalMountKeysBySlot, reason); }
                    currentMountKeys[slot] = currentKey;
                }
                else currentMountKeys[slot] = string.Empty;
                if (!WeaponChecker.MountAllowedConflict(set, currentLoadout))
                { legalMountKeysBySlot[slot] = new[] { string.Empty }; continue; }
                WeaponChecker.GetAvailableWeaponsNonAlloc(player, set, airbase, hq, false, available);
                Dictionary<string, WeaponMount> byKey = new Dictionary<string, WeaponMount>(StringComparer.Ordinal);
                List<string?> keys = new List<string?>();
                foreach (WeaponMount mount in available)
                {
                    if (mount == null || Unsupported(mount)) continue;
                    if (!MountedDroneLoadoutLogic.TryNormalizeKey(mount.jsonKey, out string key))
                    { reason = "mount_key_invalid"; return Empty(out currentMountKeys, out legalMountKeysBySlot, reason); }
                    if (byKey.TryGetValue(key, out WeaponMount prior) && !ReferenceEquals(prior, mount))
                    { reason = "ambiguous_weapon_key"; return Empty(out currentMountKeys, out legalMountKeysBySlot, reason); }
                    byKey[key] = mount; keys.Add(key);
                }
                if (!MountedDroneLoadoutLogic.TrySnapshotLegalKeys(keys, out legalMountKeysBySlot[slot]))
                { reason = "mount_key_invalid"; return Empty(out currentMountKeys, out legalMountKeysBySlot, reason); }
            }
            reason = "ok"; return true;
        }
        catch { reason = "weapon_data_unavailable"; return Empty(out currentMountKeys, out legalMountKeysBySlot, reason); }
    }
    private static bool Unsupported(WeaponMount mount) => mount.Cargo || mount.Troops || mount.slingloadHook ||
        (mount.info != null && (mount.info.nuclear || mount.info.cargo || mount.info.troops || mount.info.sling || mount.info.rearmShip));
    private static bool Empty(out string[] current, out string[][] legal, string _) { current = Array.Empty<string>(); legal = Array.Empty<string[]>(); return false; }
    private static bool Failed(out NuclearOption.SavedMission.Loadout loadout, out string[] keys, string _)
    { loadout = new NuclearOption.SavedMission.Loadout { weapons = new List<WeaponMount>() }; keys = Array.Empty<string>(); return false; }
}
