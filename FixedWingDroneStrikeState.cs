using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoyalWingman;

internal sealed class FixedWingDroneStrikeState : PilotBaseState
{
    private readonly WingmanAiManager manager;
    private readonly Aircraft drone;
    private Unit? target;
    private StrikeBombStage stage = StrikeBombStage.BombVolley;
    private float targetHeight, releasedAt = float.NegativeInfinity, breakOffUntil;
    private StrikeBombWeaponKind releasedKind = StrikeBombWeaponKind.None;
    private readonly List<PersistentID> releasedTargetIds = new List<PersistentID>();
    private StrikeBombWeaponKind lastReleasedKind = StrikeBombWeaponKind.None;
    private float lastGlideReleasedAt = float.NegativeInfinity;
    private float glideReattackMinRange;
    private bool postReleaseStarted, hasGlideRelease, glideMissedWindow;
    private string? lastGlideGate;

    internal int Revision { get; }

    internal FixedWingDroneStrikeState(WingmanAiManager manager, Aircraft drone, int revision)
    {
        this.manager = manager;
        this.drone = drone;
        Revision = revision;
    }

    public override void EnterState(Pilot pilot)
    {
        targetHeight = drone.radarAlt;
        ResetGlideState();
        Log("strike_enter revision=" + Revision);
    }

    public override void UpdateState(Pilot pilot) { }

    public override void FixedUpdateState(Pilot pilot)
    {
        try
        {
            if (!manager.StrikeOwns(drone, this, Revision)) return;
            if (pilot == null || drone.autopilot is not AutopilotPlane autopilot || drone.rb == null || drone.weaponManager == null)
            {
                Complete("structural");
                return;
            }
            if (releasedKind != StrikeBombWeaponKind.None || Time.timeSinceLevelLoad < breakOffUntil)
            {
                if (releasedKind != StrikeBombWeaponKind.None) PostRelease(autopilot);
                else BreakOff(autopilot);
                return;
            }
            if (postReleaseStarted && releasedKind == StrikeBombWeaponKind.None && Time.timeSinceLevelLoad >= breakOffUntil && releasedTargetIds.Count > 0)
            {
                while (releasedTargetIds.Count > 0)
                {
                    if (!manager.ConsumeStrikeTarget(drone, releasedTargetIds[0])) return;
                    releasedTargetIds.RemoveAt(0);
                }
                target = null; postReleaseStarted = false; lastReleasedKind = StrikeBombWeaponKind.None;
            }

            RefreshTarget();
            if (target == null)
            {
                ClearGlideMissedWindow();
                if (releasedTargetIds.Count > 0) BeginPostRelease(autopilot, "no_next_target");
                else Complete("target_set_empty");
                return;
            }

            SetOnlyTarget();
            if (!TryKnownPosition(target, out GlobalPosition known))
            {
                if (releasedTargetIds.Count > 0) { BeginPostRelease(autopilot, "target_untracked"); return; }
                Hold(autopilot);
                return;
            }

            AdvanceStages();
            if (stage == StrikeBombStage.Complete)
            {
                ClearGlideMissedWindow();
                if (releasedTargetIds.Count > 0) BeginPostRelease(autopilot, "all_compatible_ammo_exhausted");
                else Complete("all_compatible_ammo_exhausted");
                return;
            }

            if (glideMissedWindow)
            {
                float currentDistance = (known - drone.GlobalPosition()).magnitude;
                if (StrikeBombingLogic.GlideMissedWindowCanResume(currentDistance, glideReattackMinRange))
                {
                    LogGlide("state=strike_glide_missed_window_exit", null, currentDistance, glideReattackMinRange,
                        0f, 0f, 0f, 0f, false, StrikeRippleDecision.ContinueIngress);
                    ClearGlideMissedWindow();
                }
                else
                {
                    LogGlideGate(null, currentDistance, glideReattackMinRange, 0f, 0f, 0f, 0f, false,
                        StrikeRippleDecision.BreakOff, "missed_window_egress");
                    BreakOff(autopilot);
                    return;
                }
            }

            StrikeBombWeaponKind kind = stage == StrikeBombStage.BombVolley ? StrikeBombWeaponKind.Bomb : StrikeBombWeaponKind.GlideBomb;
            WeaponStation? station = Select(kind);
            if (station == null)
            {
                if (releasedTargetIds.Count > 0) { BeginPostRelease(autopilot, "no_ready_station"); return; }
                Approach(autopilot, known, kind, 0f);
                return;
            }

            if (kind == StrikeBombWeaponKind.Bomb)
                UseBomb(pilot, autopilot, station, known);
            else
                UseGlideBomb(pilot, autopilot, station, known);
        }
        catch (Exception e)
        {
            Complete("exception_" + e.GetType().Name);
        }
    }

    private void RefreshTarget()
    {
        Unit? previous = target;
        target = null;
        if (manager.TryAcquireStrikeTarget(drone, releasedTargetIds, out Unit acquired)) target = acquired;
        if (target != null && !ReferenceEquals(target, previous))
        {
            ClearGlideMissedWindow();
            Log((previous == null ? "strike_target_acquired target_pid=" : "strike_target_changed target_pid=") + target.persistentID);
        }
    }

    private void AdvanceStages()
    {
        while (stage != StrikeBombStage.Complete)
        {
            StrikeBombWeaponKind kind = stage == StrikeBombStage.BombVolley ? StrikeBombWeaponKind.Bomb : StrikeBombWeaponKind.GlideBomb;
            bool hasAmmo = false, reloading = false, salvo = false;
            foreach (WeaponStation station in drone.weaponStations)
            {
                if (!CombatSupport.StrikeBombStationCompatible(station) || CombatSupport.GetStrikeBombWeaponKind(station) != kind) continue;
                hasAmmo |= station.Ammo > 0;
                reloading |= station.Reloading;
                salvo |= station.SalvoInProgress;
            }
            StrikeBombStage next = StrikeBombingLogic.Advance(stage, hasAmmo, reloading, salvo);
            if (next == stage) return;
            stage = next;
            if (stage != StrikeBombStage.GlideBombVolley) ClearGlideMissedWindow();
            Log("strike_stage_changed stage=" + stage);
        }
    }

    private void UseBomb(Pilot pilot, AutopilotPlane autopilot, WeaponStation station, GlobalPosition known)
    {
        WeaponInfo? info = station.WeaponInfo;
        if (info == null) { Hold(autopilot); return; }
        GlobalPosition dronePosition = drone.GlobalPosition();
        Vector3 trackedHorizontal = new Vector3(known.x - dronePosition.x, 0f, known.z - dronePosition.z);
        Vector3 forward = drone.transform.forward;
        forward.y = 0f;
        float range = trackedHorizontal.magnitude;
        float angle = Vector3.Angle(forward.normalized, trackedHorizontal.normalized);
        if (!CombatSupport.Finite(trackedHorizontal) || !CombatSupport.Finite(range) || range <= 0f || !CombatSupport.Finite(angle)) { Hold(autopilot); return; }
        if (StrikeBombingLogic.ConventionalCloseOffAxis(range, drone.radarAlt, drone.speed, angle)) { NativeBombBreakOff(autopilot, "bomb_close_off_axis"); return; }

        Vector3 position = drone.transform.position;
        Vector3 ballisticHorizontal = new Vector3(target!.transform.position.x - position.x, 0f, target.transform.position.z - position.z);
        float initialHeight = position.y - (target.transform.position.y + info.airburstHeight);
        Vector3 releaseVelocity = drone.rb.velocity + drone.transform.forward * info.muzzleVelocity;
        float fallTime = Kinematics.FallTime(initialHeight, releaseVelocity.y);
        float rawForwardProjection = Vector3.Dot(releaseVelocity, ballisticHorizontal.normalized);
        float travelSpeed = Mathf.Max(rawForwardProjection, 1f);
        float travelTime = ballisticHorizontal.magnitude / travelSpeed;
        float difference = travelTime - fallTime;
        Approach(autopilot, known, StrikeBombWeaponKind.Bomb, difference);
        if (StrikeBombingLogic.NativeBombFreshBreakOff(travelTime, fallTime)) { NativeBombBreakOff(autopilot, "bomb_native_close"); return; }
        StrikeRippleDecision decision = ResolveRipple(StrikeBombingLogic.ResolveConventionalRipple(StationStillReady(station, StrikeBombWeaponKind.Bomb),
            StrikeBombingLogic.IsBombReleaseWindow(travelTime, fallTime), difference > 1.5f, rawForwardProjection, angle, info.targetRequirements.minAlignment));
        if (decision == StrikeRippleDecision.ContinueIngress) return;
        if (decision == StrikeRippleDecision.BreakOff) { BeginPostRelease(autopilot, "bomb_solution_invalid"); return; }

        if (!manager.StrikeOwns(drone, this, Revision) || target == null || !CombatSupport.ValidHostileTrackedUnit(drone, target) ||
            !TryKnownPosition(target, out GlobalPosition currentKnown) || !StationStillReady(station, StrikeBombWeaponKind.Bomb)) return;
        dronePosition = drone.GlobalPosition();
        trackedHorizontal = new Vector3(currentKnown.x - dronePosition.x, 0f, currentKnown.z - dronePosition.z);
        range = trackedHorizontal.magnitude;
        forward = drone.transform.forward;
        forward.y = 0f;
        angle = Vector3.Angle(forward.normalized, trackedHorizontal.normalized);
        if (!CombatSupport.Finite(trackedHorizontal) || !CombatSupport.Finite(range) || range <= 0f || !CombatSupport.Finite(angle)) return;
        if (StrikeBombingLogic.ConventionalCloseOffAxis(range, drone.radarAlt, drone.speed, angle)) { NativeBombBreakOff(autopilot, "bomb_close_off_axis_recheck"); return; }
        position = drone.transform.position;
        ballisticHorizontal = new Vector3(target.transform.position.x - position.x, 0f, target.transform.position.z - position.z);
        if (!CombatSupport.Finite(ballisticHorizontal) || ballisticHorizontal.sqrMagnitude <= 0f) return;
        releaseVelocity = drone.rb.velocity + drone.transform.forward * station.WeaponInfo.muzzleVelocity;
        fallTime = Kinematics.FallTime(position.y - (target.transform.position.y + station.WeaponInfo.airburstHeight), releaseVelocity.y);
        float forwardProjection = Vector3.Dot(releaseVelocity, ballisticHorizontal.normalized);
        travelTime = ballisticHorizontal.magnitude / Mathf.Max(forwardProjection, 1f);
        if (StrikeBombingLogic.NativeBombFreshBreakOff(travelTime, fallTime)) { NativeBombBreakOff(autopilot, "bomb_native_close_recheck"); return; }
        decision = ResolveRipple(StrikeBombingLogic.ResolveConventionalRipple(StationStillReady(station, StrikeBombWeaponKind.Bomb), StrikeBombingLogic.IsBombReleaseWindow(travelTime, fallTime), travelTime - fallTime > 1.5f, forwardProjection, angle, station.WeaponInfo.targetRequirements.minAlignment));
        if (decision == StrikeRippleDecision.BreakOff) { BeginPostRelease(autopilot, "bomb_recheck_invalid"); return; }
        if (decision != StrikeRippleDecision.FireNext) return;
        drone.weaponManager.currentWeaponStation = station;
        SetOnlyTarget();
        releasedTargetIds.Add(target.persistentID); lastReleasedKind = StrikeBombWeaponKind.Bomb;
        pilot.Fire();
        Log("strike_fire kind=" + StrikeBombWeaponKind.Bomb + " count=" + releasedTargetIds.Count + " stage=" + stage + " target_pid=" + target.persistentID + " station=" + drone.weaponStations.IndexOf(station) + " ammo=" + station.Ammo);
        target = null;
    }

    private void UseGlideBomb(Pilot pilot, AutopilotPlane autopilot, WeaponStation station, GlobalPosition known)
    {
        WeaponInfo? info = station.WeaponInfo;
        if (info == null) { Hold(autopilot); return; }
        Unit currentTarget = target!;
        Vector3 delta = known - drone.GlobalPosition();
        float distance = delta.magnitude;
        float angle = Vector3.Angle(drone.transform.forward, delta);
        float height = (drone.GlobalPosition() - currentTarget.GlobalPosition()).y;
        bool obscured = !currentTarget.LineOfSight(drone.transform.position - Vector3.up * drone.definition.spawnOffset.y, 1000f);
        Approach(autopilot, known, StrikeBombWeaponKind.GlideBomb, 0f);
        if (StrikeBombingLogic.InitialGlideMissedWindow(hasGlideRelease, distance, info.targetRequirements.minRange))
        {
            glideMissedWindow = true;
            glideReattackMinRange = info.targetRequirements.minRange;
            LogGlide("state=strike_glide_missed_window_enter", station, distance, glideReattackMinRange, height,
                drone.speed, angle, info.targetRequirements.minAlignment, obscured, StrikeRippleDecision.BreakOff);
            LogGlideGate(station, distance, glideReattackMinRange, height, drone.speed, angle,
                info.targetRequirements.minAlignment, obscured, StrikeRippleDecision.BreakOff, "initial_missed_window");
            BreakOff(autopilot);
            return;
        }
        bool geometryValid = StrikeBombingLogic.GlideGeometryValid(distance, info.targetRequirements.minRange, obscured, height,
            drone.speed, angle, info.targetRequirements.minAlignment);
        bool stationReady = StationStillReady(station, StrikeBombWeaponKind.GlideBomb);
        float elapsedSinceGlide = Time.timeSinceLevelLoad - lastGlideReleasedAt;
        string reason = StrikeBombingLogic.GlideGateReason(distance, info.targetRequirements.minRange, obscured, height,
            drone.speed, angle, info.targetRequirements.minAlignment, stationReady, elapsedSinceGlide);
        StrikeRippleDecision decision = ResolveRipple(StrikeBombingLogic.ResolveGlideRipple(geometryValid, stationReady, elapsedSinceGlide));
        LogGlideGate(station, distance, info.targetRequirements.minRange, height, drone.speed, angle,
            info.targetRequirements.minAlignment, obscured, decision, reason);
        if (decision == StrikeRippleDecision.ContinueIngress) return;
        if (decision == StrikeRippleDecision.BreakOff) { BeginPostRelease(autopilot, "glide_solution_invalid"); return; }

        if (!manager.StrikeOwns(drone, this, Revision) || target == null || !CombatSupport.ValidHostileTrackedUnit(drone, target) ||
            !TryKnownPosition(target, out GlobalPosition currentKnown) || !StationStillReady(station, StrikeBombWeaponKind.GlideBomb)) return;
        delta = currentKnown - drone.GlobalPosition();
        distance = delta.magnitude;
        angle = Vector3.Angle(drone.transform.forward, delta);
        height = (drone.GlobalPosition() - currentTarget.GlobalPosition()).y;
        obscured = !currentTarget.LineOfSight(drone.transform.position - Vector3.up * drone.definition.spawnOffset.y, 1000f);
        geometryValid = StrikeBombingLogic.GlideGeometryValid(distance, info.targetRequirements.minRange, obscured, height,
            drone.speed, angle, info.targetRequirements.minAlignment);
        stationReady = StationStillReady(station, StrikeBombWeaponKind.GlideBomb);
        elapsedSinceGlide = Time.timeSinceLevelLoad - lastGlideReleasedAt;
        reason = StrikeBombingLogic.GlideGateReason(distance, info.targetRequirements.minRange, obscured, height,
            drone.speed, angle, info.targetRequirements.minAlignment, stationReady, elapsedSinceGlide);
        decision = ResolveRipple(StrikeBombingLogic.ResolveGlideRipple(geometryValid, stationReady, elapsedSinceGlide));
        LogGlideGate(station, distance, info.targetRequirements.minRange, height, drone.speed, angle,
            info.targetRequirements.minAlignment, obscured, decision, reason);
        if (decision == StrikeRippleDecision.BreakOff) { BeginPostRelease(autopilot, "glide_recheck_invalid"); return; }
        if (decision != StrikeRippleDecision.FireNext) return;
        drone.weaponManager.currentWeaponStation = station;
        SetOnlyTarget();
        releasedTargetIds.Add(target.persistentID); lastReleasedKind = StrikeBombWeaponKind.GlideBomb;
        hasGlideRelease = true;
        ClearGlideMissedWindow();
        lastGlideReleasedAt = Time.timeSinceLevelLoad;
        pilot.Fire();
        Log("strike_fire kind=" + StrikeBombWeaponKind.GlideBomb + " count=" + releasedTargetIds.Count + " stage=" + stage + " target_pid=" + target.persistentID + " station=" + drone.weaponStations.IndexOf(station) + " ammo=" + station.Ammo);
        target = null;
    }

    private void Approach(AutopilotPlane autopilot, GlobalPosition known, StrikeBombWeaponKind kind, float difference)
    {
        drone.GetInputs().throttle = 1f;
        drone.GetInputs().brake = 0f;
        GlobalPosition position = drone.GlobalPosition();
        Vector3 direction = known - position;
        if (kind == StrikeBombWeaponKind.GlideBomb)
        {
            float up = Mathf.Min(drone.speed * .12f, 50f) / Mathf.Max(drone.speed, 10f);
            direction.Normalize();
            direction.y = 0f;
            autopilot.AutoAim(position + (direction + Vector3.up * up) * 500f, true, false, false, .5f, 180f, true, drone.radarAlt, drone.rb.velocity);
            return;
        }
        float globalY = drone.transform.position.GlobalY();
        targetHeight = Mathf.Max(targetHeight, globalY - known.y);
        targetHeight += Mathf.Min(drone.speed * .2f, 50f) * Time.deltaTime;
        float climb = Mathf.Min(drone.speed * .12f, 50f);
        if (difference < 15f && globalY < 2000f) climb *= 2f;
        float upFactor = climb / Mathf.Max(drone.speed, 10f);
        Vector3 targetVelocity = target != null && target.rb != null ? target.rb.velocity : Vector3.zero;
        direction = target!.GlobalPosition() + targetVelocity * Mathf.Clamp(difference, 0f, 30f) - position;
        direction.y = 0f;
        autopilot.AutoAim(position + (direction.normalized + Vector3.up * upFactor) * 500f, true, false, false, .5f, 180f, true, targetHeight, targetVelocity);
    }

    private void PostRelease(AutopilotPlane autopilot)
    {
        float elapsed = Time.timeSinceLevelLoad - releasedAt;
        float holdSeconds = releasedKind == StrikeBombWeaponKind.Bomb ? 3f : 2f;
        float breakSeconds = releasedKind == StrikeBombWeaponKind.Bomb ? 15f : 2f;
        if (elapsed < holdSeconds)
        {
            Vector3 vector = drone.rb.velocity.normalized + (releasedKind == StrikeBombWeaponKind.Bomb ? Vector3.up * .1f : Vector3.zero);
            autopilot.AutoAim(drone.GlobalPosition() + vector * 500f, true, false, false, .5f, 180f, true, targetHeight, drone.rb.velocity);
            return;
        }
        releasedKind = StrikeBombWeaponKind.None;
        breakOffUntil = Time.timeSinceLevelLoad + breakSeconds;
        BreakOff(autopilot);
    }
    private void BeginPostRelease(AutopilotPlane autopilot, string reason)
    {
        if (releasedTargetIds.Count == 0) return;
        postReleaseStarted = true;
        releasedKind = lastReleasedKind;
        releasedAt = Time.timeSinceLevelLoad;
        Log("strike_ripple_breakoff reason=" + reason + " count=" + releasedTargetIds.Count);
        PostRelease(autopilot);
    }
    private StrikeRippleDecision ResolveRipple(StrikeRippleDecision decision) =>
        StrikeBombingLogic.PreserveInitialIngress(decision, releasedTargetIds.Count > 0);
    private void NativeBombBreakOff(AutopilotPlane autopilot, string reason)
    {
        if (releasedTargetIds.Count > 0) { BeginPostRelease(autopilot, reason); return; }
        breakOffUntil = Time.timeSinceLevelLoad + 15f;
        BreakOff(autopilot);
    }
    private void ClearGlideMissedWindow()
    {
        glideMissedWindow = false;
        glideReattackMinRange = 0f;
        lastGlideGate = null;
    }
    private void ResetGlideState()
    {
        ClearGlideMissedWindow();
        hasGlideRelease = false;
        lastGlideReleasedAt = float.NegativeInfinity;
    }
    private void LogGlide(string state, WeaponStation? station, float distance, float minRange, float height,
        float speed, float angle, float minAlignment, bool obscured, StrikeRippleDecision decision)
    {
        int index = station == null ? -1 : drone.weaponStations.IndexOf(station);
        string targetPid = target == null ? "none" : target.persistentID.ToString();
        string readiness = station == null ? "ammo=- reload=- salvo=- ready=- safety=-" :
            "ammo=" + station.Ammo + " reload=" + station.Reloading + " salvo=" + station.SalvoInProgress +
            " ready=" + station.Ready() + " safety=" + station.SafetyIsOn(drone);
        Log(state + " pid=" + drone.persistentID + " target_pid=" + targetPid + " station=" + index +
            " kind=GlideBomb " + readiness + " distance=" + distance + " min_range=" + minRange +
            " height=" + height + " speed=" + speed + " gradient=" + StrikeBombingLogic.GlideReleaseGradient(height, speed, distance) +
            " angle=" + angle + " min_alignment=" + minAlignment + " obscured=" + obscured +
            " decision=" + decision + " latch=" + glideMissedWindow);
    }
    private void LogGlideGate(WeaponStation? station, float distance, float minRange, float height, float speed,
        float angle, float minAlignment, bool obscured, StrikeRippleDecision decision, string reason)
    {
        string key = reason + ":" + decision + ":" + glideMissedWindow;
        if (key == lastGlideGate) return;
        lastGlideGate = key;
        LogGlide("state=strike_glide_gate reason=" + reason, station, distance, minRange, height, speed, angle,
            minAlignment, obscured, decision);
    }

    private WeaponStation? Select(StrikeBombWeaponKind kind)
    {
        for (int i = 0; i < drone.weaponStations.Count; i++)
        {
            WeaponStation station = drone.weaponStations[i];
            if (CombatSupport.StrikeBombStationCompatible(station) && CombatSupport.GetStrikeBombWeaponKind(station) == kind && station.Ammo > 0) return station;
        }
        return null;
    }

    private bool StationStillReady(WeaponStation station, StrikeBombWeaponKind kind) =>
        drone.weaponStations.Contains(station) && CombatSupport.StrikeBombStationCompatible(station) &&
        CombatSupport.GetStrikeBombWeaponKind(station) == kind && CombatSupport.StrikeBombStationSafe(drone, station);

    private bool TryKnownPosition(Unit unit, out GlobalPosition known)
    {
        known = default;
        return drone.NetworkHQ != null && drone.NetworkHQ.TryGetKnownPosition(unit, out known) && Finite(known);
    }

    private void Hold(AutopilotPlane autopilot)
    {
        drone.GetInputs().throttle = 1f;
        drone.GetInputs().brake = 0f;
        autopilot.AutoAim(drone.GlobalPosition() + drone.transform.forward * 500f, true, false, false, .5f, 180f, true, drone.radarAlt, drone.rb.velocity);
    }

    private void BreakOff(AutopilotPlane autopilot)
    {
        drone.GetInputs().throttle = 1f;
        drone.GetInputs().brake = 0f;
        autopilot.AutoAim(drone.GlobalPosition() + (drone.transform.forward + Vector3.up * .2f) * 1000f, true, false, false, .5f, 180f, true, targetHeight, drone.rb.velocity);
    }

    private void SetOnlyTarget() { drone.weaponManager.ClearTargetList(); drone.weaponManager.AddTargetList(target!); }
    private static bool Finite(GlobalPosition position) => CombatSupport.Finite(position.x) && CombatSupport.Finite(position.y) && CombatSupport.Finite(position.z);
    private void Complete(string reason) { ResetGlideState(); Log("strike_completion reason=" + reason); manager.CompleteMission(drone, WingmanMission.Strike, reason); }
    public override void LeaveState() { ResetGlideState(); try { if (drone.weaponManager != null) drone.weaponManager.ClearTargetList(); drone.GetInputs().brake = 0f; } catch { } Log("strike_leave"); }
    private void Log(string text) => manager.CombatLog(drone, text);
}
