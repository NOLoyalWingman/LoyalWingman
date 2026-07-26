using System;
using BepInEx.Logging;
using Mirage;
using UnityEngine;

namespace LoyalWingman;

internal sealed class DroneCarrierPortRegistry
{
    internal const string Key = "discord9_lw_drone_carrier_port_proto";
    internal static WeaponMount? GenericMount { get; private set; }

    private readonly ManualLogSource log;
    internal bool Ready { get; private set; }
    internal DroneCarrierPortRegistry(ManualLogSource log) => this.log = log;

    internal bool EnsureRegistered()
    {
        if (Ready) return true;
        if (!TryPublish(out WeaponMount? mount) || mount is null) return false;
        if (!AnimatedDronePortProvider.Install(mount, log))
            log.LogWarning("[LoyalWingman] animated port provider integration unavailable");
        Ready = true;
        log.LogInfo("[LoyalWingman] state=drone_port_registered key=" + Key);
        return true;
    }

    private bool TryPublish(out WeaponMount? mount)
    {
        mount = null;
        WeaponMount? createdMount = null;
        WeaponInfo? createdInfo = null;
        GameObject? createdVisual = null;
        Encyclopedia? encyclopedia = null;
        try
        {
            if (!Encyclopedia.WeaponLookup.TryGetValue(PayloadRegistry.Key, out WeaponMount donor) || donor.info == null)
                throw new InvalidOperationException("donor");
            Encyclopedia[] encyclopedias = Resources.FindObjectsOfTypeAll<Encyclopedia>();
            if (encyclopedias.Length != 1) throw new InvalidOperationException("encyclopedia");
            encyclopedia = encyclopedias[0];
            if (Encyclopedia.WeaponLookup.TryGetValue(Key, out WeaponMount registered))
            {
                if (!ValidatePublished(encyclopedia, registered, donor)) throw new InvalidOperationException("key_collision");
                GenericMount = registered;
                mount = registered;
                return true;
            }

            WeaponMount published = UnityEngine.Object.Instantiate(donor);
            WeaponInfo info = UnityEngine.Object.Instantiate(donor.info);
            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            createdMount = published;
            createdInfo = info;
            createdVisual = visual;
            visual.name = "LW_DroneCarrierPort_Prototype";
            visual.hideFlags = HideFlags.HideAndDontSave;
            visual.transform.position = new Vector3(0f, -10000f, 0f);
            visual.transform.localScale = new Vector3(.5f, .25f, .8f);
            UnityEngine.Object.Destroy(visual.GetComponent<Collider>());
            UnityEngine.Object.DontDestroyOnLoad(visual);
            DroneCarrierPortWeapon port = visual.AddComponent<DroneCarrierPortWeapon>();
            published.jsonKey = Key;
            published.name = Key;
            published.mountName = "Loyal Wingman Drone Carrier Port";
            published.prefab = visual;
            published.info = info;
            published.ammo = 1;
            published.Cargo = published.Troops = published.radar = published.turret = published.countermeasure = published.slingloadHook = false;
            published.emptyMass = published.mass = 50f;
            published.emptyCost = 1f;
            published.drag = published.emptyDrag = .1f;
            published.RCS = published.emptyRCS = .1f;
            info.weaponName = "Drone Carrier Port";
            info.shortName = "DRONE PORT";
            info.description = "SP prototype port";
            info.cargo = info.gun = info.sling = info.troops = false;
            info.weaponPrefab = null;
            info.fireInterval = .01f;
            port.info = info;
            published.Initialize();
            if (!ReferenceEquals(published.info, info) || ReferenceEquals(donor, published) || ReferenceEquals(donor.info, info) ||
                visual.GetComponents<DroneCarrierPortWeapon>().Length != 1 || visual.GetComponent<Unit>() != null ||
                visual.GetComponent<Missile>() != null || visual.GetComponent<NetworkIdentity>() != null ||
                visual.GetComponent<NetworkBehaviour>() != null || published.Cargo || published.Troops || published.radar || published.turret ||
                published.countermeasure || published.slingloadHook || published.ammo != 1 || info.cargo || info.gun || info.sling || info.troops)
                throw new InvalidOperationException("port_validation");
            encyclopedia.weaponMounts.Add(published);
            Encyclopedia.WeaponLookup.Add(Key, published);
            ((INetworkDefinition)published).LookupIndex = encyclopedia.IndexLookup.Count;
            encyclopedia.IndexLookup.Add(published);
            if (!ValidatePublished(encyclopedia, published, donor)) throw new InvalidOperationException("lookup_validation");
            GenericMount = published;
            mount = published;
            return true;
        }
        catch (Exception exception)
        {
            if (createdMount != null)
            {
                if (Encyclopedia.WeaponLookup.TryGetValue(Key, out WeaponMount registered) && ReferenceEquals(registered, createdMount))
                    Encyclopedia.WeaponLookup.Remove(Key);
                encyclopedia?.weaponMounts.Remove(createdMount);
                encyclopedia?.IndexLookup.Remove((INetworkDefinition)createdMount);
            }
            if (createdVisual != null) UnityEngine.Object.Destroy(createdVisual);
            if (createdInfo != null) UnityEngine.Object.Destroy(createdInfo);
            if (createdMount != null) UnityEngine.Object.Destroy(createdMount);
            log.LogError("[LoyalWingman] state=drone_port_register_failed reason=" + exception.GetType().Name);
            return false;
        }
    }

    private static bool ValidatePublished(Encyclopedia encyclopedia, WeaponMount mount, WeaponMount donor)
    {
        if (mount == null || mount.jsonKey != Key || ReferenceEquals(mount, donor) || mount.info == null ||
            ReferenceEquals(mount.info, donor.info) || mount.prefab == null || mount.Cargo || mount.Troops || mount.radar ||
            mount.turret || mount.countermeasure || mount.slingloadHook || mount.ammo != 1 || mount.info.cargo || mount.info.gun ||
            mount.info.sling || mount.info.troops || !Encyclopedia.WeaponLookup.TryGetValue(Key, out WeaponMount indexed) ||
            !ReferenceEquals(indexed, mount)) return false;
        GameObject visual = mount.prefab;
        DroneCarrierPortWeapon[] ports = visual.GetComponentsInChildren<DroneCarrierPortWeapon>(true);
        Weapon[] weapons = visual.GetComponentsInChildren<Weapon>(true);
        if (ports.Length != 1 || weapons.Length != 1 || !ReferenceEquals(weapons[0], ports[0]) ||
            visual.GetComponentsInChildren<Unit>(true).Length != 0 || visual.GetComponentsInChildren<Missile>(true).Length != 0 ||
            visual.GetComponentsInChildren<NetworkIdentity>(true).Length != 0 || visual.GetComponentsInChildren<NetworkBehaviour>(true).Length != 0 ||
            !ReferenceEquals(ports[0].info, mount.info) || mount.info.weaponPrefab != null ||
            !float.IsFinite(mount.info.fireInterval) || mount.info.fireInterval <= 0f) return false;
        int mountReference = 0, mountKey = 0, indexReference = 0, indexKey = 0;
        foreach (WeaponMount value in encyclopedia.weaponMounts)
        { if (ReferenceEquals(value, mount)) ++mountReference; if (value != null && value.jsonKey == Key) ++mountKey; }
        foreach (INetworkDefinition value in encyclopedia.IndexLookup)
            if (value is WeaponMount indexedMount)
            { if (ReferenceEquals(indexedMount, mount)) ++indexReference; if (indexedMount.jsonKey == Key) ++indexKey; }
        int? lookupIndex = ((INetworkDefinition)mount).LookupIndex;
        return mountReference == 1 && mountKey == 1 && indexReference == 1 && indexKey == 1 &&
               lookupIndex.HasValue && lookupIndex.Value >= 0 && lookupIndex.Value < encyclopedia.IndexLookup.Count &&
               ReferenceEquals(encyclopedia.IndexLookup[lookupIndex.Value], mount);
    }

}
