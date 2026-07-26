using System;
using UnityEngine;

namespace LoyalWingman;

internal sealed class FixedWingDroneAntiShipAttackState : PilotBaseState
{
    private readonly WingmanAiManager manager; private readonly Aircraft drone; private readonly Ship target;
    private AntiShipAttackStage stage = AntiShipAttackStage.AgmVolley; private WeaponStation? selectedStation;
    private GlobalPosition lastKnownPosition; private float requestedTerrainHeight = float.NaN, retreatDistance, breakOffUntil;
    private bool hasKnownPosition, collisionBreakOff; private string? reportedHold, enterFailure;
    private readonly CountermeasurePulseController countermeasurePulse = new CountermeasurePulseController();
    internal Ship Target => target; internal Unit Unit => target;
    internal FixedWingDroneAntiShipAttackState(WingmanAiManager manager, Aircraft drone, Ship target) { this.manager = manager; this.drone = drone; this.target = target; }
    public override void EnterState(Pilot p)
    {
        if (drone == null || drone.weaponManager == null) enterFailure = "structural"; else SetOnlyTarget(); Log("asuw_enter stage=" + stage + " target_pid=" + target.persistentID);
    }
    public override void UpdateState(Pilot p) { }
    public override void FixedUpdateState(Pilot p)
    {
        try
        {
            if (!manager.MissionOwns(drone, this, WingmanMission.AntiShip, target)) { countermeasurePulse.Stop(drone); return; }
            if (enterFailure != null) { Complete(enterFailure); return; }
            if (!CombatSupport.ValidHostileShip(drone, target)) { manager.ClearAntiShip(drone, "target_invalid"); return; }
            if (drone.autopilot is not AutopilotPlane ap || drone.rb == null || drone.weaponManager == null) { Complete("structural"); return; }
            GlobalPosition known = lastKnownPosition; bool fresh = drone.NetworkHQ != null && drone.NetworkHQ.TryGetKnownPosition(target, out known);
            bool laserSupportOwns = StrikeMissionLogic.LaserSupportOwns(manager.AntiShipLaserPending(drone), manager.ActiveAntiShipLasers(drone));
            if (!fresh && (!(stage == AntiShipAttackStage.LaserVolley || stage == AntiShipAttackStage.SupportOrComplete || stage == AntiShipAttackStage.AshmVolley) || !hasKnownPosition)) { Complete("known_position"); return; }
            Vector3 delta = known - drone.transform.position.ToGlobalPosition(); float distance = delta.magnitude; Vector3 velocity = target.rb != null ? target.rb.velocity : Vector3.zero;
            if (!CombatSupport.Finite(delta) || !CombatSupport.Finite(distance) || distance <= 0f || !CombatSupport.Finite(velocity)) { Complete("non_finite"); return; }
            if (fresh) { lastKnownPosition = known; hasKnownPosition = true; }
            bool radarThreat = manager.TryGetAntiShipRadarThreat(drone, out Missile threat, out float threatTti);
            countermeasurePulse.Update(drone, threat, radarThreat && MissileDefenseLogic.ShouldPulseAntiShipRadarCountermeasure(threatTti));
            UpdateTerrainProfile(distance, radarThreat);
            AdvanceEmptyStages();
            if (!fresh && stage == AntiShipAttackStage.AshmVolley) { if (laserSupportOwns) { SetOnlyTarget(); LaserSupport(ap, known); Hold("laser_support_stale"); } else Complete("known_position"); return; }
            if (!fresh && laserSupportOwns && stage == AntiShipAttackStage.SupportOrComplete) { SetOnlyTarget(); LaserSupport(ap, known); Hold("laser_support_stale"); return; }
            if (Time.timeSinceLevelLoad < breakOffUntil) { BreakOff(ap); return; }
            collisionBreakOff = false;
            if (retreatDistance > 0f && !StrikeMissionLogic.ShouldExitRetreat(drone.transform.position.y - target.transform.position.y, drone.definition.aircraftParameters.turningRadius, distance, retreatDistance)) { Retreat(ap, known); return; }
            retreatDistance = 0f;
            if (stage == AntiShipAttackStage.SupportOrComplete) { if (manager.AntiShipLaserPending(drone)) { SetOnlyTarget(); Ingress(ap, known, velocity, distance); Hold("laser_pending"); } else if (manager.ActiveAntiShipLasers(drone) > 0) { SetOnlyTarget(); LaserSupport(ap, known); } else Complete("all_volleys_issued"); return; }
            AntiShipWeaponKind kind = KindFor(stage); WeaponStation? station = Select(kind);
            if (station == null) { Ingress(ap, known, velocity, distance); Hold("weapon_waiting"); return; }
            var req = station.WeaponInfo.targetRequirements;
            if (Physics.Linecast(drone.transform.position, drone.transform.position + drone.rb.velocity * 5f, 64)) { collisionBreakOff = true; breakOffUntil = Time.timeSinceLevelLoad + StrikeMissionLogic.CollisionBreakoffSeconds; BreakOff(ap); return; }
            if (!CombatSupport.TryMissionLaunchGeometry(drone, target, station, known, out float launchDistance, out float angle)) { Ingress(ap, known, velocity, distance); Hold("launch_geometry"); return; }
            if (StrikeMissionLogic.ShouldRetreat(launchDistance, req.minRange)) { retreatDistance = StrikeMissionLogic.RetreatDistance(drone.definition.aircraftParameters.turningRadius, req.minRange); Retreat(ap, known); return; }
            Ingress(ap, known, velocity, distance);
            if (station.Reloading || station.SalvoInProgress || !station.Ready() || station.SafetyIsOn(drone) || !CombatSupport.RequiredLineOfSight(drone, target, station) || !CombatSupport.AntiShipStationSafe(drone, station) || !CombatSupport.TryMissionOpportunity(drone, target, station, out float opportunity) || opportunity <= 0f || (kind == AntiShipWeaponKind.LaserRocket && (manager.AntiShipLaserPending(drone) || drone.GetLaserDesignator() == null || !drone.GetLaserDesignator().IsLased(target))) || !StrikeMissionLogic.WithinAntiShipEnvelope(launchDistance, req.minRange, req.maxRange, angle, req.minAlignment, drone.speed, req.minOwnerSpeed)) { Hold("launch_requirements"); return; }
            if (!manager.MissionOwns(drone, this, WingmanMission.AntiShip, target) || !CombatSupport.ValidHostileShip(drone, target) || !CombatSupport.AntiShipStationSafe(drone, station) || !CombatSupport.RequiredLineOfSight(drone, target, station)) { Hold("launch_recheck"); return; }
            drone.weaponManager.currentWeaponStation = station; SetOnlyTarget(); if (kind == AntiShipWeaponKind.LaserRocket) manager.MarkAntiShipLaserPending(drone); p.Fire();
            Log("asuw_fire kind=" + kind + " stage=" + stage + " station=" + drone.weaponStations.IndexOf(station) + " ammo=" + station.Ammo);
            if (StrikeMissionLogic.ShouldBreakOffAfterShot(launchDistance, req.minRange)) breakOffUntil = Time.timeSinceLevelLoad + StrikeMissionLogic.NearShotBreakoffSeconds;
        }
        catch (Exception e) { countermeasurePulse.Stop(drone); Complete("exception_" + e.GetType().Name); }
    }
    private void AdvanceEmptyStages()
    {
        while (stage != AntiShipAttackStage.SupportOrComplete)
        {
            AntiShipAttackStage current = stage;
            AntiShipAttackStage next = StrikeMissionLogic.AdvanceEmptyAntiShipStage(current, HasAmmo(KindFor(current)), AmmoFor(AntiShipWeaponKind.LaserRocket), manager.AntiShipLaserPending(drone), manager.ActiveAntiShipLasers(drone));
            if (next == current) return;
            SetStage(next);
            if (current == AntiShipAttackStage.LaserVolley) return;
        }
    }
    private bool HasAmmo(AntiShipWeaponKind kind) { foreach (WeaponStation s in drone.weaponStations) if (Structural(s, kind) && s.Ammo > 0) return true; return false; }
    private int AmmoFor(AntiShipWeaponKind kind) { int ammo = 0; foreach (WeaponStation s in drone.weaponStations) if (Structural(s, kind)) ammo += Mathf.Max(0, s.Ammo); return ammo; }
    private WeaponStation? Select(AntiShipWeaponKind kind)
    {
        if (selectedStation != null && drone.weaponStations.Contains(selectedStation) && Structural(selectedStation, kind) && selectedStation.Ammo > 0) return selectedStation;
        selectedStation = null; WeaponStation? fallback = null, best = null; float score = float.NegativeInfinity; int bestIndex = int.MaxValue;
        for (int i = 0; i < drone.weaponStations.Count; i++) { WeaponStation s = drone.weaponStations[i]; if (!Structural(s, kind) || s.Ammo <= 0) continue; fallback ??= s; if (CombatSupport.TryMissionOpportunity(drone, target, s, out float value) && value > 0f && (best == null || CombatCommandLogic.IsBetterOpportunity(value, i, score, bestIndex))) { best = s; score = value; bestIndex = i; } }
        return selectedStation = best ?? fallback;
    }
    private bool Structural(WeaponStation s, AntiShipWeaponKind kind) => s != null && CombatSupport.AntiShipStationCompatible(drone, s) && CombatSupport.GetAntiShipWeaponKind(s) == kind;
    private static AntiShipWeaponKind KindFor(AntiShipAttackStage s) => s == AntiShipAttackStage.AgmVolley ? AntiShipWeaponKind.Agm : s == AntiShipAttackStage.AshmVolley ? AntiShipWeaponKind.Ashm : AntiShipWeaponKind.LaserRocket;
    private void Ingress(AutopilotPlane ap, GlobalPosition known, Vector3 velocity, float distance) { float lead = Mathf.Clamp(distance / Mathf.Max(1f, drone.rb.velocity.magnitude), 0f, 20f); drone.GetInputs().throttle = 1f; drone.GetInputs().brake = 0f; ap.AutoAim(known + velocity * lead, true, false, false, .25f, 180f, true, requestedTerrainHeight, velocity); }
    private void Retreat(AutopilotPlane ap, GlobalPosition known) { Vector3 away = drone.transform.position.ToGlobalPosition() - known; away.y = 0f; if (!CombatSupport.Finite(away) || away.sqrMagnitude < .01f) throw new InvalidOperationException("retreat_vector"); drone.GetInputs().throttle = 1f; drone.GetInputs().brake = 0f; ap.AutoAim((drone.transform.position + away.normalized * retreatDistance).ToGlobalPosition(), true, true, false, .5f, 180f, true, requestedTerrainHeight, drone.rb.velocity); }
    private void BreakOff(AutopilotPlane ap) { Vector3 forward = drone.transform.forward; forward.y = 0f; if (!CombatSupport.Finite(forward) || forward.sqrMagnitude < .01f) throw new InvalidOperationException("breakoff_vector"); drone.GetInputs().throttle = 1f; drone.GetInputs().brake = 0f; ap.AutoAim((drone.transform.position + forward.normalized * 1000f).ToGlobalPosition(), true, collisionBreakOff, false, .5f, 180f, true, requestedTerrainHeight, drone.rb.velocity); }
    private void LaserSupport(AutopilotPlane ap, GlobalPosition known) { SetOnlyTarget(); Vector3 away = drone.transform.position.ToGlobalPosition() - known; away.y = 0f; if (!CombatSupport.Finite(away) || away.sqrMagnitude < .01f) { Ingress(ap, known, Vector3.zero, away.magnitude); return; } ap.AutoAim((drone.transform.position + away.normalized * 1000f).ToGlobalPosition(), true, false, false, .5f, 180f, true, requestedTerrainHeight, drone.rb.velocity); }
    private void SetOnlyTarget() { if (drone == null || drone.weaponManager == null) throw new InvalidOperationException("structural"); drone.weaponManager.ClearTargetList(); drone.weaponManager.AddTargetList(target); }
    private void UpdateTerrainProfile(float distance, bool radarThreat)
    {
        float height = radarThreat ? 5f : StrikeMissionLogic.AntiShipProfileHeight(distance);
        if (height == requestedTerrainHeight) return;
        requestedTerrainHeight = height;
        Log("asuw_profile requested_height=" + height + " radar_threat=" + radarThreat + " radar_alt=" + drone.radarAlt + " target_range=" + distance + " minimum_radar_alt=" + drone.definition.aircraftParameters.minimumRadarAlt + " max_radius=" + drone.maxRadius);
    }
    private void SetStage(AntiShipAttackStage next) { if (stage != next) { stage = next; selectedStation = null; Log("asuw_stage_changed stage=" + stage); } }
    private void Hold(string reason) { if (reportedHold != reason) { reportedHold = reason; Log("asuw_hold reason=" + reason); } }
    private void Complete(string reason) { Log("asuw_completion reason=" + reason); manager.CompleteMission(drone, WingmanMission.AntiShip, "asuw_" + reason); }
    public override void LeaveState() { countermeasurePulse.Stop(drone); try { if (drone != null && drone.weaponManager != null) drone.weaponManager.ClearTargetList(); } catch { } Log("asuw_leave target_pid=" + target.persistentID); }
    private void Log(string text) => manager.CombatLog(drone, text);
}
