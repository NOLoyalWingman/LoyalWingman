using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoyalWingman;

internal sealed class LoyalWingmanCradleWeapon : Weapon
{
    internal const int PhysicalRoundsPerCradle = FqCradleLoadoutLogic.PhysicalRoundsPerCradle;
    internal const int Capacity = PhysicalRoundsPerCradle;
    internal Hardpoint? PhysicalHardpoint { get; private set; }
    private Aircraft? carrier;
    private static readonly HashSet<WeaponStation> localLaunchLatches = new HashSet<WeaponStation>();

    public override void AttachToHardpoint(Aircraft aircraft, Hardpoint hardpoint, WeaponMount weaponMount)
    {
        base.AttachToHardpoint(aircraft, hardpoint, weaponMount);
        carrier = aircraft;
        PhysicalHardpoint = hardpoint;
        info = weaponMount.info;
        ammo = 1;
    }

    public override int GetAmmoLoaded() => ammo > 0 ? 1 : 0;
    public override int GetAmmoTotal() => ammo > 0 ? 1 : 0;
    public override int GetFullAmmo() => 1;

    private void Update()
    {
        if (weaponStation == null || !localLaunchLatches.Contains(weaponStation))
            return;
        try
        {
            if (!GameManager.playerInput.GetButton("Fire"))
                localLaunchLatches.Remove(weaponStation);
        }
        catch { }
    }

    private void OnDestroy()
    {
        if (weaponStation != null)
            localLaunchLatches.Remove(weaponStation);
    }

    public override void Rearm(int ammoToRearm, WeaponStation station)
    {
        weaponStation = station;
        ammo = CradleReleaseProgressionLogic.RearmCapacityOne(ammo, ammoToRearm);
        if (ammo > 0)
            localLaunchLatches.Remove(station);
    }

    internal static bool AdmitLocalLaunch(WeaponStation station, Unit owner)
    {
        bool hasCradle = station.Weapons.Exists(weapon => weapon is LoyalWingmanCradleWeapon);
        if (!hasCradle || !owner.HasAuthority)
            return true;
        try
        {
            if (!GameManager.playerInput.GetButton("Fire"))
                return true;
        }
        catch { return true; }
        return localLaunchLatches.Add(station);
    }

    public override void Fire(Unit owner, Unit target, Vector3 inheritedVelocity, WeaponStation station,
                              GlobalPosition aimpoint)
    {
        if (!Ready(owner, station, out string reason)) { Report(false, reason, station, -1, -1); return; }
        bool resolved = PayloadRegistry.TryResolveCradleRound(carrier!, this, out int physicalCradleId, out int physicalRound, out reason);
        bool accepted = false;
        if (CradleReleaseProgressionLogic.ShouldScheduleWingman(owner.IsServer, resolved))
        {
            try
            {
                accepted = NativeWeaponReleaseBridge.RequestRelease(carrier!, this, physicalCradleId, physicalRound, out reason);
            }
            catch (Exception e)
            {
                reason = "native_release_callback_" + e.GetType().Name;
            }
        }
        ammo = 0;
        CradleNativeReleasePath nativePath = CradleReleaseProgressionLogic.NativePath(owner.IsServer, owner.HasAuthority);
        Report(accepted, accepted ? "native_release_scheduled" : reason, station, physicalCradleId, physicalRound);
        if (nativePath == CradleNativeReleasePath.Rpc)
            carrier!.RpcLaunchMissile(station.Number, target, aimpoint);
        else if (nativePath == CradleNativeReleasePath.Cmd)
            carrier!.CmdLaunchMissile(station.Number, target, aimpoint);
    }

    private bool Ready(Unit owner, WeaponStation station, out string reason)
    {
        if (carrier == null || PhysicalHardpoint == null || owner != carrier || attachedUnit != carrier) reason = "owner_or_attachment";
        else if (weaponStation != station) reason = "station";
        else if (ammo <= 0) reason = "empty";
        else { reason = "ok"; return true; }
        return false;
    }

    private void Report(bool accepted, string reason, WeaponStation? station, int physicalCradleId, int physicalRound) => NativeWeaponReleaseBridge.Log(
        "state=native_weapon_release accepted=" + accepted + " reason=" + reason + " carrier_pid=" +
        (carrier == null ? "none" : carrier.persistentID.ToString()) + " physical_cradle=" + physicalCradleId + " physical_round=" +
        physicalRound + " station=" + (station == null ? "none" : station.Number.ToString()) + " ammo=" + GetAmmoTotal());
}
