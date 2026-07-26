using System;
using UnityEngine;
namespace LoyalWingman;
// Threat validity is independent from the one-shot attempted latch, so defense continues after firing.
internal sealed class FixedWingDroneMissileDefenseState : PilotBaseState
{
    private readonly WingmanAiManager manager;
    private readonly Aircraft drone;
    private readonly Missile threat;
    private MissileDefensePhase phase;
    private bool attempted, terminalLogged;
    private float lastRange, lastTti;
    private readonly CountermeasurePulseController countermeasurePulse = new CountermeasurePulseController();
    internal Missile Threat => threat;
    internal FixedWingDroneMissileDefenseState(WingmanAiManager manager, Aircraft drone, Missile threat)
    {
        this.manager = manager;
        this.drone = drone;
        this.threat = threat;
    }
    public override void EnterState(Pilot p)
    {
        Kinematics(out _, out float range, out float closing, out float tti);
        bool hasIr = false, readyIr = false;
        foreach (WeaponStation station in drone.weaponStations)
        {
            hasIr |= CombatSupport.StationHasSeeker(station, true);
            readyIr |= CombatSupport.StationSafe(drone, station, true);
        }
        manager.CombatLog(drone, "missile_defense_enter threat_pid=" + threat.persistentID + " seeker=" +
                                 threat.GetSeekerType() + " range=" + range + " closing=" + closing + " tti=" + tti +
                                 " hasIR=" + hasIr + " readyIR=" + readyIr);
    }
    public override void UpdateState(Pilot p) { }
    public override void FixedUpdateState(Pilot p)
    {
        try
        {
            if (!manager.TacticalOwns(drone, this) || !manager.ActiveDefenseThreat(drone, threat))
            {
                Exit("threat_invalid");
                return;
            }
            Kinematics(out Vector3 delta, out _, out _, out float tti);
            RadarBeam beam = RadarBeamFor(delta);
            MissileSeekerType seeker = GetSeeker();
            Vector3 rawRadarEvade = seeker == MissileSeekerType.Radar ? RawRadarEvade(beam) : Vector3.zero;
            countermeasurePulse.Update(drone, threat, seeker != MissileSeekerType.Radar ||
                                MissileDefenseLogic.ShouldPulseRadarCountermeasure(tti,
                                    Vector3.Angle(rawRadarEvade, drone.rb.velocity)));
            float maneuverTime = ManeuverTime(seeker, beam);
            if (phase == MissileDefensePhase.Maneuver)
            {
                Maneuver(delta, false, rawRadarEvade, tti, seeker);
                Fire(false);
                phase = attempted ? MissileDefensePhase.ResumeManeuver
                                  : MissileDefenseLogic.AdvancePhase(phase, tti, maneuverTime);
                return;
            }
            if (phase == MissileDefensePhase.TerminalAcquire)
            {
                if (!terminalLogged)
                {
                    terminalLogged = true;
                    manager.CombatLog(drone, "missile_defense_terminal_acquire threat_pid=" + threat.persistentID +
                                             " seeker=" + threat.GetSeekerType() + " tti=" + tti + " maneuverTime=" +
                                             maneuverTime + " attempted=" + attempted);
                }
                Maneuver(delta, true, rawRadarEvade, tti, seeker);
                phase = MissileDefenseLogic.AdvancePhase(phase, tti, maneuverTime);
                return;
            }
            if (phase == MissileDefensePhase.FireAttempt)
            {
                phase = Fire(true) ? MissileDefensePhase.ResumeManeuver : MissileDefensePhase.TerminalAcquire;
                Maneuver(delta, true, rawRadarEvade, tti, seeker);
                return;
            }
            if (phase == MissileDefensePhase.ResumeManeuver)
                Maneuver(delta, false, rawRadarEvade, tti, seeker);
        }
        catch (Exception e)
        {
            countermeasurePulse.Stop(drone);
            manager.CombatLog(drone, "missile_defense_exception error=" + e.GetType().Name);
            manager.ResumeDesired(drone, this, "exception_" + e.GetType().Name);
        }
    }
    private void Kinematics(out Vector3 delta, out float range, out float closing, out float tti)
    {
        delta = drone.transform.position - threat.transform.position;
        range = delta.magnitude;
        closing = range > .1f ? Vector3.Dot(-delta.normalized, drone.rb.velocity - threat.rb.velocity) : float.NaN;
        tti = closing > 0f ? range / closing : float.PositiveInfinity;
        if (!CombatSupport.Finite(tti) || tti < 0f)
            tti = float.PositiveInfinity;
        lastRange = range;
        lastTti = tti;
    }
    private WeaponStation? FindStation(bool forced)
    {
        for (int i = 0; i < drone.weaponStations.Count; i++)
        {
            WeaponStation s = drone.weaponStations[i];
            if (CombatSupport.StationSafe(drone, s, true) && CombatSupport.RequiredLineOfSight(drone, threat, s) &&
                (forced || CombatSupport.HudShoot(drone, threat, s, false)))
                return s;
        }
        return null;
    }
    private bool Fire(bool forced)
    {
        if (attempted)
            return false;
        WeaponStation? s = FindStation(forced);
        if (s == null || !manager.MayFire(drone, this))
            return false;
        WeaponManager? w = drone.weaponManager;
        if (w == null || drone.pilots == null || drone.pilots.Length == 0 || drone.pilots[0] == null ||
            !manager.TryMarkDefenseAttempt(drone, threat))
            return false;
        attempted = true;
        w.currentWeaponStation = s;
        w.ClearTargetList();
        w.AddTargetList(threat);
        drone.pilots[0].Fire();
        float angle = Vector3.Angle(drone.transform.forward, threat.transform.position - drone.transform.position);
        manager.CombatLog(drone, "missile_defense_fire_issued threat_pid=" + threat.persistentID + " seeker=" +
                                 threat.GetSeekerType() + " forced=" + forced + " station=" + drone.weaponStations.IndexOf(s) +
                                 " range=" + lastRange + " angle=" + angle + " tti=" + lastTti);
        return true;
    }
    private MissileSeekerType GetSeeker() => MissileDefenseLogic.ClassifySeekerType(threat.GetSeekerType());
    private RadarBeam RadarBeamFor(Vector3 delta)
    {
        Vector3 velocity = Vector3.ProjectOnPlane(drone.rb.velocity, Vector3.up),
                heading = velocity.sqrMagnitude > .01f ? velocity
                                                        : Vector3.ProjectOnPlane(drone.transform.forward, Vector3.up),
                bearing = Vector3.ProjectOnPlane(-delta, Vector3.up);
        return MissileDefenseLogic.ChooseRadarBeam(heading.x, heading.z, bearing.x, bearing.z);
    }
    private float ManeuverTime(MissileSeekerType seeker, RadarBeam beam) => seeker == MissileSeekerType.Radar
                                                                                  ? MissileDefenseLogic.RadarManeuverTime(beam.error)
                                                                                  : 2.5f;
    private void Maneuver(Vector3 delta, bool noseOn, Vector3 rawRadarEvade, float tti, MissileSeekerType seeker)
    {
        if (drone.autopilot is not AutopilotPlane ap || drone.rb == null)
            return;
        Vector3 dir;
        if (noseOn)
            dir = -delta;
        else if (seeker == MissileSeekerType.Radar)
            dir = RadarNotchDirection(rawRadarEvade, tti);
        else
            dir = Vector3.Cross(Vector3.up, delta);
        if (!CombatSupport.Finite(dir) || dir.sqrMagnitude < .01f)
            dir = Vector3.Cross(Vector3.up, drone.transform.forward);
        if (!CombatSupport.Finite(dir) || dir.sqrMagnitude < .01f)
            return;
        Vector3 aim = drone.transform.position + dir.normalized * 3000f;
        if (seeker == MissileSeekerType.Radar)
        {
            bool terminalClimb = MissileDefenseLogic.ShouldTerminalClimb(tti);
            if (terminalClimb)
                aim += Vector3.up * 1000f;
            drone.GetInputs().throttle = 1f;
            ap.AutoAim(aim.ToGlobalPosition(), true, false, false, 1f, 180f, !terminalClimb,
                       Mathf.Clamp(10f, drone.maxRadius, 8000f), drone.rb.velocity);
            return;
        }
        if (!noseOn)
            drone.GetInputs().throttle = 0f;
        ap.AutoAim(aim.ToGlobalPosition(), true, false, false, .5f, 75f, true, drone.radarAlt, drone.rb.velocity);
    }
    private Vector3 RawRadarEvade(RadarBeam beam)
    {
        Vector3 fallback = new Vector3(beam.x, 0f, beam.z);
        Vector3 toEvasion = threat.GetEvasionPoint() - drone.GlobalPosition();
        if (!CombatSupport.Finite(toEvasion) || toEvasion.sqrMagnitude < .01f)
            return fallback;
        Vector3 rhs = Vector3.Cross(toEvasion, drone.rb.velocity);
        if (!CombatSupport.Finite(rhs) || rhs.sqrMagnitude < .01f)
            return fallback;
        Vector3 evade = Vector3.Cross((-toEvasion).normalized, rhs);
        if (!CombatSupport.Finite(evade) || evade.sqrMagnitude < .01f)
            return fallback;
        evade.Normalize();
        if (Vector3.Dot(evade, drone.transform.forward) < 0f)
            evade = -evade;
        return evade;
    }
    private Vector3 RadarNotchDirection(Vector3 rawRadarEvade, float tti)
    {
        if (!CombatSupport.Finite(rawRadarEvade) || rawRadarEvade.sqrMagnitude < .01f)
            return Vector3.forward;
        if (tti > 7f)
        {
            Vector3 blended = Vector3.Lerp(drone.transform.forward.normalized, rawRadarEvade, .3f);
            if (CombatSupport.Finite(blended) && blended.sqrMagnitude >= .01f)
                return blended.normalized;
        }
        return rawRadarEvade;
    }
    private void Exit(string reason)
    {
        if (phase == MissileDefensePhase.Exit)
            return;
        phase = MissileDefensePhase.Exit;
        countermeasurePulse.Stop(drone);
        manager.CombatLog(drone, "missile_defense_exit threat_pid=" + threat.persistentID + " seeker=" +
                                 threat.GetSeekerType() + " reason=" + reason + " attempted=" + attempted +
                                 " lastTti=" + lastTti);
        manager.ResumeDesired(drone, this, reason);
    }
    public override void LeaveState() => countermeasurePulse.Stop(drone);
}
