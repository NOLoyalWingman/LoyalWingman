using System;
using UnityEngine;

namespace LoyalWingman;

internal sealed class FixedWingDroneSeadState : PilotBaseState
{
    private readonly WingmanAiManager manager; private readonly Aircraft drone; private readonly Unit target;
    private SeadAttackStage stage; private GlobalPosition lastKnownPosition;
    private float targetHeight, retreatDistance, breakOffUntil, nextTelemetry;
    private bool hasKnownPosition; private string? reportedHold, enterFailure;
    internal Unit Target => target;
    internal FixedWingDroneSeadState(WingmanAiManager manager, Aircraft drone, Unit target) { this.manager = manager; this.drone = drone; this.target = target; }
    public override void EnterState(Pilot pilot)
    {
        try { SetOnlyTarget(); targetHeight = drone.radarAlt; Log("sead_enter target_pid=" + target.persistentID); }
        catch (Exception e) { enterFailure = "exception_" + e.GetType().Name; }
    }
    public override void UpdateState(Pilot pilot) { }
    public override void FixedUpdateState(Pilot pilot)
    {
        try
        {
            if (!manager.MissionOwns(drone, this, WingmanMission.Sead, target)) return;
            if (enterFailure != null) { Complete("enter_" + enterFailure); return; }
            if (!CombatSupport.ValidHostileSam(drone, target)) { manager.InvalidateSeadTarget(drone, target, "target_invalid"); return; }
            if (drone.autopilot is not AutopilotPlane autopilot || drone.rb == null || drone.weaponManager == null) { Complete("structural"); return; }
            GlobalPosition known = lastKnownPosition;
            bool fresh = drone.NetworkHQ != null && drone.NetworkHQ.TryGetKnownPosition(target, out known) && Finite(known);
            if (fresh) { lastKnownPosition = known; hasKnownPosition = true; }
            bool laserPending = manager.SeadLaserPending(drone); int activeLasers = manager.ActiveSeadLasers(drone);
            AdvanceStages(laserPending);
            if (!fresh)
            {
                if (hasKnownPosition && SeadMissionLogic.LaserSupportOwns(laserPending, activeLasers)) { SetOnlyTarget(); LaserSupport(autopilot, known); Hold("laser_support_stale"); }
                else Complete("known_position");
                return;
            }
            Vector3 delta = known - drone.transform.position.ToGlobalPosition(); float distance = delta.magnitude;
            Vector3 velocity = target.rb != null ? target.rb.velocity : Vector3.zero;
            if (!CombatSupport.Finite(delta) || !CombatSupport.Finite(distance) || distance <= 0f || !CombatSupport.Finite(velocity)) { Complete("non_finite"); return; }
            if (Time.timeSinceLevelLoad < breakOffUntil) { BreakOff(autopilot); return; }
            if (retreatDistance > 0f && !StrikeMissionLogic.ShouldExitRetreat(drone.transform.position.y - target.transform.position.y, drone.definition.aircraftParameters.turningRadius, distance, retreatDistance)) { Retreat(autopilot, known); return; }
            retreatDistance = 0f;
            if (stage == SeadAttackStage.SupportOrComplete)
            {
                if (laserPending) { SetOnlyTarget(); Ingress(autopilot, known, velocity, distance); Hold("laser_pending"); }
                else if (activeLasers > 0) { SetOnlyTarget(); LaserSupport(autopilot, known); }
                else Complete("all_volleys_issued");
                return;
            }
            SeadWeaponKind kind = KindFor(stage); WeaponStation? station = Select(kind);
            if (station == null) { Ingress(autopilot, known, velocity, distance); Hold("weapon_waiting"); return; }
            if (Physics.Linecast(drone.transform.position, drone.transform.position + drone.rb.velocity * 5f, 64)) { breakOffUntil = Time.timeSinceLevelLoad + StrikeMissionLogic.CollisionBreakoffSeconds; BreakOff(autopilot); return; }
            if (!CombatSupport.TryMissionLaunchGeometry(drone, target, station, known, out float launchDistance, out float angle)) { Ingress(autopilot, known, velocity, distance); Hold("launch_geometry"); return; }
            var req = station.WeaponInfo.targetRequirements;
            if (StrikeMissionLogic.ShouldRetreat(launchDistance, req.minRange)) { retreatDistance = StrikeMissionLogic.RetreatDistance(drone.definition.aircraftParameters.turningRadius, req.minRange); Retreat(autopilot, known); return; }
            Ingress(autopilot, known, velocity, distance);
            if (!CombatSupport.SeadStationSafe(drone, station) || !CombatSupport.RequiredLineOfSight(drone, target, station) || !CombatSupport.TryMissionOpportunity(drone, target, station, out float opportunity) || opportunity <= 0f || (kind == SeadWeaponKind.LaserRocket && (laserPending || drone.GetLaserDesignator() == null || !drone.GetLaserDesignator().IsLased(target))) || !WithinEnvelope(launchDistance, req.minRange, req.maxRange, angle, req.minAlignment, drone.speed, req.minOwnerSpeed)) { Hold("launch_requirements"); return; }
            if (!manager.MissionOwns(drone, this, WingmanMission.Sead, target) || !CombatSupport.ValidHostileSam(drone, target) || drone.NetworkHQ == null || !drone.NetworkHQ.TryGetKnownPosition(target, out GlobalPosition current) || !Finite(current) || !CombatSupport.SeadStationCompatible(drone, station) || CombatSupport.GetSeadWeaponKind(station) != kind || !CombatSupport.SeadStationSafe(drone, station) || !CombatSupport.RequiredLineOfSight(drone, target, station) || !CombatSupport.TryMissionOpportunity(drone, target, station, out opportunity) || opportunity <= 0f || !CombatSupport.TryMissionLaunchGeometry(drone, target, station, current, out launchDistance, out angle) || !WithinEnvelope(launchDistance, req.minRange, req.maxRange, angle, req.minAlignment, drone.speed, req.minOwnerSpeed) || kind == SeadWeaponKind.LaserRocket && (manager.SeadLaserPending(drone) || drone.GetLaserDesignator() == null || !drone.GetLaserDesignator().IsLased(target))) { Hold("launch_recheck"); return; }
            drone.weaponManager.currentWeaponStation = station; SetOnlyTarget(); if (kind == SeadWeaponKind.LaserRocket) manager.MarkSeadLaserPending(drone); pilot.Fire();
            Log("sead_fire kind=" + kind + " stage=" + stage + " station=" + drone.weaponStations.IndexOf(station) + " ammo=" + station.Ammo);
            if (StrikeMissionLogic.ShouldBreakOffAfterShot(launchDistance, req.minRange)) breakOffUntil = Time.timeSinceLevelLoad + StrikeMissionLogic.NearShotBreakoffSeconds;
        }
        catch (Exception e) { Complete("exception_" + e.GetType().Name); }
    }
    private void AdvanceStages(bool laserPending)
    {
        while (stage != SeadAttackStage.SupportOrComplete)
        {
            SeadAttackStage current = stage;
            SeadAttackStage next = SeadMissionLogic.Advance(current, target.radar is Radar radar && radar.activated, HasAmmo(KindFor(current)), LaserAmmo(), laserPending);
            if (next == current) return;
            stage = next; Log("sead_stage target_pid=" + target.persistentID + " stage=" + stage);
        }
    }
    private bool HasAmmo(SeadWeaponKind kind) { foreach (WeaponStation station in drone.weaponStations) if (CombatSupport.SeadStationCompatible(drone, station) && CombatSupport.GetSeadWeaponKind(station) == kind && station.Ammo > 0) return true; return false; }
    private int LaserAmmo() { int ammo = 0; foreach (WeaponStation station in drone.weaponStations) if (CombatSupport.SeadStationCompatible(drone, station) && CombatSupport.GetSeadWeaponKind(station) == SeadWeaponKind.LaserRocket) ammo += Mathf.Max(0, station.Ammo); return ammo; }
    private WeaponStation? Select(SeadWeaponKind kind)
    {
        WeaponStation? fallback = null, best = null; float score = float.NegativeInfinity; int bestIndex = int.MaxValue;
        for (int index = 0; index < drone.weaponStations.Count; index++) { WeaponStation station = drone.weaponStations[index]; if (!CombatSupport.SeadStationCompatible(drone, station) || CombatSupport.GetSeadWeaponKind(station) != kind || station.Ammo <= 0) continue; fallback ??= station; if (CombatSupport.TryMissionOpportunity(drone, target, station, out float opportunity) && opportunity > 0f && (best == null || CombatCommandLogic.IsBetterOpportunity(opportunity, index, score, bestIndex))) { best = station; score = opportunity; bestIndex = index; } }
        return best ?? fallback;
    }
    private static SeadWeaponKind KindFor(SeadAttackStage current) => current == SeadAttackStage.ArmVolley ? SeadWeaponKind.Arm : current == SeadAttackStage.AgmVolley ? SeadWeaponKind.Agm : SeadWeaponKind.LaserRocket;
    private static bool Finite(GlobalPosition position) => CombatSupport.Finite(position.x) && CombatSupport.Finite(position.y) && CombatSupport.Finite(position.z);
    private static bool WithinEnvelope(float distance, float minRange, float maxRange, float angle, float minAlignment, float speed, float minOwnerSpeed) => CombatSupport.Finite(distance) && CombatSupport.Finite(minRange) && CombatSupport.Finite(maxRange) && CombatSupport.Finite(angle) && CombatSupport.Finite(minAlignment) && CombatSupport.Finite(speed) && CombatSupport.Finite(minOwnerSpeed) && distance >= minRange && distance < maxRange && angle < minAlignment && speed >= minOwnerSpeed;
    private void Ingress(AutopilotPlane autopilot, GlobalPosition known, Vector3 velocity, float distance) { float travel = Mathf.Clamp(distance / Mathf.Max(1f, drone.rb.velocity.magnitude), 0f, 20f); UpdateTargetHeight(); drone.GetInputs().throttle = 1f; drone.GetInputs().brake = 0f; autopilot.AutoAim(known + velocity * travel, true, false, false, .5f, 180f, true, Mathf.Clamp(targetHeight, drone.maxRadius, 8000f), velocity); Telemetry(); }
    private void Retreat(AutopilotPlane autopilot, GlobalPosition known) { Vector3 away = drone.transform.position.ToGlobalPosition() - known; away.y = 0f; if (!CombatSupport.Finite(away) || away.sqrMagnitude < .01f) throw new InvalidOperationException("retreat_vector"); UpdateTargetHeight(); drone.GetInputs().throttle = 1f; drone.GetInputs().brake = 0f; autopilot.AutoAim((drone.transform.position.ToGlobalPosition() + away.normalized * retreatDistance), true, true, false, .5f, 180f, true, Mathf.Clamp(targetHeight, drone.maxRadius, 8000f), drone.rb.velocity); Telemetry(); }
    private void BreakOff(AutopilotPlane autopilot) { Vector3 forward = drone.transform.forward; forward.y = 0f; if (!CombatSupport.Finite(forward) || forward.sqrMagnitude < .01f) throw new InvalidOperationException("breakoff_vector"); UpdateTargetHeight(); drone.GetInputs().throttle = 1f; drone.GetInputs().brake = 0f; autopilot.AutoAim((drone.transform.position.ToGlobalPosition() + (forward.normalized + Vector3.up * .2f) * 1000f), true, false, false, .5f, 180f, true, Mathf.Clamp(targetHeight, drone.maxRadius, 8000f), drone.rb.velocity); Telemetry(); }
    private void LaserSupport(AutopilotPlane autopilot, GlobalPosition known) { Vector3 away = drone.transform.position.ToGlobalPosition() - known; away.y = 0f; if (!CombatSupport.Finite(away) || away.sqrMagnitude < .01f) { Ingress(autopilot, known, Vector3.zero, away.magnitude); return; } UpdateTargetHeight(); autopilot.AutoAim((drone.transform.position.ToGlobalPosition() + away.normalized * 1000f), true, false, false, .5f, 180f, true, Mathf.Clamp(targetHeight, drone.maxRadius, 8000f), drone.rb.velocity); Telemetry(); }
    private void UpdateTargetHeight() { if (target.radarAlt > 10f) targetHeight = target.radarAlt; }
    private void SetOnlyTarget() { if (drone == null || drone.weaponManager == null) throw new InvalidOperationException("structural"); drone.weaponManager.ClearTargetList(); drone.weaponManager.AddTargetList(target); }
    private void Hold(string reason) { if (reportedHold != reason) { reportedHold = reason; Log("sead_hold target_pid=" + target.persistentID + " reason=" + reason); } }
    private void Complete(string reason) { Log("sead_complete target_pid=" + target.persistentID + " reason=" + reason); manager.CompleteMission(drone, WingmanMission.Sead, reason); }
    private void Telemetry() { if (Time.fixedTime < nextTelemetry) return; nextTelemetry = Time.fixedTime + 2f; Log("sead_telemetry target_pid=" + target.persistentID + " stage=" + stage); }
    public override void LeaveState() { try { if (drone != null && drone.weaponManager != null) drone.weaponManager.ClearTargetList(); } catch { } Log("sead_leave target_pid=" + target.persistentID); }
    private void Log(string text) => manager.CombatLog(drone, text);
}
