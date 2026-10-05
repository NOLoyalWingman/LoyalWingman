using System;
using UnityEngine;

namespace LoyalWingman;

internal sealed class FixedWingDroneCasState : PilotBaseState
{
    private readonly WingmanAiManager manager;
    private readonly Aircraft drone;
    private Unit? target;
    private GlobalPosition lastKnownPosition;
    private CasAttackStage stage = CasAttackStage.AgmVolley;
    private float nextScan, gunLastFired, gunLastTargetAngle, gunClimbFactor, gunTargetHeight, gunBreakOffUntil;
    private Vector3 gunTargetVelocityPrevious;
    private bool hasLastKnownPosition, cruisingToCenter, gunClimbing;

    internal int CenterRevision { get; }
    internal FixedWingDroneCasState(WingmanAiManager manager, Aircraft drone, int centerRevision)
    {
        this.manager = manager;
        this.drone = drone;
        CenterRevision = centerRevision;
        cruisingToCenter = true;
    }

    public override void EnterState(Pilot pilot)
    {
        drone.flightAssist = true;
        drone.GetInputs().brake = 0f;
        gunTargetHeight = drone.radarAlt;
        gunLastFired = -100f;
        Log("cas_enter center_revision=" + CenterRevision);
    }

    public override void UpdateState(Pilot pilot) { }

    public override void FixedUpdateState(Pilot pilot)
    {
        try
        {
            if (!manager.TryGetCasCenter(drone, out GlobalPosition center, out int revision, out float loiterPhase) ||
                revision != CenterRevision || !manager.CasOwns(drone, this, CenterRevision)) return;
            if (drone.autopilot is not AutopilotPlane autopilot || drone.rb == null || drone.weaponManager == null) { Complete("structural"); return; }

            Unit? previousTarget = target;
            bool hasRecordedTarget = manager.TryGetCasTarget(drone, out Unit recordedTarget);
            if (hasRecordedTarget) target = recordedTarget;
            if (target != null && !CombatSupport.ValidHostileCasTarget(drone, target))
            {
                manager.InvalidateCasTarget(drone, target, "target_invalid");
                target = null;
                TryAcquireTarget(true);
            }
            else if (!hasRecordedTarget)
            {
                target = null;
                TryAcquireTarget(false);
            }
            if (!ReferenceEquals(target, previousTarget))
            {
                ResetGunTargetHistory();
                if (target != null) Log("cas_target_acquired target_pid=" + target.persistentID);
            }

            if (target == null)
            {
                drone.weaponManager.ClearTargetList();
                Loiter(autopilot, center, loiterPhase);
                return;
            }

            SetOnlyTarget();
            GlobalPosition known = lastKnownPosition;
            bool fresh = drone.NetworkHQ != null && drone.NetworkHQ.TryGetKnownPosition(target, out known) && Finite(known);
            if (fresh) { lastKnownPosition = known; hasLastKnownPosition = true; }
            bool laserPending = manager.CasLaserPending(drone);
            int activeLasers = manager.ActiveCasLasers(drone);
            AdvanceStages(laserPending);
            if (!fresh)
            {
                if (hasLastKnownPosition && (laserPending || activeLasers > 0)) SupportLaser(autopilot, lastKnownPosition);
                else Hold(autopilot, center, loiterPhase);
                return;
            }

            if (stage == CasAttackStage.SupportOrComplete)
            {
                if (laserPending || activeLasers > 0) SupportLaser(autopilot, known);
                else Complete("all_compatible_ammo_exhausted");
                return;
            }
            if (stage == CasAttackStage.FixedGunVolley)
            {
                UseFixedGuns(pilot, autopilot, known);
                return;
            }
            UseMissileOrRocket(pilot, autopilot, known, laserPending);
        }
        catch (Exception e) { Complete("exception_" + e.GetType().Name); }
    }

    private void TryAcquireTarget(bool immediate)
    {
        if (!immediate && Time.timeSinceLevelLoad < nextScan) return;
        nextScan = Time.timeSinceLevelLoad + 1f;
        if (manager.TryAcquireCasTarget(drone, out Unit acquired))
        {
            target = acquired;
        }
    }

    private void AdvanceStages(bool laserPending)
    {
        while (stage != CasAttackStage.SupportOrComplete)
        {
            CasWeaponKind kind = KindFor(stage);
            bool hasAmmo = false, reloading = false, salvoInProgress = false;
            foreach (WeaponStation station in drone.weaponStations)
            {
                if (!CombatSupport.CasStationCompatible(drone, station) || CombatSupport.GetCasWeaponKind(station) != kind)
                    continue;
                hasAmmo |= station.Ammo > 0;
                reloading |= station.Reloading;
                salvoInProgress |= station.SalvoInProgress;
            }
            CasAttackStage next = CasMissionLogic.Advance(stage, hasAmmo, reloading, salvoInProgress, laserPending);
            if (next == stage) return;
            stage = next;
            Log("cas_stage_changed stage=" + stage);
        }
    }

    private void UseMissileOrRocket(Pilot pilot, AutopilotPlane autopilot, GlobalPosition known, bool laserPending)
    {
        CasWeaponKind kind = KindFor(stage);
        WeaponStation? station = Select(kind);
        Ingress(autopilot, known);
        if (station == null || station.Reloading || station.SalvoInProgress || !station.Ready() || station.SafetyIsOn(drone) ||
            !CombatSupport.CasStationSafe(drone, station) || !CombatSupport.RequiredLineOfSight(drone, target!, station) ||
            !CombatSupport.TryMissionOpportunity(drone, target!, station, out float opportunity) || opportunity <= 0f ||
            (kind == CasWeaponKind.LaserRocket && (laserPending || drone.GetLaserDesignator() == null || !drone.GetLaserDesignator().IsLased(target!))) ||
            !CombatSupport.TryMissionLaunchGeometry(drone, target!, station, known, out float distance, out float angle) ||
            !WithinEnvelope(distance, station.WeaponInfo.targetRequirements, angle)) return;

        if (!manager.CasOwns(drone, this, CenterRevision) || target == null || !CombatSupport.ValidHostileCasTarget(drone, target) ||
            drone.NetworkHQ == null || !drone.NetworkHQ.TryGetKnownPosition(target, out GlobalPosition current) || !Finite(current) ||
            !CombatSupport.CasStationCompatible(drone, station) || CombatSupport.GetCasWeaponKind(station) != kind ||
            !CombatSupport.CasStationSafe(drone, station) || !CombatSupport.RequiredLineOfSight(drone, target, station) ||
            !CombatSupport.TryMissionOpportunity(drone, target, station, out opportunity) || opportunity <= 0f ||
            !CombatSupport.TryMissionLaunchGeometry(drone, target, station, current, out distance, out angle) ||
            !WithinEnvelope(distance, station.WeaponInfo.targetRequirements, angle) ||
            (kind == CasWeaponKind.LaserRocket && (manager.CasLaserPending(drone) || drone.GetLaserDesignator() == null || !drone.GetLaserDesignator().IsLased(target)))) return;

        drone.weaponManager.currentWeaponStation = station;
        SetOnlyTarget();
        if (kind == CasWeaponKind.LaserRocket) manager.MarkCasLaserPending(drone);
        pilot.Fire();
        Log("cas_fire kind=" + kind + " stage=" + stage + " station=" + drone.weaponStations.IndexOf(station) + " ammo=" + station.Ammo);
    }

    private void UseFixedGuns(Pilot pilot, AutopilotPlane autopilot, GlobalPosition known)
    {
        if (Time.timeSinceLevelLoad < gunBreakOffUntil) { BreakOff(autopilot); return; }
        WeaponStation? station = Select(CasWeaponKind.FixedGun);
        if (station == null || station.WeaponInfo == null || !station.WeaponInfo.gun || !station.WeaponInfo.boresight || station.Ammo <= 0) { Ingress(autopilot, known); return; }
        if (drone.NetworkHQ == null || !drone.NetworkHQ.IsTargetPositionAccurate(target!, 100f)) { Ingress(autopilot, known); return; }
        Vector3 targetVector = known - drone.transform.position.ToGlobalPosition();
        float targetDistance = targetVector.magnitude;
        float targetAngle = Vector3.Angle(drone.transform.forward, targetVector);
        if (!CombatSupport.Finite(targetVector) || !CombatSupport.Finite(targetDistance) || targetDistance <= 0f || targetDistance > station.WeaponInfo.targetRequirements.maxRange || targetAngle > Mathf.Min(50f, targetDistance * .1f)) { Ingress(autopilot, known); return; }

        float leadTime = TargetCalc.TargetLeadTime(target!, drone.gameObject, drone.rb, station.WeaponInfo.muzzleVelocity, station.WeaponInfo.dragCoef, 2);
        Vector3 velocity = target!.rb == null ? Vector3.zero : target.rb.velocity;
        Vector3 relativeVelocity = velocity - drone.rb.velocity;
        float closingTime = targetDistance / Mathf.Max(Vector3.Dot(-targetVector.normalized, relativeVelocity), .1f);
        bool obscured = !target!.LineOfSight(drone.transform.position - Vector3.up * drone.definition.spawnOffset.y, 1000f);
        GlobalPosition aim = target.GlobalPosition();
        if (obscured) gunClimbing = true;
        else
        {
            Vector3 acceleration = (velocity - gunTargetVelocityPrevious) / Time.fixedDeltaTime;
            gunTargetVelocityPrevious = velocity;
            if (target.radarAlt < 1f)
            {
                float heightRatio = (drone.transform.position.y - target.transform.position.y) / Mathf.Max(targetDistance, 10f);
                if (heightRatio < .1f) gunClimbing = true;
                if (heightRatio > .2f) gunClimbing = false;
            }
            else gunClimbing = false;
            float maxRange = target.speed < 30f ? station.WeaponInfo.targetRequirements.maxRange : station.WeaponInfo.targetRequirements.maxRange * .7f;
            aim = target.GlobalPosition() + velocity * leadTime + .5f * leadTime * leadTime * (acceleration + Vector3.up * 9.81f);
            Vector3 mountForward = Vector3.zero;
            foreach (Weapon weapon in station.Weapons) mountForward += weapon.transform.forward;
            float mountAngle = Vector3.Angle(mountForward, aim - drone.transform.position.ToGlobalPosition());
            float damping = Mathf.Clamp((mountAngle - gunLastTargetAngle) / Time.fixedDeltaTime, -20f, 0f);
            gunLastTargetAngle = mountAngle;
            float aperture = Mathf.Clamp(50f * target.maxRadius / targetDistance, .5f, 3f);
            float extraLead = Mathf.Min(mountAngle * .05f, 1f);
            aim += velocity * extraLead + .5f * extraLead * (acceleration + Vector3.up * 9.81f);
            if (targetDistance < maxRange && mountAngle + Mathf.Min(damping * .25f, 0f) < aperture)
            {
                if (CanRequestFixedGun(station))
                {
                    drone.weaponManager.currentWeaponStation = station;
                    SetOnlyTarget();
                    pilot.Fire();
                    gunLastFired = Time.timeSinceLevelLoad;
                }
            }
            gunTargetHeight = drone.radarAlt;
        }
        gunClimbFactor = Mathf.Max(gunClimbFactor + (gunClimbing ? 100f : -500f) * Time.deltaTime, 0f);
        aim += Vector3.up * gunClimbFactor;
        bool ignoreCollision = targetAngle > 5f || closingTime < 3f;
        if (closingTime > 0f && targetAngle < 15f)
        {
            int limit = Vector3.Dot(target.transform.forward, drone.transform.forward) > 0f ? 2 : 1;
            if (target.speed < 30f) limit = 2;
            if (closingTime < limit) { gunBreakOffUntil = Time.timeSinceLevelLoad + (target.speed < 30f ? 10f : 2f); BreakOff(autopilot); return; }
        }
        ControlInputs inputs = drone.GetInputs();
        inputs.brake = 0f;
        inputs.throttle = drone.speed > drone.definition.aircraftParameters.cornerSpeed && drone.speed > target.speed * 1.5f ? 0f : 1f;
        if (target.speed > 30f && targetDistance < 500f && targetAngle < 45f && drone.speed - target.speed > 60f) inputs.throttle = 0f;
        if (Time.timeSinceLevelLoad - gunLastFired < .5f && CanRequestFixedGun(station)) pilot.Fire();
        autopilot.AutoAim(aim, false, target.speed > 30f || ignoreCollision, false, 1f, 180f, obscured, Mathf.Clamp(gunTargetHeight, drone.maxRadius, 8000f), velocity);
    }

    private void ResetGunTargetHistory()
    {
        // Firing continuation is target-specific; do not carry it or its lead/damping history across targets.
        gunLastFired = -100f;
        gunLastTargetAngle = 0f;
        gunTargetVelocityPrevious = Vector3.zero;
        gunClimbFactor = 0f;
        gunClimbing = false;
        gunBreakOffUntil = 0f;
        gunTargetHeight = drone.radarAlt;
        hasLastKnownPosition = false;
        lastKnownPosition = default;
    }

    private void Loiter(AutopilotPlane autopilot, GlobalPosition center, float phase)
    {
        GlobalPosition position = drone.transform.position.ToGlobalPosition();
        float horizontal = new Vector3(center.x - position.x, 0f, center.z - position.z).magnitude;
        if (horizontal <= 2200f) cruisingToCenter = false;
        AircraftDefinition? definition = drone.definition as AircraftDefinition;
        float corner = definition?.aircraftParameters.cornerSpeed ?? float.NaN;
        float orbitSpeed = LoiterCommandLogic.OrbitTargetSpeed(corner);
        float theta = phase * Mathf.Deg2Rad + orbitSpeed / 2200f * Time.fixedTime;
        float aimTheta = theta + 25f * Mathf.Deg2Rad;
        Vector3 radial = new Vector3(Mathf.Cos(aimTheta), 0f, Mathf.Sin(aimTheta));
        Vector3 tangent = new Vector3(-Mathf.Sin(aimTheta), 0f, Mathf.Cos(aimTheta));
        GlobalPosition destination = cruisingToCenter ? center : center + radial * 2200f;
        Vector3 direction = cruisingToCenter ? new Vector3(center.x - position.x, 0f, center.z - position.z).normalized : tangent;
        float speed = Vector3.ProjectOnPlane(drone.rb.velocity, Vector3.up).magnitude;
        float cruise = definition?.aircraftParameters.cruiseThrottle ?? .65f;
        float throttle = cruisingToCenter ? Mathf.Clamp(cruise + (165f - speed) / 200f + Vector3.Dot(destination - position, direction) / 3000f, .45f, 1f) : LoiterCommandLogic.OrbitThrottle(speed, corner, cruise);
        drone.GetInputs().throttle = throttle;
        drone.GetInputs().brake = 0f;
        autopilot.AutoAim(destination, true, false, false, .45f, 60f, true, drone.radarAlt, cruisingToCenter ? Vector3.zero : tangent * orbitSpeed);
    }

    private void Ingress(AutopilotPlane autopilot, GlobalPosition known) { drone.GetInputs().throttle = 1f; drone.GetInputs().brake = 0f; autopilot.AutoAim(known, true, false, false, .5f, 180f, true, drone.radarAlt, target != null && target.rb != null ? target.rb.velocity : Vector3.zero); }
    private void SupportLaser(AutopilotPlane autopilot, GlobalPosition known) { Vector3 away = drone.transform.position.ToGlobalPosition() - known; away.y = 0f; if (away.sqrMagnitude < .01f) { Ingress(autopilot, known); return; } autopilot.AutoAim(drone.transform.position.ToGlobalPosition() + away.normalized * 1000f, true, false, false, .5f, 180f, true, drone.radarAlt, drone.rb.velocity); }
    private void BreakOff(AutopilotPlane autopilot) { autopilot.AutoAim(drone.transform.position.ToGlobalPosition() + (drone.transform.forward + Vector3.up * .2f) * 1000f, true, false, false, .5f, 180f, true, drone.radarAlt, drone.rb.velocity); }
    private void Hold(AutopilotPlane autopilot, GlobalPosition center, float phase) => Loiter(autopilot, center, phase);
    private WeaponStation? Select(CasWeaponKind kind) { WeaponStation? best = null; float score = float.NegativeInfinity; int bestIndex = int.MaxValue; for (int i = 0; i < drone.weaponStations.Count; i++) { WeaponStation station = drone.weaponStations[i]; if (!CombatSupport.CasStationCompatible(drone, station) || CombatSupport.GetCasWeaponKind(station) != kind || station.Ammo <= 0) continue; if (!CombatSupport.TryMissionOpportunity(drone, target!, station, out float value) || value <= 0f) { best ??= station; continue; } if (best == null || CombatCommandLogic.IsBetterOpportunity(value, i, score, bestIndex)) { best = station; score = value; bestIndex = i; } } return best; }
    private bool CanRequestFixedGun(WeaponStation station) => CasMissionLogic.FixedGunRequestAllowed(station.Reloading,
        station.SalvoInProgress, station.Ready(), station.SafetyIsOn(drone), CombatSupport.CasStationSafe(drone, station));
    private static CasWeaponKind KindFor(CasAttackStage current) => current == CasAttackStage.AgmVolley ? CasWeaponKind.Agm : current == CasAttackStage.LaserVolley ? CasWeaponKind.LaserRocket : CasWeaponKind.FixedGun;
    private static bool WithinEnvelope(float distance, TargetRequirements requirements, float angle) => CombatSupport.Finite(distance) && distance >= requirements.minRange && distance < requirements.maxRange && angle < requirements.minAlignment;
    private static bool Finite(GlobalPosition position) => CombatSupport.Finite(position.x) && CombatSupport.Finite(position.y) && CombatSupport.Finite(position.z);
    private void SetOnlyTarget() { drone.weaponManager.ClearTargetList(); drone.weaponManager.AddTargetList(target!); }
    private void Complete(string reason) { Log("cas_completion reason=" + reason); manager.CompleteMission(drone, WingmanMission.Cas, reason); }
    public override void LeaveState() { try { if (drone.weaponManager != null) drone.weaponManager.ClearTargetList(); drone.GetInputs().brake = 0f; } catch { } Log("cas_leave"); }
    private void Log(string text) => manager.CombatLog(drone, text);
}
