using System;
using UnityEngine;

namespace LoyalWingman;

// Intercept finds a solution, Launch fires once, BreakAway lasts eight seconds, then the cycle resumes.
internal sealed class FixedWingDroneAirToAirAttackState : PilotBaseState
{
    private enum Phase
    {
        Intercept,
        Launch,
        BreakAway
    }
    private readonly WingmanAiManager manager;
    private readonly Aircraft drone, target;
    private WeaponStation? launchStation;
    private Phase phase;
    private bool fireIssued, holdReported;
    private float breakUntil, nextTelemetry;

    internal Aircraft Target => target;
    internal FixedWingDroneAirToAirAttackState(WingmanAiManager manager, Aircraft drone, Aircraft target)
    {
        this.manager = manager;
        this.drone = drone;
        this.target = target;
    }
    public override void EnterState(Pilot p)
    {
        drone.weaponManager.ClearTargetList();
        drone.weaponManager.AddTargetList(target);
        Log("combat_enter");
    }
    public override void UpdateState(Pilot p)
    {
    }
    public override void FixedUpdateState(Pilot p)
    {
        try
        {
            if (!manager.CombatOwns(drone, this))
                return;
            if (!CombatSupport.ValidAircraft(target))
            {
                manager.InvalidateAirToAirTarget(drone, target, "target_invalid");
                return;
            }
            if (phase == Phase.Intercept)
            {
                if (!Guide())
                {
                    Fail("intercept");
                    return;
                }
                launchStation = FindBestLaunchStation();
                if (launchStation != null)
                    phase = Phase.Launch;
                else if (IsExplicitAirToAir() &&
                         WingmanAiManager.MissionWeaponStatus(drone, WingmanMission.AirToAir) is
                         StrikeWeaponStatus.Missing or StrikeWeaponStatus.Exhausted)
                    Complete("all_compatible_aam_exhausted");
                return;
            }
            if (phase == Phase.Launch)
            {
                if (Fire())
                {
                    phase = Phase.BreakAway;
                    breakUntil = Time.realtimeSinceStartup + 8f;
                }
                else
                    phase = Phase.Intercept;
                return;
            }
            if (!BreakAway())
            {
                Fail("breakaway");
                return;
            }
            if (Time.realtimeSinceStartup >= breakUntil)
            {
                phase = Phase.Intercept;
                fireIssued = false;
                launchStation = null;
                holdReported = false;
            }
        }
        catch (Exception e)
        {
            Fail("exception_" + e.GetType().Name);
        }
    }
    private void Fail(string reason)
    {
        Log("combat_suspend reason=" + reason);
        manager.Suspend(drone, "a2a_" + reason);
    }
    private void Complete(string reason)
    {
        Log("combat_completion reason=" + reason);
        manager.CompleteMission(drone, WingmanMission.AirToAir, reason);
    }
    private bool Fire()
    {
        if (fireIssued)
            return false;
        WeaponStation? station = FindBestLaunchStation();
        if (station == null || !manager.MayFire(drone, this) ||
            !CombatSupport.LaunchContext(drone, target, station, true, out _))
            return false;
        WeaponManager? weapons = drone.weaponManager;
        if (weapons == null || drone.pilots == null || drone.pilots.Length == 0 || drone.pilots[0] == null)
            return false;
        fireIssued = true;
        weapons.currentWeaponStation = station;
        weapons.ClearTargetList();
        weapons.AddTargetList(target);
        drone.pilots[0].Fire();
        Log("combat_fire");
        manager.CombatMissileAway(drone);
        return true;
    }
    private bool IsExplicitAirToAir() => manager.TryGetAirToAirTarget(drone, out Aircraft assigned) && assigned == target;
    private WeaponStation? FindBestLaunchStation()
    {
        WeaponStation? best = null;
        float bestOpportunity = float.NegativeInfinity;
        int bestIndex = int.MaxValue;
        bool physical = false;
        for (int index = 0; index < drone.weaponStations.Count; index++)
        {
            WeaponStation station = drone.weaponStations[index];
            if (!CombatSupport.LaunchContext(drone, target, station, true, out _))
                continue;
            physical = true;
            if (!CombatSupport.TryOpportunity(drone, target, station, out float opportunity) || opportunity <= 0f)
                continue;
            if (best == null || CombatCommandLogic.IsBetterOpportunity(opportunity, index, bestOpportunity, bestIndex))
            {
                best = station;
                bestOpportunity = opportunity;
                bestIndex = index;
            }
        }
        if (best == null && physical && !holdReported)
        {
            holdReported = true;
            Log("a2a_hold_fire no_solution");
            manager.A2aHoldFire();
        }
        return best;
    }
    private bool Guide()
    {
        if (drone.autopilot is not AutopilotPlane ap || drone.rb == null || target.rb == null)
            return false;
        Vector3 aim =
            target.transform.position +
            target.rb.velocity * Mathf.Clamp(Vector3.Distance(drone.transform.position, target.transform.position) /
                                                 Mathf.Max(1f, drone.rb.velocity.magnitude),
                                             0f, 20f);
        float altitude = target.radarAlt;
        if (!CombatSupport.Finite(aim) || !CombatSupport.Finite(altitude) || !CombatSupport.Finite(target.rb.velocity))
            return false;
        drone.GetInputs().throttle = 1f;
        drone.GetInputs().brake = 0f;
        ap.AutoAim(aim.ToGlobalPosition(), true, false, false, .5f, 75f, true, altitude, target.rb.velocity);
        if (Time.fixedTime >= nextTelemetry)
        {
            nextTelemetry = Time.fixedTime + 2f;
            Log("combat_telemetry phase=Intercept");
        }
        return true;
    }
    private bool BreakAway()
    {
        if (drone.autopilot is not AutopilotPlane ap || drone.rb == null)
            return false;
        Vector3 delta = target.transform.position - drone.transform.position;
        if (!CombatSupport.Finite(delta) || delta.sqrMagnitude < .01f)
            return false;
        Vector3 aim = drone.transform.position - delta.normalized * 2500f + Vector3.up * 400f;
        ap.AutoAim(aim.ToGlobalPosition(), true, false, false, .5f, 75f, true, drone.radarAlt + 300f,
                   drone.rb.velocity);
        return true;
    }
    public override void LeaveState()
    {
        drone.weaponManager.ClearTargetList();
    }
    private void Log(string text) => manager.CombatLog(drone, text);
}
