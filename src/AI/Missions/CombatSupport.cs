using NuclearOption.Networking;
using UnityEngine;

namespace LoyalWingman;

// Physical launch gating is separate from the game's AnalyzeTarget opportunity decision.
internal static class CombatSupport
{
    internal static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
    internal static bool Finite(Vector3 x) => Finite(x.x) && Finite(x.y) && Finite(x.z);
    internal static bool IsHostile(Aircraft friendly, Aircraft target)
    {
        try
        {
            return friendly != null && target != null && friendly.NetworkHQ != null && target.NetworkHQ != null &&
                   friendly.NetworkHQ.faction != null && target.NetworkHQ.faction != null &&
                   friendly.NetworkHQ != target.NetworkHQ && friendly.NetworkHQ.faction != target.NetworkHQ.faction;
        }
        catch
        {
            return false;
        }
    }
    internal static bool ValidHostileTrackedAircraft(Aircraft friendly, Aircraft target)
    {
        try
        {
            return ValidHostileAircraft(friendly, target) && friendly.NetworkHQ != null &&
                   friendly.NetworkHQ.trackingDatabase.TryGetValue(target.persistentID, out TrackingInfo tracking) &&
                   tracking.id == target.persistentID && tracking.TryGetUnit(out Unit tracked) && tracked == target;
        }
        catch
        {
            return false;
        }
    }
    internal static bool ValidHostileTrackedUnit(Aircraft friendly, Unit? target)
    {
        try
        {
            FactionHQ? hq = friendly?.NetworkHQ;
            return hq != null && ValidHostileTrackedUnit(hq, target);
        }
        catch { return false; }
    }
    internal static bool ValidHostileShip(Aircraft friendly, Unit target) { try { FactionHQ? hq = friendly?.NetworkHQ; return hq != null && ValidHostileShip(hq, target); } catch { return false; } }
    internal static bool ValidHostileAircraft(Aircraft friendly, Aircraft target) { try { FactionHQ? hq = friendly?.NetworkHQ; return hq != null && ValidHostileAircraft(hq, target); } catch { return false; } }
    internal static bool ValidHostileAircraft(FactionHQ friendly, Aircraft target) => ValidAircraft(target) && friendly != null && target.NetworkHQ != null && friendly.faction != null && target.NetworkHQ.faction != null && friendly != target.NetworkHQ && friendly.faction != target.NetworkHQ.faction;
    internal static bool ValidHostileTrackedUnit(FactionHQ friendly, Unit? target)
    {
        try { return friendly != null && target != null && !target.disabled && target.gameObject.activeInHierarchy && target.Identity != null && target.Identity.IsSpawned && target.Identity.NetId != 0 && !target.persistentID.Equals(default(PersistentID)) && target.NetworkHQ != null && friendly.faction != null && target.NetworkHQ.faction != null && friendly != target.NetworkHQ && friendly.faction != target.NetworkHQ.faction && UnitRegistry.TryGetUnit(target.persistentID, out Unit resolved) && resolved == target && friendly.trackingDatabase.TryGetValue(target.persistentID, out TrackingInfo tracking) && tracking.id == target.persistentID && tracking.TryGetUnit(out Unit tracked) && tracked == target; }
        catch { return false; }
    }
    internal static bool ValidHostileGround(Aircraft friendly, Unit target) =>
        (target is GroundVehicle || target is Building) && ValidHostileTrackedUnit(friendly, target);
    internal static bool ValidHostileGround(FactionHQ friendly, Unit target) =>
        (target is GroundVehicle || target is Building) && ValidHostileTrackedUnit(friendly, target);
    internal static bool ValidHostileCasTarget(Aircraft friendly, Unit target)
    {
        bool isAircraft = target is Aircraft;
        bool aircraftAirborne = target is Aircraft aircraft && aircraft.airborne;
        return CasMissionLogic.IsCasTargetCategory(target is GroundVehicle, target is Building, isAircraft,
                                                   aircraftAirborne) &&
               (isAircraft ? ValidHostileTrackedAircraft(friendly, (Aircraft)target) : ValidHostileTrackedUnit(friendly, target));
    }
    internal static bool ValidHostileSam(Aircraft friendly, Unit target)
    {
        if (!ValidHostileGround(friendly, target)) return false;
        try
        {
            foreach (WeaponStation station in target.weaponStations)
                if (station?.WeaponInfo != null && station.WeaponInfo.missile && station.WeaponInfo.effectiveness.antiAir > 0f)
                    return true;
        }
        catch { }
        return false;
    }
    internal static bool ValidHostileSam(FactionHQ friendly, Unit target)
    {
        if (!ValidHostileGround(friendly, target)) return false;
        try { foreach (WeaponStation station in target.weaponStations) if (station?.WeaponInfo != null && station.WeaponInfo.missile && station.WeaponInfo.effectiveness.antiAir > 0f) return true; } catch { }
        return false;
    }
    internal static bool ValidHostileShip(FactionHQ friendly, Unit target) => target is Ship ship && ValidHostileTrackedUnit(friendly, ship);
    internal static bool ValidAircraft(Aircraft a)
    {
        try
        {
            return a != null && !a.disabled && !a.HasEjected() && a.unitState != Unit.UnitState.Abandoned &&
                   a.unitState != Unit.UnitState.Returned && a.gameObject.activeInHierarchy && a.Identity != null &&
                   a.Identity.IsSpawned && a.Identity.NetId != 0 && a.rb != null &&
                   UnitRegistry.TryGetPersistentUnit(a.persistentID, out PersistentUnit u) && u != null &&
                   u.unit == a && Finite(a.transform.position) && Finite(a.rb.velocity) && Finite(a.radarAlt);
        }
        catch
        {
            return false;
        }
    }
    internal static bool Runtime(Aircraft a, PilotBaseState state)
    {
        try
        {
            return ValidAircraft(a) && GameManager.gameState == GameState.SinglePlayer &&
                   NetworkManagerNuclearOption.i != null && NetworkManagerNuclearOption.i.Server.Active && a.IsServer &&
                   a.LocalSim && a.pilots != null && a.pilots.Length > 0 && a.pilots[0] != null &&
                   a.pilots[0].pilotType == Pilot.PilotType.Plane && a.pilots[0].currentState == state &&
                   a.autopilot is AutopilotPlane;
        }
        catch
        {
            return false;
        }
    }
    internal static bool StationHasSeeker(WeaponStation station, bool irOnly)
    {
        try
        {
            if (station == null || station.WeaponInfo == null || station.WeaponInfo.weaponPrefab == null)
                return false;
            bool hasIr = station.WeaponInfo.weaponPrefab.GetComponent<IRSeeker>() != null;
            bool hasArh = station.WeaponInfo.weaponPrefab.GetComponent<ARHSeeker>() != null;
            return CombatCommandLogic.IsSupportedSeeker(hasIr, hasArh, irOnly);
        }
        catch
        {
            return false;
        }
    }
    internal static bool AirToAirStationCompatible(WeaponStation station)
    {
        try
        {
            if (station == null || station.WeaponInfo?.weaponPrefab == null)
                return false;
            WeaponInfo info = station.WeaponInfo;
            var prefab = info.weaponPrefab;
            return CombatCommandLogic.IsAirToAirStation(info.missile, prefab.GetComponent<IRSeeker>() != null,
                                                        prefab.GetComponent<ARHSeeker>() != null);
        }
        catch
        {
            return false;
        }
    }
    internal static bool StationSafe(Aircraft aircraft, WeaponStation station, bool irOnly)
    {
        try
        {
            return aircraft != null && station != null && aircraft.weaponStations != null &&
                   aircraft.weaponStations.Contains(station) && station.WeaponInfo != null &&
                   station.WeaponInfo.missile && !station.WeaponInfo.gun && !station.WeaponInfo.bomb &&
                   !station.WeaponInfo.glideBomb && !station.WeaponInfo.sling && !station.WeaponInfo.troops &&
                   !station.Cargo && !station.Reloading && !station.SalvoInProgress && station.Ammo > 0 &&
                   station.Ready() && !station.SafetyIsOn(aircraft) && StationHasSeeker(station, irOnly);
        }
        catch
        {
            return false;
        }
    }
    internal static SeadWeaponKind GetSeadWeaponKind(WeaponStation station)
    {
        try
        {
            if (station == null || station.WeaponInfo?.weaponPrefab == null) return SeadWeaponKind.None;
            WeaponInfo info = station.WeaponInfo; var prefab = info.weaponPrefab;
            return SeadMissionLogic.Classify(info.missile, info.gun, info.bomb, info.glideBomb, station.Cargo, info.sling,
                info.troops, prefab.GetComponent<IRSeeker>() != null, prefab.GetComponent<ARHSeeker>() != null,
                prefab.GetComponent<ARMSeeker>() != null, prefab.GetComponent<OpticalSeeker>() != null,
                info.targetRequirements.lineOfSight, prefab.GetComponent<OpticalSeekerCruiseMissile>() != null,
                info.laserGuided, prefab.GetComponent<LaserSeeker>() != null);
        }
        catch { return SeadWeaponKind.None; }
    }
    internal static bool SeadStationCompatible(Aircraft aircraft, WeaponStation station)
    {
        SeadWeaponKind kind = GetSeadWeaponKind(station);
        return kind != SeadWeaponKind.None &&
               (kind != SeadWeaponKind.LaserRocket || aircraft != null && aircraft.GetLaserDesignator() != null);
    }
    internal static AntiShipWeaponKind GetAntiShipWeaponKind(WeaponStation station)
    {
        try
        {
            if (station == null || station.WeaponInfo?.weaponPrefab == null)
                return AntiShipWeaponKind.None;
            WeaponInfo info = station.WeaponInfo;
            var prefab = info.weaponPrefab;
            return StrikeMissionLogic.ResolveAntiShipWeaponKind(
                prefab.GetComponent<OpticalSeekerCruiseMissile>() != null,
                prefab.GetComponent<OpticalSeeker>() != null,
                info.targetRequirements.lineOfSight, info.laserGuided, prefab.GetComponent<LaserSeeker>() != null);
        }
        catch { return AntiShipWeaponKind.None; }
    }
    internal static bool AntiShipStationCompatible(Aircraft aircraft, WeaponStation station)
    {
        AntiShipWeaponKind kind = GetAntiShipWeaponKind(station);
        return kind != AntiShipWeaponKind.None &&
               (kind != AntiShipWeaponKind.LaserRocket || aircraft != null && aircraft.GetLaserDesignator() != null);
    }
    internal static bool SeadStationSafe(Aircraft aircraft, WeaponStation station) =>
        StationStructurallySafe(aircraft, station) && SeadStationCompatible(aircraft, station);
    internal static bool AntiShipStationSafe(Aircraft aircraft, WeaponStation station)
    {
        try
        {
            return aircraft != null && station != null && aircraft.weaponStations != null &&
                   aircraft.weaponStations.Contains(station) && station.WeaponInfo?.weaponPrefab != null &&
                   GetAntiShipWeaponKind(station) != AntiShipWeaponKind.None && !station.WeaponInfo.gun &&
                   !station.WeaponInfo.bomb && !station.WeaponInfo.glideBomb && !station.WeaponInfo.sling &&
                   !station.WeaponInfo.troops && !station.Cargo && !station.Reloading && !station.SalvoInProgress &&
                   station.Ammo > 0 && station.Ready() && !station.SafetyIsOn(aircraft) &&
                   AntiShipStationCompatible(aircraft, station);
        }
        catch { return false; }
    }
    internal static CasWeaponKind GetCasWeaponKind(WeaponStation station)
    {
        try
        {
            if (station == null || station.WeaponInfo == null) return CasWeaponKind.None;
            WeaponInfo info = station.WeaponInfo; var prefab = info.weaponPrefab;
            return CasMissionLogic.Classify(info.missile, info.gun, info.boresight, info.bomb, info.glideBomb,
                station.Cargo, info.sling, info.troops, prefab != null && prefab.GetComponent<IRSeeker>() != null,
                prefab != null && prefab.GetComponent<ARHSeeker>() != null, prefab != null && prefab.GetComponent<ARMSeeker>() != null,
                prefab != null && prefab.GetComponent<OpticalSeeker>() != null, prefab != null && prefab.GetComponent<OpticalSeekerCruiseMissile>() != null,
                info.targetRequirements.lineOfSight, info.laserGuided, prefab != null && prefab.GetComponent<LaserSeeker>() != null,
                info.effectiveness.antiSurface, station.TurretCount());
        }
        catch { return CasWeaponKind.None; }
    }
    internal static bool CasStationCompatible(Aircraft aircraft, WeaponStation station)
    {
        CasWeaponKind kind = GetCasWeaponKind(station);
        return kind != CasWeaponKind.None &&
               (kind != CasWeaponKind.LaserRocket || aircraft != null && aircraft.GetLaserDesignator() != null);
    }
    internal static bool CasStationSafe(Aircraft aircraft, WeaponStation station)
    {
        try
        {
            return aircraft != null && station != null && aircraft.weaponStations != null &&
                   aircraft.weaponStations.Contains(station) && station.WeaponInfo != null &&
                   GetCasWeaponKind(station) != CasWeaponKind.None && !station.WeaponInfo.bomb &&
                   !station.WeaponInfo.glideBomb && !station.WeaponInfo.sling && !station.WeaponInfo.troops &&
                   !station.Cargo && !station.Reloading && !station.SalvoInProgress && station.Ammo > 0 &&
                   station.Ready() && !station.SafetyIsOn(aircraft) && CasStationCompatible(aircraft, station);
        }
        catch { return false; }
    }
    internal static StrikeBombWeaponKind GetStrikeBombWeaponKind(WeaponStation station)
    {
        try
        {
            if (station == null || station.WeaponInfo == null) return StrikeBombWeaponKind.None;
            WeaponInfo info = station.WeaponInfo;
            if (info.nuclear) return StrikeBombWeaponKind.None;
            return StrikeBombingLogic.Classify(info.bomb, info.glideBomb, info.nuclear, info.missile, info.gun,
                info.laserGuided, info.cargo || station.Cargo, info.sling, info.troops);
        }
        catch { return StrikeBombWeaponKind.None; }
    }
    internal static bool StrikeBombStationCompatible(WeaponStation station) =>
        GetStrikeBombWeaponKind(station) != StrikeBombWeaponKind.None;
    internal static bool StrikeBombStationSafe(Aircraft aircraft, WeaponStation station)
    {
        try
        {
            return aircraft != null && station != null && aircraft.weaponStations != null &&
                   aircraft.weaponStations.Contains(station) && station.WeaponInfo != null &&
                   GetStrikeBombWeaponKind(station) != StrikeBombWeaponKind.None && !station.WeaponInfo.nuclear &&
                   !station.WeaponInfo.missile && !station.WeaponInfo.gun && !station.WeaponInfo.laserGuided &&
                   !station.WeaponInfo.cargo && !station.Cargo && !station.WeaponInfo.sling &&
                   !station.WeaponInfo.troops && !station.Reloading && !station.SalvoInProgress && station.Ammo > 0 &&
                   station.Ready() && !station.SafetyIsOn(aircraft) && StrikeBombStationCompatible(station);
        }
        catch { return false; }
    }
    private static bool StationStructurallySafe(Aircraft aircraft, WeaponStation station)
    {
        try
        {
            return aircraft != null && station != null && aircraft.weaponStations != null &&
                   aircraft.weaponStations.Contains(station) && station.WeaponInfo != null &&
                   station.WeaponInfo.weaponPrefab != null && station.WeaponInfo.missile && !station.WeaponInfo.gun &&
                   !station.WeaponInfo.bomb && !station.WeaponInfo.glideBomb && !station.WeaponInfo.sling &&
                   !station.WeaponInfo.troops && !station.Cargo && !station.Reloading && !station.SalvoInProgress &&
                   station.Ammo > 0 && station.Ready() && !station.SafetyIsOn(aircraft);
        }
        catch { return false; }
    }
    internal static bool RequiredLineOfSight(Aircraft aircraft, Unit target, WeaponStation station)
    {
        try
        {
            return station.WeaponInfo != null && (!station.WeaponInfo.targetRequirements.lineOfSight ||
                                                  target.LineOfSight(aircraft.transform.position, 1000f));
        }
        catch
        {
            return false;
        }
    }
    internal static bool TryMissionLaunchGeometry(Aircraft aircraft, Unit target, WeaponStation station,
                                                  GlobalPosition known, out float distance, out float angle)
    {
        distance = angle = 0f;
        try
        {
            if (aircraft == null || target == null || station == null || station.WeaponInfo == null ||
                aircraft.rb == null || !Finite(known.x) || !Finite(known.y) || !Finite(known.z))
                return false;
            Vector3 delta = known - aircraft.transform.position.ToGlobalPosition();
            float muzzleVelocity = station.WeaponInfo.muzzleVelocity;
            if (muzzleVelocity > 0f && target.rb != null)
                delta += target.rb.velocity * (delta.magnitude / muzzleVelocity);
            distance = delta.magnitude;
            angle = Vector3.Angle(aircraft.transform.forward, delta);
            return Finite(delta) && Finite(distance) && Finite(angle);
        }
        catch { return false; }
    }
    internal static bool HudShoot(Aircraft aircraft, Unit target, WeaponStation station, bool opportunity)
    {
        try
        {
            if (!ValidAircraft(aircraft) || aircraft.NetworkHQ == null || target == null || station == null ||
                station.WeaponInfo == null || !aircraft.NetworkHQ.TryGetKnownPosition(target, out GlobalPosition known))
                return false;
            GlobalPosition own = aircraft.transform.position.ToGlobalPosition();
            Vector3 delta = known - own;
            float distance = FastMath.Distance(own, known), speed = aircraft.rb.velocity.magnitude;
            if (!Finite(delta) || !Finite(distance) || distance <= 0f || !Finite(speed) ||
                !Finite(target.rb.velocity) || !Finite(target.transform.position))
                return false;
            Missile missile = station.WeaponInfo.weaponPrefab.GetComponent<Missile>();
            if (missile == null)
                return false;
            missile.CalcRange(speed, aircraft.transform.position.y, known.y, distance, target.rb.velocity.magnitude,
                              out float dynamicRange);
            var req = station.WeaponInfo.targetRequirements;
            float angle = Vector3.Angle(aircraft.transform.forward, delta);
            if (!Finite(dynamicRange) || !Finite(angle) || distance > dynamicRange || distance < req.minRange ||
                angle > req.minAlignment || speed < req.minOwnerSpeed ||
                !RequiredLineOfSight(aircraft, target, station))
                return false;
            if (!opportunity)
                return true;
            if (aircraft.NetworkHQ == null ||
                !aircraft.NetworkHQ.trackingDatabase.TryGetValue(target.persistentID, out TrackingInfo tracking))
                return false;
            return CombatAI.AnalyzeTarget(station, aircraft, tracking, 0f, distance, true).opportunity > 0f;
        }
        catch
        {
            return false;
        }
    }
    internal static bool LaunchContext(Aircraft defender, Aircraft target, WeaponStation station, bool geometry,
                                       out string reason)
    {
        reason = "";
        try
        {
            if (!ValidHostileTrackedAircraft(defender, target))
            {
                reason = "aircraft_hostility_or_tracking";
                return false;
            }
            if (station == null || station.Cargo || station.Reloading || station.SalvoInProgress || station.Ammo <= 0 ||
                station.WeaponInfo == null || !station.WeaponInfo.missile || station.WeaponInfo.gun ||
                station.WeaponInfo.bomb || station.WeaponInfo.glideBomb || station.WeaponInfo.sling ||
                station.WeaponInfo.troops || !station.Ready() || station.SafetyIsOn(defender))
            {
                reason = "station";
                return false;
            }
            if (!StationHasSeeker(station, false))
            {
                reason = "seeker";
                return false;
            }
            if (!HudShoot(defender, target, station, false))
            {
                reason = "shoot";
                return false;
            }
            return true;
        }
        catch
        {
            reason = "exception";
            return false;
        }
    }
    internal static bool TryOpportunity(Aircraft defender, Aircraft target, WeaponStation station,
                                        out float opportunity)
    {
        opportunity = 0f;
        try
        {
            if (defender.NetworkHQ == null ||
                !defender.NetworkHQ.trackingDatabase.TryGetValue(target.persistentID, out TrackingInfo tracking))
                return false;
            float distance = Vector3.Distance(defender.transform.position, target.transform.position);
            opportunity = CombatAI.AnalyzeTarget(station, defender, tracking, 0f, distance, true).opportunity;
            return Finite(opportunity);
        }
        catch
        {
            return false;
        }
    }
    internal static bool TryMissionOpportunity(Aircraft defender, Unit target, WeaponStation station,
                                               out float opportunity)
    {
        opportunity = 0f;
        try
        {
            if (defender.NetworkHQ == null ||
                !defender.NetworkHQ.trackingDatabase.TryGetValue(target.persistentID, out TrackingInfo tracking))
                return false;
            float distance = Vector3.Distance(defender.transform.position, target.transform.position);
            opportunity = CombatAI.AnalyzeTarget(station, defender, tracking, 0f, distance, true).opportunity;
            return Finite(opportunity);
        }
        catch { return false; }
    }
    internal static bool ValidMissile(Missile missile)
    {
        try
        {
            return missile != null && !missile.disabled && missile.gameObject.activeInHierarchy &&
                   missile.Identity != null && missile.Identity.IsSpawned && missile.Identity.NetId != 0 &&
                   !missile.persistentID.Equals(default(PersistentID)) &&
                   UnitRegistry.TryGetUnit(missile.persistentID, out Unit resolved) && resolved == missile;
        }
        catch
        {
            return false;
        }
    }
}
