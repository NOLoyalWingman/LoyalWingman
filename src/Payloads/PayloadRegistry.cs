using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Mirage;
using UnityEngine;

namespace LoyalWingman;

// Reflection is deliberately confined here: QOL/game releases expose these content types differently.
internal sealed class PayloadRegistry
{
    internal const string Key = "discord9_lw_fq106_cradle";
    private const BindingFlags Any =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly BepInEx.Logging.ManualLogSource log;
    private readonly string carrierKey;
    private readonly int fallbackIndex;
    private readonly float mass, cost, rcs, drag;
    private object? mount;
    private GameObject? templateParent;
    internal bool Ready { get; private set; }
    internal PayloadRegistry(BepInEx.Logging.ManualLogSource log, string carrierKey, int fallbackIndex, float mass,
                             float cost, float rcs, float drag)
    {
        this.log = log;
        this.carrierKey = carrierKey;
        this.fallbackIndex = fallbackIndex;
        this.mass = mass;
        this.cost = cost;
        this.rcs = rcs;
        this.drag = drag;
        NativeWeaponReleaseBridge.Configure(log);
    }
    internal bool EnsureRegistered()
    {
        if (Ready)
            return true;
        try
        {
            if (Encyclopedia.Lookup == null || !Encyclopedia.Lookup.TryGetValue(carrierKey, out UnitDefinition unit) ||
                !(unit is AircraftDefinition carrier) || carrier.unitPrefab == null)
                return false;
            WeaponManager? manager = carrier.unitPrefab.GetComponentInChildren<WeaponManager>(true);
            object? sets = Field(manager, "hardpointSets");
            int combined = FindCargoBay(sets);
            if (combined < 0)
            {
                log.LogError("[LoyalWingman] state=payload_register_failed reason=cargo_bay_missing");
                return false;
            }
            object? set = At(sets, combined);
            object? options = Field(set, "weaponOptions");
            if (Count(Field(set, "hardpoints")) <= 0)
                throw new InvalidOperationException("Combined Bay has no hardpoints");
            LogSets(sets, combined, options);
            mount = FindByKey(options, Key) ?? CreateProxyMount(options);
            if (mount == null)
            {
                log.LogError("[LoyalWingman] state=payload_register_failed reason=proxy_clone_missing");
                return false;
            }
            int hardpoints = Count(Field(set, "hardpoints"));
            float mountMass = mass / hardpoints, mountCost = cost / hardpoints, mountDrag = drag / hardpoints,
                  mountRcs = rcs / hardpoints;
            Set(mount, "jsonKey", Key);
            ((UnityEngine.Object)mount).name = Key;
            Set(mount, "mountName", "FQ-106 Loyal Wingman Four-Round External Cradle");
            Set(mount, "ammo", LoyalWingmanCradleWeapon.Capacity);
            Set(mount, "Cargo", true);
            Set(mount, "Troops", false);
            Set(mount, "radar", false);
            Set(mount, "turret", false);
            Set(mount, "countermeasure", false);
            Set(mount, "slingloadHook", false);
            Set(mount, "emptyMass", mountMass);
            Set(mount, "mass", mountMass);
            Set(mount, "emptyCost", mountCost);
            Set(mount, "drag", mountDrag);
            Set(mount, "emptyDrag", mountDrag);
            Set(mount, "RCS", mountRcs);
            Set(mount, "emptyRCS", mountRcs);
            Set(mount, "isEventContent", false);
            object? info = Field(mount, "info");
            if (info == null)
                throw new InvalidOperationException("mount info missing");
            Set(info, "weaponName", "FQ-106 Loyal Wingman Four-Round External Cradle");
            Set(info, "shortName", "FQ-106 Cradle x4");
            Set(info, "description",
                "Four-round external cradle. Each native Fire releases one FQ-106; accepted rounds are spent and are not refunded.");
            Set(info, "costPerRound", mountCost / LoyalWingmanCradleWeapon.Capacity);
            Set(info, "massPerRound", mountMass / LoyalWingmanCradleWeapon.Capacity);
            Set(info, "fireInterval", float.Epsilon);
            Set(info, "cargo", true);
            Set(info, "gun", false);
            Set(info, "sling", false);
            Set(info, "troops", false);
            Set(info, "weaponPrefab", null);
            GameObject? prefab = Field(mount, "prefab") as GameObject;
            LoyalWingmanCradleWeapon[] rounds = prefab == null ? Array.Empty<LoyalWingmanCradleWeapon>() : prefab.GetComponents<LoyalWingmanCradleWeapon>();
            if (rounds.Length != LoyalWingmanCradleWeapon.Capacity)
                throw new InvalidOperationException("probe_weapon_missing");
            for (int physicalRound = 0; physicalRound < rounds.Length; ++physicalRound)
                rounds[physicalRound].info = (WeaponInfo)info;
            CallIf(mount, "Initialize");
            Set(mount, "ammo", LoyalWingmanCradleWeapon.Capacity);
            if (!(Field(mount, "ammo") is int initializedAmmo) ||
                initializedAmmo != LoyalWingmanCradleWeapon.Capacity ||
                !(Field(mount, "Cargo") is bool cargo && cargo) ||
                !(Field(mount, "Troops") is bool troops && !troops) ||
                !(Field(mount, "radar") is bool radar && !radar) ||
                !(Field(mount, "turret") is bool turret && !turret) ||
                !(Field(mount, "countermeasure") is bool cm && !cm) ||
                !(Field(mount, "slingloadHook") is bool slingHook && !slingHook) ||
                !ReferenceEquals(Field(mount, "info"), rounds[0].info) ||
                !(Field(info, "fireInterval") is float interval) || interval <= 0f)
                throw new InvalidOperationException("proxy_validation_failed");
            AddUnique(options, mount);
            RegisterLookup((WeaponMount)mount);
            Ready = true;
            log.LogInfo("[LoyalWingman] state=payload_registered key=" + Key + " combinedIndex=" + combined +
                        " ammo=" + LoyalWingmanCradleWeapon.Capacity + " fireInterval=" + float.Epsilon);
            return true;
        }
        catch (Exception e)
        {
            log.LogError("[LoyalWingman] state=payload_register_failed error=" + e.GetType().Name);
            return false;
        }
    }
    // The runtime mount, not the serialized loadout selection, is the authoritative statement that this payload is
    // equipped.
    internal bool IsEquipped(Aircraft carrier, out int combinedIndex, out string reason)
    {
        combinedIndex = -1;
        reason = "payload_not_equipped";
        if (!Ready)
        {
            reason = "payload_not_registered";
            return false;
        }
        combinedIndex = FindCargoBay(carrier.weaponManager.hardpointSets);
        if (combinedIndex < 0)
            return false;
        WeaponMount? runtime = carrier.weaponManager.hardpointSets[combinedIndex].weaponMount;
        string? selected = carrier.Networkloadout?.weapons[combinedIndex]?.jsonKey;
        log.LogInfo("[LoyalWingman] state=payload_loadout selected=" + (selected ?? "null") +
                    " runtime=" + (runtime == null ? "null" : runtime.jsonKey));
        if (runtime == null || runtime.jsonKey != Key)
        {
            reason = selected == Key ? "payload_instance_missing" : "payload_not_equipped";
            return false;
        }
        return true;
    }
    internal static bool TryResolveCradleRound(Aircraft carrier, LoyalWingmanCradleWeapon firingRound,
                                               out int physicalCradleId, out int physicalRound, out string reason)
    {
        physicalCradleId = -1;
        physicalRound = -1;

        reason = "resolver_unknown";
        if (carrier == null || firingRound == null || firingRound.PhysicalHardpoint == null)
        { reason = "resolver_attachment_missing"; return false; }
        int cradle = 0;
        foreach (HardpointSet set in carrier.weaponManager.hardpointSets)
            foreach (Hardpoint hardpoint in set.hardpoints)
            {
                if (hardpoint.mount?.jsonKey != Key) continue;
                if (ReferenceEquals(hardpoint, firingRound.PhysicalHardpoint)) physicalCradleId = cradle;
                cradle++;
            }
        if (physicalCradleId < 0)
        { reason = "resolver_physical_hardpoint_not_found"; return false; }
        if (firingRound.PhysicalHardpoint.spawnedPrefab == null)
        { reason = "resolver_spawned_prefab_missing"; return false; }
        LoyalWingmanCradleWeapon[] rounds = firingRound.PhysicalHardpoint.spawnedPrefab
            .GetComponentsInChildren<LoyalWingmanCradleWeapon>(true);
        if (rounds.Length != LoyalWingmanCradleWeapon.Capacity)
        { reason = "resolver_component_count_" + rounds.Length; return false; }
        int matches = 0;
        for (int index = 0; index < rounds.Length; ++index)
            if (ReferenceEquals(rounds[index], firingRound)) { physicalRound = index; matches++; }
        if (matches != 1)
        { reason = matches == 0 ? "resolver_round_not_found" : "resolver_round_ambiguous"; return false; }
        reason = "ok";
        return true;
    }
    private int FindCargoBay(object? sets)
    {
        int i = 0;
        foreach (object set in Items(sets))
        {
            string name = (Field(set, "name") as string) ?? "";
            if (name.IndexOf("Cargo Bay", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Combined Bay", StringComparison.OrdinalIgnoreCase) >= 0)
                return i;
            i++;
        }
        return fallbackIndex >= 0 && fallbackIndex < Count(sets) ? fallbackIndex : -1;
    }
    private object? CreateProxyMount(object? options)
    {
        object? donor = null;
        foreach (object x in Items(options))
            if (x is UnityEngine.Object && Field(x, "info") is UnityEngine.Object)
            {
                donor = x;
                break;
            }
        if (donor == null)
            return null;
        object clone = UnityEngine.Object.Instantiate((UnityEngine.Object)donor);
        object info = UnityEngine.Object.Instantiate((UnityEngine.Object)Field(donor, "info")!);
        if (templateParent == null)
        {
            templateParent = new GameObject("LW_Cradle_TemplateRoot");
            templateParent.transform.position = new Vector3(0f, -10000f, 0f);
            templateParent.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(templateParent);
        }
        GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
        visual.name = "LW_FQ106_Cradle_VisualTemplate";
        visual.transform.SetParent(templateParent.transform, false);
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localRotation = Quaternion.identity;
        visual.transform.localScale = new Vector3(3f, .5f, 8f);
        UnityEngine.Object.Destroy(visual.GetComponent<Collider>());
        for (int physicalRound = 0; physicalRound < LoyalWingmanCradleWeapon.Capacity; ++physicalRound)
            visual.AddComponent<LoyalWingmanCradleWeapon>();
        if (visual.transform.localPosition != Vector3.zero || visual.GetComponent<Missile>() != null ||
            visual.GetComponent<Unit>() != null || visual.GetComponent<NetworkIdentity>() != null ||
            visual.GetComponent<NetworkBehaviour>() != null)
            throw new InvalidOperationException("unsafe template");
        log.LogInfo("[LoyalWingman] state=payload_template localPosition=" + visual.transform.localPosition +
                    " components=" + visual.GetComponents<Component>().Length);
        Set(clone, "info", info);
        Set(clone, "prefab", visual);
        Set(info, "weaponPrefab", null);
        return clone;
    }
    private void RegisterLookup(WeaponMount value)
    {
        Encyclopedia[] candidates = Resources.FindObjectsOfTypeAll<Encyclopedia>();
        log.LogInfo("[LoyalWingman] state=encyclopedia_candidates count=" + candidates.Length);
        if (candidates.Length != 1)
            throw new InvalidOperationException("Encyclopedia ambiguous");
        Encyclopedia encyclopedia = candidates[0];
        WeaponMount registered;
        if (Encyclopedia.WeaponLookup.ContainsKey(Key))
        {
            registered = Encyclopedia.WeaponLookup[Key];
            if (registered == null || registered.jsonKey != Key)
                throw new InvalidOperationException("WeaponLookup key collision");
            mount = registered;
        }
        else
        {
            encyclopedia.weaponMounts.Add(value);
            Encyclopedia.WeaponLookup.Add(Key, value);
            registered = value;
        }
        if (!encyclopedia.weaponMounts.Contains(registered))
            encyclopedia.weaponMounts.Add(registered);
        if (!encyclopedia.IndexLookup.Contains((INetworkDefinition)registered))
        {
            int index = encyclopedia.IndexLookup.Count;
            encyclopedia.IndexLookup.Add((INetworkDefinition)registered);
            ((INetworkDefinition)registered).LookupIndex = index;
        }
    }
    private void LogSets(object? sets, int combined, object? options)
    {
        int i = 0;
        foreach (object s in Items(sets))
        {
            log.LogInfo("[LoyalWingman] payload hardpoint index=" + i + " name=" + (Field(s, "name") ?? "null"));
            i++;
        }
        foreach (object o in Items(options))
            log.LogInfo("[LoyalWingman] payload option key=" + (Field(o, "jsonKey") ?? "null"));
        log.LogInfo("[LoyalWingman] payload combinedIndex=" + combined + " hardpointSets=" + Count(sets));
    }
    private static object? Field(object? o, string n) => o?.GetType().GetField(n, Any)?.GetValue(o);
    private static void Set(object o, string n, object? v)
    {
        FieldInfo? f = o.GetType().GetField(n, Any);
        if (f == null || f.IsInitOnly)
            throw new MissingFieldException(o.GetType().Name, n);
        f.SetValue(o, v);
    }
    private static void CallIf(object o,
                               string n) => o.GetType().GetMethod(n, Any, null, Type.EmptyTypes, null)?.Invoke(o, null);
    private static IEnumerable Items(object? o) => o as IEnumerable ?? Array.Empty<object>();
    private static int Count(object? o)
    {
        int n = 0;
        foreach (object _ in Items(o))
            n++;
        return n;
    }
    private static object? At(object? o, int index)
    {
        int i = 0;
        foreach (object x in Items(o))
            if (i++ == index)
                return x;
        return null;
    }
    private static object? FindByKey(object? o, string key)
    {
        foreach (object x in Items(o))
            if (Field(x, "jsonKey") as string == key)
                return x;
        return null;
    }
    private static void AddUnique(object? list, object value)
    {
        if (list == null)
            throw new MissingMemberException("list");
        foreach (object x in Items(list))
            if (ReferenceEquals(x, value) || (Field(x, "jsonKey") as string) == (Field(value, "jsonKey") as string))
                return;
        list.GetType().GetMethod("Add", Any)?.Invoke(list, new[] { value });
    }
}
