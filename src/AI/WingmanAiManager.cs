using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace LoyalWingman;

internal enum WingmanMode
{
    Follow,
    Loiter
}

// Permanent slots survive mode changes; each record independently owns its drone's desired state.
internal sealed class WingmanAiManager
{
    private readonly WingmanSessionContext context;
    private readonly Action<string> log;
    private readonly WingmanStatusReporter reporter;
    private readonly HashSet<Aircraft> registered = new HashSet<Aircraft>();
    private readonly HashSet<Aircraft> rejected = new HashSet<Aircraft>();
    private readonly Dictionary<Aircraft, Record> records = new Dictionary<Aircraft, Record>();
    private readonly Dictionary<Aircraft, Action<Missile>> missileRegisterHandlers = new Dictionary<Aircraft, Action<Missile>>();
    private readonly Dictionary<Aircraft, Action<Missile>> missileDeregisterHandlers = new Dictionary<Aircraft, Action<Missile>>();
    private readonly Dictionary<Aircraft, SeparationDirective> separation =
        new Dictionary<Aircraft, SeparationDirective>();
    private readonly HashSet<Aircraft> hullEnvelopeLogged = new HashSet<Aircraft>();
    private float nextReconcile, nextSample, nextCombatScan, nextMissileDefenseScan;
    private Aircraft? activeLeader;
    private bool leaderAvailable;
    private GlobalPosition anchor, rawLeader;
    private bool hasAnchor, hasRawLeader;
    private Vector3 anchorVelocity;
    private float anchorRadarAlt = 700f, relativeUp = 700f, candidateSince = -1f, lastSampleTime;
    private WingmanMode mode;
    private bool modeInitialized, jumpLatched;
    private float epoch, epochTime;
    internal WingmanAiManager(WingmanSessionContext context, WingmanStatusReporter reporter)
    {
        this.context = context;
        log = context.Log;
        this.reporter = reporter;
        reporter.SetManagedCallsignResolver(drone => TryGetPresentationLabel(drone, out string label) ? label : null);
    }
    internal bool IsRegistered(Aircraft aircraft) => registered.Contains(aircraft);
    internal bool TryGetPresentationLabel(Aircraft? drone, out string label)
    {
        if (drone != null && records.TryGetValue(drone, out Record r))
        {
            label = WingmanGroupLogic.FqPresentationLabel(r.slotOrdinal);
            return true;
        }
        label = "";
        return false;
    }
    internal WingmanMode CurrentMode => mode;
    internal bool IsReturningToBase(Aircraft drone) =>
        records.TryGetValue(drone, out Record r) && r.returningToBase;
    internal bool TryGetMission(Aircraft drone, out WingmanMission mission)
    {
        if (records.TryGetValue(drone, out Record r))
        {
            mission = r.mission;
            return true;
        }
        mission = WingmanMission.None;
        return false;
    }
    internal bool TryGetMapStatus(Aircraft drone, out int slotOrdinal, out DroneModeOverride modeOverride,
                                  out bool attackAssigned, out bool defending, out bool attacking)
    {
        if (!records.TryGetValue(drone, out Record r))
        {
            slotOrdinal = -1;
            modeOverride = DroneModeOverride.Auto;
            attackAssigned = defending = attacking = false;
            return false;
        }
        slotOrdinal = r.slotOrdinal;
        modeOverride = r.modeOverride;
        attackAssigned = r.mission != WingmanMission.None;
        defending = r.state is FixedWingDroneMissileDefenseState;
        attacking = r.state is FixedWingDroneAirToAirAttackState;
        return true;
    }
    internal bool TryGetMapGroup(Aircraft drone, out int groupId, out int groupSlot)
    {
        if (records.TryGetValue(drone, out Record r))
        {
            groupId = r.groupId;
            groupSlot = r.groupSlot;
            return true;
        }
        groupId = groupSlot = -1;
        return false;
    }
    internal bool Register(Aircraft drone, int ordinal, int groupId, int groupSlot)
    {
        if (!registered.Add(drone))
            return false;
        FormationSlot slot = FormationSlot.For(ordinal);
        Record r = new Record(ordinal, PhaseFor(ordinal), slot, groupId, groupSlot);
        records[drone] = r;
        Action<Missile> registeredHandler = missile => OnMissionMissileRegistered(drone, missile);
        Action<Missile> deregisteredHandler = missile => OnMissionMissileDeregistered(drone, missile);
        missileRegisterHandlers[drone] = registeredHandler; missileDeregisterHandlers[drone] = deregisteredHandler;
        drone.onRegisterMissile += registeredHandler; drone.onDeregisterMissile += deregisteredHandler;
        log("state=wingman_slot_assigned pid=" + Pid(drone) + " slot=" + ordinal +
            " back_m=" + slot.back.ToString("F0", CultureInfo.InvariantCulture) +
            " lateral_m=" + slot.lateral.ToString("F0", CultureInfo.InvariantCulture) +
            " up_m=" + slot.up.ToString("F0", CultureInfo.InvariantCulture) +
            " phase_deg=" + r.loiterPhase.ToString("F0", CultureInfo.InvariantCulture));
        Log("follow_registered", drone, "spawned");
        return true;
    }
    internal bool TryBeginLaunchClearance(Aircraft drone, Aircraft carrier, Vector3 egressDirection,
                                           out string reason)
    {
        reason = "";
        if (!records.TryGetValue(drone, out Record r) || !registered.Contains(drone))
        {
            reason = "not_registered";
            return false;
        }
        if (r.returningToBase || context.HasPlayerAssociation(drone) ||
            !TryKinematics(drone, r.slotOrdinal, false, out _) || !TryKinematics(carrier, -1, true, out _) ||
            drone.pilots == null || drone.pilots.Length == 0 || drone.pilots[0] == null ||
            drone.pilots[0].pilotType != Pilot.PilotType.Plane || drone.autopilot is not AutopilotPlane ||
            !drone.IsServer || !drone.LocalSim)
        {
            reason = "runtime";
            return false;
        }
        float egressMagnitude = egressDirection.magnitude;
        if (!Finite(egressDirection) || !Finite(egressMagnitude) || egressMagnitude <= 0f)
        {
            reason = "egress_direction";
            return false;
        }
        egressDirection.Normalize();
        if (!Finite(egressDirection) || egressDirection.sqrMagnitude <= 0f)
        {
            reason = "egress_direction";
            return false;
        }
        PersistentID carrierId = carrier.persistentID;
        bool available = TryMeasureLaunchSeparation(drone, carrier, carrierId, out float current,
                                                     out float minimum, out _, out _);
        if (!HullCpaLogic.LaunchClearanceRequired(available, current, minimum))
            return true;
        r.launchClearanceActive = true;
        r.launchCarrier = carrier;
        r.launchCarrierId = carrierId;
        r.egressDirection = egressDirection;
        FixedWingDroneLaunchClearanceState state =
            new FixedWingDroneLaunchClearanceState(this, drone, carrier, carrierId, egressDirection);
        if (SwitchTo(drone, r, state, "launch_clearance"))
            return true;
        ClearLaunchClearance(r);
        reason = "state_install";
        return false;
    }
    internal bool TrySampleLaunchClearance(Aircraft drone, FixedWingDroneLaunchClearanceState state,
                                            Aircraft carrier, PersistentID carrierId, out float currentClearance,
                                            out float minimumClearance, out float timeToMinimum, out string source)
    {
        currentClearance = minimumClearance = timeToMinimum = -1f;
        source = "unavailable";
        return records.TryGetValue(drone, out Record r) && r.launchClearanceActive && r.state == state &&
               ReferenceEquals(r.launchCarrier, carrier) && r.launchCarrierId == carrierId &&
               TryMeasureLaunchSeparation(drone, carrier, carrierId, out currentClearance, out minimumClearance,
                                          out timeToMinimum, out source);
    }
    internal void CompleteLaunchClearance(Aircraft drone, FixedWingDroneLaunchClearanceState state,
                                           Aircraft carrier, PersistentID carrierId, string reason)
    {
        if (!records.TryGetValue(drone, out Record r) || !r.launchClearanceActive || r.state != state ||
            !ReferenceEquals(r.launchCarrier, carrier) || r.launchCarrierId != carrierId)
            return;
        bool available = TryMeasureLaunchSeparation(drone, carrier, carrierId, out float current, out float minimum,
                                                     out float timeToMinimum, out string source);
        ClearLaunchClearance(r);
        log(string.Format(CultureInfo.InvariantCulture,
            "state=launch_clearance_handoff pid={0} carrier_pid={1} reason={2} source={3} available={4} " +
            "current_clearance_m={5:F1} cpa_clearance_m={6:F1} time_to_cpa_s={7:F2} desired_mode={8} mission={9}",
            Pid(drone), carrierId, reason, source, available, current, minimum, timeToMinimum,
            r.modeOverride == DroneModeOverride.Auto ? mode.ToString() : r.modeOverride.ToString(), r.mission));
        ApplyDesired(drone, r, "launch_clearance_" + reason);
    }
    internal bool OwnsLaunchClearance(Aircraft drone, FixedWingDroneLaunchClearanceState state,
                                       Aircraft carrier, PersistentID carrierId) =>
        records.TryGetValue(drone, out Record r) && r.launchClearanceActive && r.state == state &&
        ReferenceEquals(r.launchCarrier, carrier) && r.launchCarrierId == carrierId && RuntimeOwns(drone, state, out _);
    internal void LogLaunchClearanceEnter(Aircraft drone, Aircraft carrier, PersistentID carrierId,
                                           Vector3 egressDirection)
    {
        if (!records.TryGetValue(drone, out Record r) || !r.launchClearanceActive ||
            !ReferenceEquals(r.launchCarrier, carrier) || r.launchCarrierId != carrierId)
            return;
        log(string.Format(CultureInfo.InvariantCulture,
            "state=launch_clearance_enter pid={0} carrier_pid={1} egress_direction=({2:F4},{3:F4},{4:F4}) replaced={5}",
            Pid(drone), carrierId, egressDirection.x, egressDirection.y, egressDirection.z, StateName(r.state)));
    }
    internal void LogLaunchClearanceControl(Aircraft drone, Aircraft carrier, PersistentID carrierId, string source,
                                             bool available, float current, float minimum, float timeToMinimum,
                                             float alignment) =>
        log(string.Format(CultureInfo.InvariantCulture,
            "state=launch_clearance_control pid={0} carrier_pid={1} source={2} available={3} " +
            "current_clearance_m={4:F1} cpa_clearance_m={5:F1} time_to_cpa_s={6:F2} egress_velocity_alignment={7:F3}",
            Pid(drone), carrierId, source, available, current, minimum, timeToMinimum, alignment));
    // F8 uses the leader's native first target, then assigns exactly one eligible defender.
    internal void DesignateTarget(Aircraft local)
    {
        WeaponManager? weapons = local.weaponManager;
        List<Unit>? targets = weapons?.GetTargetList();
        Aircraft? candidate = targets != null && targets.Count > 0 ? targets[0] as Aircraft : null;
        if (candidate == null)
        {
            log("state=target_rejected reason=no_aircraft");
            reporter.NoEnemyAircraftSelected();
            return;
        }
        if (!CombatSupport.ValidHostileTrackedAircraft(local, candidate))
        {
            log("state=target_rejected reason=not_hostile");
            reporter.InvalidHostileSelection();
            return;
        }
        Candidate? best = null;
        foreach (KeyValuePair<Aircraft, Record> pair in records)
        {
            Aircraft defender = pair.Key;
            if (!DefenderOwns(defender, pair.Value) || !CombatSupport.ValidHostileTrackedAircraft(defender, candidate) ||
                !HasAvailableAam(defender))
                continue;
            Candidate next = new Candidate(defender, pair.Value, candidate);
            if (!best.HasValue || next.BetterThan(best.Value))
                best = next;
        }
        if (!best.HasValue)
            return;
        TryAssignAirToAir(best.Value.defender, candidate, out _);
    }
    internal bool SetRecordMode(Aircraft drone, DroneModeOverride value, out string reason)
    {
        if (!CommandRuntime(drone, out reason))
            return false;
        Record r = records[drone];
        r.modeOverride = value;
        ApplyDesired(drone, r, "set_mode");
        return true;
    }
    internal bool TryCruiseToPoint(Aircraft drone, GlobalPosition point, out string reason)
    {
        if (!CommandRuntime(drone, out reason))
            return false;
        Record r = records[drone];
        if (!Finite(point.x) || !Finite(point.y) || !Finite(point.z))
        {
            reason = "non_finite";
            return false;
        }
        r.hasLoiterPoint = true;
        r.loiterPoint = point;
        r.loiterAnchorRevision++;
        r.modeOverride = DroneModeOverride.Loiter;
        ApplyDesired(drone, r, "cruise_to_point");
        return true;
    }
    internal bool ClearLoiterHere(Aircraft drone, out string reason)
    {
        if (!CommandRuntime(drone, out reason))
            return false;
        Record r = records[drone];
        r.hasLoiterPoint = false;
        r.loiterAnchorRevision++;
        r.modeOverride = DroneModeOverride.Loiter;
        ApplyDesired(drone, r, "clear_loiter_here");
        return true;
    }
    internal bool TryGetLoiterHere(Aircraft drone, out GlobalPosition point)
    {
        point = default;
        if (!records.TryGetValue(drone, out Record r) || !r.hasLoiterPoint)
            return false;
        point = r.loiterPoint;
        return true;
    }
    internal bool TryGetMapCruisePhase(Aircraft drone, out bool cruising)
    {
        cruising = records.TryGetValue(drone, out Record r) && r.hasLoiterPoint &&
                   r.state is FixedWingDroneLoiterState loiter && loiter.CruisingToLoiterPoint;
        return cruising;
    }
    internal bool TryAssignAirToAir(Aircraft drone, Aircraft target, out string reason)
    {
        if (!CommandRuntime(drone, out reason))
            return false;
        StrikeWeaponStatus weapons = MissionWeaponStatus(drone, WingmanMission.AirToAir);
        if (weapons == StrikeWeaponStatus.Missing) { reason = "no_compatible_station"; return false; }
        if (weapons == StrikeWeaponStatus.Exhausted) { reason = "no_ammo"; return false; }
        if (!CombatSupport.ValidHostileTrackedAircraft(drone, target)) { reason = "invalid_target"; return false; }
        Record r = records[drone];
        ClearCapRecord(r); ClearAirToAirRecord(r); ClearAntiShipRecord(r); ClearSeadRecord(r); ClearCasRecord(r); ClearStrikeRecord(r);
        r.airToAirTarget = target;
        r.airToAirTargetId = target.persistentID.ToString();
        r.mission = WingmanMission.AirToAir;
        ApplyDesired(drone, r, "assign_air_to_air");
        return true;
    }
    internal bool TryAssignCap(Aircraft drone, GlobalPosition anchor, out string reason)
    {
        if (!CommandRuntime(drone, out reason)) return false;
        if (!context.CombatEnabled) { reason = "combat_disabled"; return false; }
        if (!Finite(anchor.x) || !Finite(anchor.y) || !Finite(anchor.z)) { reason = "non_finite"; return false; }
        if (!HasAvailableAam(drone)) { reason = "no_ammo"; return false; }
        Record r = records[drone];
        ClearAirToAirRecord(r); ClearAntiShipRecord(r); ClearSeadRecord(r); ClearCasRecord(r); ClearStrikeRecord(r); ClearCapRecord(r);
        r.hasCapAnchor = true; r.capAnchor = anchor; r.capRevision++; r.loiterAnchorRevision++; r.mission = WingmanMission.Cap;
        CombatLog(drone, "cap_assign");
        ApplyDesired(drone, r, "assign_cap");
        reason = ""; return true;
    }
    internal bool ClearCap(Aircraft drone, string reason)
    {
        if (!records.TryGetValue(drone, out Record r) || r.returningToBase || r.mission != WingmanMission.Cap) return false;
        ClearCapRecord(r);
        r.mission = WingmanMission.None;
        ApplyDesired(drone, r, reason);
        return true;
    }
    internal bool TryGetCapAnchor(Aircraft drone, out GlobalPosition anchor, out int revision)
    {
        anchor = default; revision = 0;
        if (!records.TryGetValue(drone, out Record r) || r.mission != WingmanMission.Cap || !r.hasCapAnchor) return false;
        anchor = r.capAnchor; revision = r.capRevision; return true;
    }
    internal bool TryGetAirToAirTarget(Aircraft drone, out Aircraft target)
    {
        target = null!;
        return records.TryGetValue(drone, out Record r) && r.airToAirTarget != null &&
               CombatSupport.ValidHostileTrackedAircraft(drone, r.airToAirTarget) && (target = r.airToAirTarget) != null;
    }
    internal bool ClearAirToAir(Aircraft drone, string reason)
    {
        if (!records.TryGetValue(drone, out Record r)) return false;
        ClearAirToAirRecord(r);
        if (r.mission == WingmanMission.AirToAir) { r.mission = WingmanMission.None; ApplyDesired(drone, r, reason); }
        return true;
    }
    internal bool InvalidateAirToAirTarget(Aircraft drone, Aircraft target, string reason)
    {
        if (!records.TryGetValue(drone, out Record r))
            return false;
        if (r.mission == WingmanMission.Cap && r.capTarget == target &&
            CombatCommandLogic.MatchesTarget(r.capTargetId, target.persistentID.ToString()))
        { ClearCapTarget(drone, r, reason); ApplyDesired(drone, r, reason); return true; }
        if (r.airToAirTarget != target || !CombatCommandLogic.MatchesTarget(r.airToAirTargetId, target.persistentID.ToString())) return false;
        ClearCapRecord(r); ClearAirToAirRecord(r);
        if (r.mission == WingmanMission.AirToAir)
        {
            r.mission = WingmanMission.None;
            ApplyDesired(drone, r, reason);
        }
        return true;
    }
    internal bool TryAssignSead(Aircraft drone, Unit target, out string reason)
    {
        if (!CommandRuntime(drone, out reason)) return false;
        if (!context.CombatEnabled) { reason = "combat_disabled"; return false; }
        if (!CombatSupport.ValidHostileSam(drone, target)) { reason = "invalid_target"; return false; }
        StrikeWeaponStatus weapons = MissionWeaponStatus(drone, WingmanMission.Sead);
        if (weapons == StrikeWeaponStatus.Missing) { reason = "no_compatible_station"; return false; }
        if (weapons == StrikeWeaponStatus.Exhausted) { reason = "no_ammo"; return false; }
        Record r = records[drone];
        ClearCapRecord(r); ClearAirToAirRecord(r);
        if (r.mission == WingmanMission.AntiShip) ClearAntiShipRecord(r);
        if (r.mission == WingmanMission.Cas) ClearCasRecord(r);
        if (r.mission == WingmanMission.Strike) ClearStrikeRecord(r);
        if (r.seadTarget != target)
        {
            ClearSeadRecord(r);
            r.seadTarget = target;
            r.seadTargetId = target.persistentID.ToString();
        }
        r.mission = WingmanMission.Sead;
        ApplyDesired(drone, r, "assign_Sead");
        reason = ""; return true;
    }
    internal bool TryGetSeadTarget(Aircraft drone, out Unit target)
    {
        target = null!;
        return records.TryGetValue(drone, out Record r) && r.seadTarget != null &&
               CombatSupport.ValidHostileSam(drone, r.seadTarget) && (target = r.seadTarget) != null;
    }
    internal bool ClearSead(Aircraft drone, string reason)
    {
        if (!records.TryGetValue(drone, out Record r)) return false;
        ClearSeadRecord(r);
        if (r.mission == WingmanMission.Sead) { r.mission = WingmanMission.None; ApplyDesired(drone, r, reason); }
        return true;
    }
    internal bool TryAssignAntiShip(Aircraft drone, Ship target, out string reason)
    {
        if (!CommandRuntime(drone, out reason)) return false;
        if (!context.CombatEnabled) { reason = "combat_disabled"; return false; }
        if (!CombatSupport.ValidHostileShip(drone, target)) { reason = "invalid_target"; return false; }
        StrikeWeaponStatus weapons = MissionWeaponStatus(drone, WingmanMission.AntiShip);
        if (weapons == StrikeWeaponStatus.Missing) { reason = "no_compatible_station"; return false; }
        if (weapons == StrikeWeaponStatus.Exhausted) { reason = "no_ammo"; return false; }
        Record r = records[drone];
        ClearCapRecord(r); ClearAirToAirRecord(r);
        if (r.mission == WingmanMission.Sead) ClearSeadRecord(r);
        if (r.mission == WingmanMission.Cas) ClearCasRecord(r);
        if (r.mission == WingmanMission.Strike) ClearStrikeRecord(r);
        if (r.antiShipTarget != target)
        {
            ClearAntiShipRecord(r);
            r.antiShipTarget = target;
            r.antiShipTargetId = target.persistentID.ToString();
        }
        if (r.mission != WingmanMission.AntiShip) r.mission = WingmanMission.AntiShip;
        ApplyDesired(drone, r, "assign_AntiShip");
        reason = "";
        return true;
    }
    internal bool TryGetAntiShipTarget(Aircraft drone, out Ship target)
    {
        target = null!;
        return records.TryGetValue(drone, out Record r) && r.antiShipTarget != null &&
               CombatSupport.ValidHostileShip(drone, r.antiShipTarget) && (target = r.antiShipTarget) != null;
    }
    internal bool ClearAntiShip(Aircraft drone, string reason)
    {
        if (!records.TryGetValue(drone, out Record r)) return false;
        ClearAntiShipRecord(r);
        if (r.mission == WingmanMission.AntiShip) { r.mission = WingmanMission.None; ApplyDesired(drone, r, reason); }
        return true;
    }
    internal bool TryAssignCas(Aircraft drone, GlobalPosition center, out string reason)
    {
        if (!CommandRuntime(drone, out reason)) return false;
        if (!context.CombatEnabled) { reason = "combat_disabled"; return false; }
        if (!Finite(center.x) || !Finite(center.y) || !Finite(center.z)) { reason = "non_finite"; return false; }
        StrikeWeaponStatus weapons = MissionWeaponStatus(drone, WingmanMission.Cas);
        if (weapons == StrikeWeaponStatus.Missing) { reason = "no_compatible_station"; return false; }
        if (weapons == StrikeWeaponStatus.Exhausted) { reason = "no_ammo"; return false; }
        Record r = records[drone];
        ClearCapRecord(r); ClearAirToAirRecord(r); ClearAntiShipRecord(r); ClearSeadRecord(r); ClearCasRecord(r);
        ClearStrikeRecord(r);
        r.hasCasCenter = true; r.casCenter = center;
        r.mission = WingmanMission.Cas;
        ApplyDesired(drone, r, "assign_Cas");
        reason = ""; return true;
    }
    internal bool ClearCas(Aircraft drone, string reason)
    {
        if (!records.TryGetValue(drone, out Record r)) return false;
        ClearCasRecord(r);
        if (r.mission == WingmanMission.Cas) { r.mission = WingmanMission.None; ApplyDesired(drone, r, reason); }
        return true;
    }
    internal bool TryGetCasCenter(Aircraft drone, out GlobalPosition center, out int revision, out float loiterPhase)
    {
        center = default; revision = 0; loiterPhase = 0f;
        if (!records.TryGetValue(drone, out Record r) || !r.hasCasCenter) return false;
        center = r.casCenter; revision = r.casCenterRevision; loiterPhase = r.loiterPhase;
        return true;
    }
    internal bool TryGetCasTarget(Aircraft drone, out Unit target)
    {
        target = null!;
        return records.TryGetValue(drone, out Record r) && r.casTarget != null &&
               CombatSupport.ValidHostileCasTarget(drone, r.casTarget) && (target = r.casTarget) != null;
    }
    internal bool TryAcquireCasTarget(Aircraft drone, out Unit target)
    {
        target = null!;
        if (!records.TryGetValue(drone, out Record r) || !r.hasCasCenter || drone.NetworkHQ == null) return false;
        if (r.casTarget != null && CombatSupport.ValidHostileCasTarget(drone, r.casTarget)) { target = r.casTarget; return true; }
        ClearCasTarget(r);
        float bestDistanceSq = float.PositiveInfinity; string? bestPid = null; Unit? best = null;
        foreach (KeyValuePair<PersistentID, TrackingInfo> pair in drone.NetworkHQ.trackingDatabase)
        {
            TrackingInfo tracking = pair.Value;
            if (!tracking.TryGetUnit(out Unit candidate) || !CombatSupport.ValidHostileCasTarget(drone, candidate)) continue;
            GlobalPosition known = tracking.lastKnownPosition;
            if (!Finite(known.x) || !Finite(known.z) ||
                !CasMissionLogic.IsWithinSearchRadius(r.casCenter.x, r.casCenter.z, known.x, known.z)) continue;
            float dx = known.x - r.casCenter.x, dz = known.z - r.casCenter.z, distanceSq = dx * dx + dz * dz;
            string pid = candidate.persistentID.ToString();
            if (best == null || CasMissionLogic.IsBetterCandidate(distanceSq, pid, bestDistanceSq, bestPid!))
            { best = candidate; bestPid = pid; bestDistanceSq = distanceSq; }
        }
        if (best == null) return false;
        r.casTarget = best; r.casTargetId = bestPid; target = best; return true;
    }
    internal bool CasOwns(Aircraft drone, FixedWingDroneCasState state, int centerRevision) =>
        records.TryGetValue(drone, out Record r) && !r.returningToBase && r.state == state &&
        r.mission == WingmanMission.Cas && r.hasCasCenter && r.casCenterRevision == centerRevision &&
        context.CombatEnabled && CombatSupport.Runtime(drone, state) && !context.HasPlayerAssociation(drone);
    internal bool InvalidateCasTarget(Aircraft drone, Unit target, string reason)
    {
        if (!records.TryGetValue(drone, out Record r) || r.casTarget != target) return false;
        ClearCasTarget(r);
        if (r.mission == WingmanMission.Cas) ApplyDesired(drone, r, reason);
        return true;
    }
    internal bool TryAssignStrike(Aircraft drone, IReadOnlyList<Unit> targets, out string reason)
    {
        if (!CommandRuntime(drone, out reason)) return false;
        if (!context.CombatEnabled) { reason = "combat_disabled"; return false; }
        if (targets == null || targets.Count == 0) { reason = "no_targets"; return false; }
        StrikeWeaponStatus weapons = MissionWeaponStatus(drone, WingmanMission.Strike);
        if (weapons == StrikeWeaponStatus.Missing) { reason = "no_compatible_station"; return false; }
        if (weapons == StrikeWeaponStatus.Exhausted) { reason = "no_ammo"; return false; }
        Dictionary<string, PersistentID> ids = new Dictionary<string, PersistentID>();
        foreach (Unit target in targets)
        {
            if (target == null || !CombatSupport.ValidHostileGround(drone, target)) { reason = "invalid_target"; return false; }
            ids[target.persistentID.ToString()] = target.persistentID;
        }
        string[] sorted = StrikeBombingLogic.CopySortDistinctIds(ids.Keys);
        PersistentID[] copied = new PersistentID[sorted.Length];
        for (int i = 0; i < sorted.Length; i++) copied[i] = ids[sorted[i]];
        Record r = records[drone];
        ClearCapRecord(r); ClearAirToAirRecord(r); ClearAntiShipRecord(r); ClearSeadRecord(r); ClearCasRecord(r); ClearStrikeRecord(r);
        r.strikeTargetIds = copied;
        r.mission = WingmanMission.Strike;
        ApplyDesired(drone, r, "assign_Strike");
        reason = ""; return true;
    }
    internal bool ClearStrike(Aircraft drone, string reason)
    {
        if (!records.TryGetValue(drone, out Record r)) return false;
        ClearStrikeRecord(r);
        if (r.mission == WingmanMission.Strike) { r.mission = WingmanMission.None; ApplyDesired(drone, r, reason); }
        return true;
    }
    // Revision-scoped progress belongs to the record, rather than a transient strike state, so defense can resume it.
    internal HashSet<PersistentID> GetStrikeReleasedTargetIds(Aircraft drone, int revision)
    {
        if (!records.TryGetValue(drone, out Record r) || r.strikeRevision != revision)
            return new HashSet<PersistentID>();
        return r.strikeReleasedTargetIds;
    }
    internal bool TryGetStrikeTarget(Aircraft drone, out Unit target)
    {
        target = null!;
        return records.TryGetValue(drone, out Record r) && r.strikeTarget != null &&
               StrikeContains(r, r.strikeTarget.persistentID) && CombatSupport.ValidHostileGround(drone, r.strikeTarget) &&
               (target = r.strikeTarget) != null;
    }
    internal bool TryGetStrikeTargetSet(Aircraft drone, out PersistentID[] targetIds)
    {
        targetIds = Array.Empty<PersistentID>();
        if (!records.TryGetValue(drone, out Record r)) return false;
        targetIds = (PersistentID[])r.strikeTargetIds.Clone();
        return true;
    }
    internal bool TryAcquireStrikeTarget(Aircraft drone, out Unit target)
    {
        return TryAcquireStrikeTarget(drone, null, out target);
    }
    internal bool TryAcquireStrikeTarget(Aircraft drone, IReadOnlyCollection<PersistentID>? excluded, out Unit target)
    {
        target = null!;
        if (!records.TryGetValue(drone, out Record r) || r.strikeTargetIds.Length == 0 || drone.NetworkHQ == null)
            return false;
        if (r.strikeTarget != null && !StrikeExcluded(excluded, r.strikeTarget.persistentID) && StrikeContains(r, r.strikeTarget.persistentID) &&
            CombatSupport.ValidHostileGround(drone, r.strikeTarget)) { target = r.strikeTarget; return true; }
        ClearStrikeTarget(r);
        GlobalPosition own = drone.GlobalPosition();
        if (!Finite(own.x) || !Finite(own.y) || !Finite(own.z)) return false;
        float bestDistanceSq = float.PositiveInfinity; string? bestPid = null; Unit? best = null;
        foreach (PersistentID id in r.strikeTargetIds)
        {
            if (StrikeExcluded(excluded, id)) continue;
            if (!drone.NetworkHQ.trackingDatabase.TryGetValue(id, out TrackingInfo tracking) ||
                !tracking.TryGetUnit(out Unit candidate) || candidate.persistentID != id ||
                !CombatSupport.ValidHostileGround(drone, candidate)) continue;
            GlobalPosition known = tracking.lastKnownPosition;
            if (!Finite(known.x) || !Finite(known.y) || !Finite(known.z)) continue;
            Vector3 delta = known - own;
            float distanceSq = delta.sqrMagnitude;
            string pid = candidate.persistentID.ToString();
            if (best == null || StrikeBombingLogic.IsBetterTarget(distanceSq, pid, bestDistanceSq, bestPid!))
            { best = candidate; bestPid = pid; bestDistanceSq = distanceSq; }
        }
        if (best == null) return false;
        r.strikeTarget = best; r.strikeTargetId = bestPid; target = best; return true;
    }
    private static bool StrikeExcluded(IReadOnlyCollection<PersistentID>? excluded, PersistentID id)
    {
        if (excluded == null) return false;
        foreach (PersistentID excludedId in excluded) if (excludedId == id) return true;
        return false;
    }
    internal bool StrikeOwns(Aircraft drone, FixedWingDroneStrikeState state, int revision) =>
        records.TryGetValue(drone, out Record r) && !r.returningToBase && r.state == state &&
        r.mission == WingmanMission.Strike && r.strikeTargetIds.Length > 0 && r.strikeRevision == revision &&
        context.CombatEnabled && CombatSupport.Runtime(drone, state) && !context.HasPlayerAssociation(drone);
    internal bool InvalidateStrikeTarget(Aircraft drone, Unit target, string reason)
    {
        if (!records.TryGetValue(drone, out Record r) || r.strikeTarget != target) return false;
        ClearStrikeTarget(r);
        if (r.mission == WingmanMission.Strike) ApplyDesired(drone, r, reason);
        return true;
    }
    internal bool ConsumeStrikeTarget(Aircraft drone, PersistentID targetId)
    {
        if (!records.TryGetValue(drone, out Record r) || r.mission != WingmanMission.Strike || r.returningToBase) return false;
        int found = -1;
        for (int i = 0; i < r.strikeTargetIds.Length; i++)
            if (r.strikeTargetIds[i] == targetId) { found = i; break; }
        if (found < 0) return true;
        PersistentID[] remaining = new PersistentID[r.strikeTargetIds.Length - 1];
        if (found > 0) Array.Copy(r.strikeTargetIds, remaining, found);
        if (found + 1 < r.strikeTargetIds.Length)
            Array.Copy(r.strikeTargetIds, found + 1, remaining, found, r.strikeTargetIds.Length - found - 1);
        r.strikeTargetIds = remaining;
        if (r.strikeTarget != null && r.strikeTarget.persistentID == targetId)
            ClearStrikeTarget(r);
        CombatLog(drone, "strike_target_consumed target_pid=" + targetId + " remaining=" + remaining.Length);
        return true;
    }
    internal bool StandDown(Aircraft drone, out string reason)
    {
        reason = "";
        if (!records.TryGetValue(drone, out Record r))
        {
            reason = "not_registered";
            return false;
        }
        if (r.returningToBase)
            return TryCancelReturnToBase(drone, out reason);
        ClearCapRecord(r); ClearAirToAirRecord(r);
        if (r.mission == WingmanMission.AntiShip) ClearAntiShipRecord(r);
        if (r.mission == WingmanMission.Sead) ClearSeadRecord(r);
        if (r.mission == WingmanMission.Cas) ClearCasRecord(r);
        if (r.mission == WingmanMission.Strike) ClearStrikeRecord(r);
        r.mission = WingmanMission.None;
        ApplyDesired(drone, r, "stand_down");
        return true;
    }
    private bool CommandRuntime(Aircraft drone, out string reason)
    {
        reason = "";
        if (!records.TryGetValue(drone, out Record r))
        {
            reason = "not_registered";
            return false;
        }
        if (r.returningToBase)
        {
            reason = "rtb_in_progress";
            return false;
        }
        if (!CombatSupport.ValidAircraft(drone) || !drone.IsServer || !drone.LocalSim)
        {
            reason = "runtime";
            return false;
        }
        if (context.HasPlayerAssociation(drone))
        {
            reason = "player_association";
            return false;
        }
        return true;
    }
    internal bool TryReturnToBase(Aircraft drone, out string reason)
    {
        reason = "";
        if (!records.TryGetValue(drone, out Record r))
        {
            reason = "not_registered";
            return false;
        }
        if (r.returningToBase)
        {
            reason = "rtb_in_progress";
            return false;
        }
        if (context.HasPlayerAssociation(drone))
        {
            reason = "player_association";
            return false;
        }
        if (!drone.IsServer || !drone.LocalSim)
        {
            reason = "runtime";
            return false;
        }
        if (!NativeRecoveryGuard.TryEvaluate(drone, out reason))
            return false;
        Pilot pilot = drone.pilots[0];
        if (!TryPreflightNativeLanding(drone, out reason))
            return false;
        if (pilot.pilotType != Pilot.PilotType.Plane || drone.autopilot is not AutopilotPlane)
        {
            reason = "native_landing_unavailable";
            return false;
        }
        PilotBaseState landing = pilot.AILandingState;
        if (landing == null)
        {
            reason = "native_landing_unavailable";
            return false;
        }
        Exception? handoffException = null;
        try
        {
            pilot.SwitchState(landing);
        }
        catch (Exception e)
        {
            handoffException = e;
            log("state=rtb_handoff_exception pid=" + Pid(drone) + " native=AIPilotLandingState exception=" +
                e.GetType().Name);
        }
        if (pilot.currentState != landing)
        {
            reason = handoffException == null ? "rtb_handoff_failed" : "rtb_handoff_exception";
            if (handoffException == null)
                log("state=rtb_handoff_failed pid=" + Pid(drone) + " native=AIPilotLandingState");
            return false;
        }
        if (handoffException != null)
            log("state=rtb_handoff_exception_installed pid=" + Pid(drone) + " native=AIPilotLandingState exception=" +
                handoffException.GetType().Name);
        ClearLaunchClearance(r);
        ClearCapRecord(r); ClearAirToAirRecord(r);
        if (r.mission == WingmanMission.AntiShip) ClearAntiShipRecord(r);
        if (r.mission == WingmanMission.Sead) ClearSeadRecord(r);
        if (r.mission == WingmanMission.Cas) ClearCasRecord(r);
        if (r.mission == WingmanMission.Strike) ClearStrikeRecord(r);
        r.returningToBase = true;
        r.mission = WingmanMission.None;
        r.antiShipRadarThreat = null; r.state = null;
        separation.Remove(drone);
        log("state=rtb_handoff pid=" + Pid(drone) + " native=AIPilotLandingState");
        r.lastRtbPilotState = StateName(pilot.currentState);
        log("state=rtb_handoff_observed pid=" + Pid(drone) + " current=" + r.lastRtbPilotState +
            " native=" + (pilot.currentState is AIPilotLandingState));
        return true;
    }
    // AIPilotLandingState ejects when its own RequestLanding query has no result.  Run the same query first.
    private static bool TryPreflightNativeLanding(Aircraft drone, out string reason)
    {
        reason = "native_landing_unavailable";
        try
        {
            if (drone.NetworkHQ == null || drone.weaponManager == null)
                return false;
            AircraftParameters parameters = drone.GetAircraftParameters();
            RunwayQuery query = new RunwayQuery
            {
                RunwayType = RunwayQueryType.Landing,
                MinSize = parameters.verticalLanding ? drone.definition.length : parameters.takeoffDistance,
                LandingSpeed = parameters.verticalLanding ? 0f :
                    Mathf.Sqrt(drone.GetMass() / drone.definition.aircraftInfo.maxWeight) * parameters.landingSpeed,
                TailHook = drone.weaponManager.HasTailHook()
            };
            Airbase? airbase = drone.NetworkHQ.GetNearestAirbase(drone.transform.position, query);
            Airbase.Runway.RunwayUsage? usage = airbase != null ? airbase.RequestLanding(drone, query) : null;
            if (!usage.HasValue)
            {
                reason = "native_landing_no_runway";
                return false;
            }
            reason = "";
            return true;
        }
        catch
        {
            reason = "native_landing_unavailable";
            return false;
        }
    }
    internal bool TryCancelReturnToBase(Aircraft drone, out string reason)
    {
        reason = "";
        if (!records.TryGetValue(drone, out Record r))
        {
            reason = "not_registered";
            return false;
        }
        if (!RtbCancellationLogic.CanCancel(r.returningToBase, drone.disabled, drone.HasEjected(),
                                            drone.unitState == Unit.UnitState.Abandoned,
                                            drone.unitState == Unit.UnitState.Returned, drone.radarAlt, out reason))
            return false;
        Pilot pilot;
        AIPilotLandingState landing;
        try
        {
            pilot = drone.pilots[0];
            landing = pilot.AILandingState;
        }
        catch
        {
            reason = "rtb_committed";
            return false;
        }
        if (!CanLeaveNativeLandingForCancel(drone, landing))
        {
            reason = "rtb_committed";
            return false;
        }
        r.returningToBase = false;
        ApplyDesired(drone, r, "rtb_cancel", allowNativeLanding: true);
        if (r.state == null || pilot.currentState != r.state || pilot.currentState == landing)
        {
            r.returningToBase = true;
            return false;
        }
        ClearCasRecord(r); ClearStrikeRecord(r); r.mission = WingmanMission.None;
        r.lastRtbPilotState = null;
        log("state=rtb_cancel_accepted pid=" + Pid(drone));
        ReleaseNativeLandingUsage(drone, landing, "rtb_cancel");
        return true;
    }
    // Cancellation is the sole intentional takeover of a native landing state; generic reconciliation never does this.
    private bool CanLeaveNativeLandingForCancel(Aircraft drone, AIPilotLandingState landing)
    {
        try
        {
            return context.CanReconcile() && !context.HasPlayerAssociation(drone) && drone.IsServer && drone.LocalSim &&
                   drone.pilots != null && drone.pilots.Length > 0 && drone.pilots[0] != null &&
                   drone.pilots[0].pilotType == Pilot.PilotType.Plane && drone.autopilot is AutopilotPlane &&
                   drone.pilots[0].currentState == landing;
        }
        catch { return false; }
    }
    internal void Unregister(Aircraft drone, string reason)
    {
        records.TryGetValue(drone, out Record? r);
        bool liveCustom = r != null && InstalledStateOwns(drone, r);
        if (liveCustom)
        {
            bool detachOnly = context.HasPlayerAssociation(drone) || !drone.IsServer || !drone.LocalSim ||
                              drone.disabled || drone.HasEjected() || drone.unitState == Unit.UnitState.Abandoned ||
                              drone.unitState == Unit.UnitState.Returned;
            if (detachOnly)
            {
                // We no longer own the aircraft's lifecycle: detach bookkeeping without changing its pilot state.
                CancelOnly(r!.state!);
                r.state = null;
            }
            else
            {
                bool handedOff = false;
                try { handedOff = context.CanFallback(drone) && SwitchFreshNativeIfSafe(drone, "unregister_" + reason); }
                catch (Exception e) { log("state=unregister_native_fallback_exception pid=" + Pid(drone) + " exception=" + e.GetType().Name); }
                if (!handedOff)
                {
                    // Keep every record field, including active clearance, until the live custom state can hand off.
                    log("state=unregister_retained pid=" + Pid(drone) + " reason=native_handoff_failed");
                    return;
                }
                r!.state = null;
            }
        }
        if (r != null)
            ClearLaunchClearance(r);
        try { UnbindMissionMissileHandlers(drone, r); }
        catch (Exception e) { log("state=unregister_unbind_exception pid=" + Pid(drone) + " exception=" + e.GetType().Name); }
        registered.Remove(drone);
        rejected.Remove(drone);
        separation.Remove(drone);
        records.Remove(drone);
        Log("follow_unregistered", drone, reason);
    }
    internal bool Suspend(Aircraft drone, string reason)
    {
        if (!records.TryGetValue(drone, out Record r))
            return true;
        if (r.returningToBase)
            return false;
        if (r.state == null)
        {
            ClearLaunchClearance(r);
            return true;
        }
        try
        {
            if (!context.CanFallback(drone) || drone.pilots == null || drone.pilots.Length == 0 ||
                drone.pilots[0] == null)
                return false;
            Pilot p = drone.pilots[0];
            if (p.currentState != r.state)
                return false;
            if (!SwitchFreshNativeIfSafe(drone, reason))
                return false;
            Disarm(drone);
            ClearLaunchClearance(r);
            r.antiShipRadarThreat = null; r.state = null;
            separation.Remove(drone);
            log("state=wingman_suspended pid=" + Pid(drone) + " reason=" + reason + " throttle=0");
            return true;
        }
        catch
        {
            return false;
        }
    }
    internal void Tick()
    {
        if (!context.CanReconcile())
            return;
        SampleLeaderAndSeparation();
        if (!context.CombatEnabled)
            foreach (KeyValuePair<Aircraft, Record> pair in new List<KeyValuePair<Aircraft, Record>>(records))
                if (!pair.Value.returningToBase && (pair.Value.state is FixedWingDroneAirToAirAttackState ||
                    pair.Value.state is FixedWingDroneSeadState || pair.Value.state is FixedWingDroneCasState ||
                    pair.Value.state is FixedWingDroneStrikeState ||
                    pair.Value.state is FixedWingDroneAntiShipAttackState ||
                    pair.Value.state is FixedWingDroneMissileDefenseState))
                    ApplyDesired(pair.Key, pair.Value, "combat_disabled");
        if (Time.realtimeSinceStartup >= nextMissileDefenseScan)
        {
            nextMissileDefenseScan = Time.realtimeSinceStartup + .1f;
            ScanMissileDefense();
        }
        if (Time.realtimeSinceStartup >= nextCombatScan)
        {
            nextCombatScan = Time.realtimeSinceStartup + .75f;
            ScanCombat();
        }
        if (Time.realtimeSinceStartup < nextReconcile)
            return;
        nextReconcile = Time.realtimeSinceStartup + .75f;
        Reconcile("interval");
    }
    internal void StopAll(string reason)
    {
        foreach (Aircraft d in new List<Aircraft>(registered))
        {
            if (!records.TryGetValue(d, out Record r))
                continue;
            ClearLaunchClearance(r);
            if (r.returningToBase)
                continue;
            ClearCapRecord(r); ClearAirToAirRecord(r); ClearAntiShipRecord(r); ClearSeadRecord(r); ClearCasRecord(r); ClearStrikeRecord(r); r.mission = WingmanMission.None;
            if (r.state == null)
                continue;
            PilotBaseState old = r.state;
            r.antiShipRadarThreat = null; r.state = null;
            CancelOnly(old);
            Disarm(d);
            SwitchFreshNativeIfSafe(d, reason);
        }
    }
    internal void Reconcile(string reason)
    {
        if (!context.CanReconcile())
            return;
        foreach (Aircraft d in new List<Aircraft>(registered))
        {
            try
            {
                if (d == null)
                {
                    records.TryGetValue(d!, out Record removed); UnbindMissionMissileHandlers(d!, removed);
                    registered.Remove(d!);
                    records.Remove(d!);
                    separation.Remove(d!);
                    continue;
                }
                if (!records.TryGetValue(d, out Record r))
                    continue;
                if (r.returningToBase)
                {
                    ObserveRtbPilotState(d, r);
                    continue;
                }
                if (r.state != null)
                {
                    ApplyDesired(d, r, reason);
                    continue;
                }
                if (!context.EligibleForInstall(d, out string reject))
                {
                    RejectOnce(d, reject);
                    continue;
                }
                if (d.pilots == null || d.pilots.Length == 0 || d.pilots[0] == null)
                {
                    RejectOnce(d, "pilot_missing");
                    continue;
                }
                d.flightAssist = true;
                d.GetInputs().brake = 0f;
                ApplyDesired(d, r, reason);
            }
            catch (Exception e)
            {
                Fallback(d, "install_" + e.GetType().Name);
            }
        }
    }
    internal bool TryGetGuidance(Aircraft d, PilotBaseState caller, out Aircraft leader, out string reason)
    {
        if (!RuntimeOwns(d, caller, out reason))
        {
            leader = null!;
            return false;
        }
        leader = activeLeader ?? null!;
        if (leader == null)
        {
            reason = "no_leader";
            return false;
        }
        bool eligible = context.RuntimeEligible(d, leader, out reason);
        if (!eligible && (reason == "leader" || reason.StartsWith("leader_", StringComparison.Ordinal)))
            reason = "leader_unavailable";
        return eligible;
    }
    internal bool TryGetSeparation(Aircraft d, PilotBaseState caller, out SeparationDirective directive)
    {
        directive = default;
        return RuntimeOwns(d, caller, out _) && separation.TryGetValue(d, out directive);
    }
    internal void Fallback(Aircraft d, string reason)
    {
        if (CancelState(d))
        {
            separation.Remove(d);
            if (SwitchFreshNativeIfSafe(d, reason))
                return;
            Log("follow_stopped", d, reason);
        }
    }
    // A formation controller can temporarily loiter without changing the group's AUTO mode or its override.
    // In particular, an explicit FOLLOW remains FOLLOW and resumes when the leader returns.
    internal void RequestLoiter(Aircraft d, PilotBaseState c, string reason)
    {
        if (!RuntimeOwns(d, c, out _) || !records.TryGetValue(d, out Record r))
            return;
        r.followFallbackActive = true;
        if (reason == "infeasible_min_throttle")
            r.followFallbackUntil = Mathf.Max(r.followFallbackUntil, Time.realtimeSinceStartup + 30f);
        Log("wingman_follow_fallback", d, reason);
        ApplyDesired(d, r, "follow_fallback_" + reason);
    }
    internal void LogAvoidanceEnter(Aircraft d, FormationSlot s, SeparationDirective x) =>
        log(string.Format(CultureInfo.InvariantCulture,
                          "state=wingman_avoidance_enter pid={0} slot={1} other={2} current={3:F1} cpa={4:F1} t={5:F2}",
                          Pid(d), s.ordinal, x.otherPid, x.currentDistance, x.cpaDistance, x.timeToCpa));
    internal void LogAvoidanceExit(Aircraft d, FormationSlot s, string other, float duration, float minSep,
                                   float minCpa) =>
        log(string.Format(
            CultureInfo.InvariantCulture,
            "state=wingman_avoidance_exit pid={0} slot={1} other={2} duration={3:F2} minSep={4:F1} minCpa={5:F1}",
            Pid(d), s.ordinal, other, duration, minSep, minCpa));
    internal void LogSeparationTelemetry(Aircraft d, bool available, SeparationDirective x, float minSep,
                                         float minCpa) =>
        log(string.Format(
            CultureInfo.InvariantCulture,
            "state=follow_separation pid={0} separation_available={1} nearest_pid={2} nearest_separation_m={3:F1} " +
            "cpa_separation_m={4:F1} time_to_cpa_s={5:F2} min_nearest_separation_m={6:F1} min_cpa_separation_m={7:F1}",
            Pid(d), available, available ? x.otherPid : "none", available ? x.currentDistance : -1f,
            available ? x.cpaDistance : -1f, available ? x.timeToCpa : -1f, minSep, minCpa));
    internal void LogFollowControl(Aircraft d, bool armed, float weight, float aimAlignment, float alongFraction,
                                   float aimGate, float alongGate, float distanceGate, float rangeGate, float turnGate,
                                   float effectiveMaxClosure) =>
        log(string.Format(CultureInfo.InvariantCulture,
                          "state=follow_control pid={0} catchup_armed={1} catchup_weight={2:F3} aim_alignment={3:F3} " +
                          "along_fraction={4:F3} aim_gate={5:F3} along_gate={6:F3} distance_gate={7:F3} " +
                          "range_gate={8:F3} turn_gate={9:F3} effective_max_closure_mps={10:F2}",
                          Pid(d), armed, weight, aimAlignment, alongFraction, aimGate, alongGate, distanceGate,
                          rangeGate, turnGate, effectiveMaxClosure));
    internal void LogFollowControl(Aircraft d, float distance, float desiredClosure, float leaderSpeed,
                                   float droneSpeedRaw, float droneSpeedFiltered, float desiredDroneSpeed,
                                   float speedError, float leaderAlong, float droneAlong, float diagnosticClosure,
                                   float lookahead, float speedLookahead, float aheadGuardLookahead,
                                   float aimAlongDistance, float crossTrackError, float formationRange, float maxCross,
                                   float minAim, float alignment, float minAlignment, float leaderTurn, float droneTurn,
                                   float maxLeaderTurn, float maxDroneTurn, float minDroneSpeed, float maxDroneSpeed,
                                   float p, float i, float ff, float raw, float throttle, float minThrottle,
                                   float maxThrottle, float saturation, bool frozen, float infeasible,
                                   FormationSlot slot, bool avoid, float weight, SeparationDirective nearest,
                                   float minSep, float minCpa)
    {
        log(string.Format(
            CultureInfo.InvariantCulture,
            "state=follow_control pid={0} distance_error_m={1:F2} desired_closure_mps={2:F2} " +
            "leader_horizontal_speed_mps={3:F2} drone_horizontal_speed_raw_mps={4:F2} " +
            "drone_horizontal_speed_filtered_mps={5:F2} desired_drone_speed_mps={6:F2} speed_error_mps={7:F2} " +
            "leader_along_mps={8:F2} drone_along_mps={9:F2} diagnosticClosure_mps={10:F2} lookahead_m={11:F2} " +
            "speed_lookahead_m={12:F2} ahead_guard_lookahead_m={13:F2} aim_along_distance_m={14:F2} " +
            "cross_track_error_m={15:F2} formation_horizontal_range_m={16:F2} max_abs_cross_track_error_m={17:F2} " +
            "min_aim_along_distance_m={18:F2} heading_alignment={19:F3} min_heading_alignment={20:F3} " +
            "leader_turn_rate_degps={21:F2} drone_turn_rate_degps={22:F2} max_abs_leader_turn_rate={23:F2} " +
            "max_abs_drone_turn_rate={24:F2} min_drone_horizontal_speed={25:F2} max_drone_horizontal_speed={26:F2} " +
            "p={27:F3} i={28:F3} feed_forward={29:F2} raw_throttle={30:F3} throttle={31:F3} min_throttle={32:F3} " +
            "max_throttle={33:F3} saturation_pct={34:F2} integration_frozen={35} infeasible_seconds={36:F2} " +
            "slot={37} slot_back_m={38:F0} slot_lateral_m={39:F0} slot_up_m={40:F0} avoidance_active={41} " +
            "avoidance_weight={42:F2} nearest_pid={43} nearest_separation_m={44:F1} cpa_separation_m={45:F1} " +
            "time_to_cpa_s={46:F2} min_nearest_separation_m={47:F1} min_cpa_separation_m={48:F1}",
            Pid(d), distance, desiredClosure, leaderSpeed, droneSpeedRaw, droneSpeedFiltered, desiredDroneSpeed,
            speedError, leaderAlong, droneAlong, diagnosticClosure, lookahead, speedLookahead, aheadGuardLookahead,
            aimAlongDistance, crossTrackError, formationRange, maxCross, minAim, alignment, minAlignment, leaderTurn,
            droneTurn, maxLeaderTurn, maxDroneTurn, minDroneSpeed, maxDroneSpeed, p, i, ff, raw, throttle, minThrottle,
            maxThrottle, saturation, frozen, infeasible, slot.ordinal, slot.back, slot.lateral, slot.up, avoid, weight,
            nearest.otherPid ?? "none", nearest.currentDistance, nearest.cpaDistance, nearest.timeToCpa, minSep,
            minCpa));
    }
    internal void Cleanup()
    {
        // Reuse Unregister's live-state handoff invariant.  A failed handoff leaves its record in place for retry.
        foreach (Aircraft drone in new List<Aircraft>(records.Keys))
            Unregister(drone, "cleanup");
        if (records.Count != 0)
            return;
        registered.Clear();
        rejected.Clear();
        separation.Clear();
        hullEnvelopeLogged.Clear();
        activeLeader = null;
        leaderAvailable = false;
        hasAnchor = hasRawLeader = false;
        anchor = rawLeader = default;
        anchorVelocity = Vector3.zero;
        anchorRadarAlt = relativeUp = 700f;
        mode = WingmanMode.Loiter;
        modeInitialized = false;
        candidateSince = -1f;
        lastSampleTime = 0f;
        epoch = epochTime = 0f;
        jumpLatched = false;
        nextSample = nextReconcile = nextCombatScan = 0f;
    }
    private bool CancelState(Aircraft d)
    {
        if (!records.TryGetValue(d, out Record r) || r.returningToBase || r.state == null)
            return false;
        PilotBaseState state = r.state;
        try
        {
            if (d == null || d.pilots == null || d.pilots.Length == 0 || d.pilots[0] == null ||
                d.pilots[0].currentState != state)
                return false;
            r.antiShipRadarThreat = null; r.state = null;
            ClearLaunchClearance(r);
            CancelOnly(state);
            Disarm(d);
            return true;
        }
        catch
        {
            return false;
        }
    }
    private bool SwitchFreshNativeIfSafe(Aircraft d, string reason)
    {
        if (d == null || d.pilots == null || d.pilots.Length == 0 || d.pilots[0] == null)
            return false;
        Pilot p = d.pilots[0];
        AIPilotCombatModes fresh = new AIPilotCombatModes(d);
        try
        {
            p.AICombatState = fresh;
            p.SwitchState(fresh);
        }
        catch
        {
        }
        if (p.currentState != fresh)
            return false;
        Log("wingman_native_fallback", d, reason);
        reporter.NativeFallback(d);
        return true;
    }
    private void SampleLeaderAndSeparation()
    {
        if (Time.realtimeSinceStartup < nextSample)
            return;
        nextSample = Time.realtimeSinceStartup + .2f;
        if (context.TryGetLeader(out Aircraft leader))
        {
            leaderAvailable = true;
            if (activeLeader != leader)
            {
                activeLeader = leader;
                leaderAvailable = true;
                candidateSince = -1f;
                hasRawLeader = hasAnchor = false;
                Log("active_leader_changed", leader, "current");
                reporter.LeaderChanged(leader);
            }
            GlobalPosition desired = leader.transform.position.ToGlobalPosition();
            float now = Time.realtimeSinceStartup,
                  dt = lastSampleTime <= 0f ? .2f : Mathf.Max(.01f, now - lastSampleTime);
            lastSampleTime = now;
            Vector3 delta = hasRawLeader ? desired - rawLeader : Vector3.zero;
            if (hasRawLeader && delta.magnitude > Mathf.Max(5000f, leader.rb.velocity.magnitude * dt + 1000f))
            {
                if (!jumpLatched)
                    Log("wingman_leader_jump", leader, "accepted");
                rawLeader = desired;
                anchor = desired;
                anchorVelocity = Vector3.ProjectOnPlane(leader.rb.velocity, Vector3.up);
                hasRawLeader = hasAnchor = true;
                jumpLatched = true;
            }
            rawLeader = desired;
            hasRawLeader = true;
            jumpLatched = false;
            if (!hasAnchor)
            {
                anchor = desired;
                anchorVelocity = Vector3.ProjectOnPlane(leader.rb.velocity, Vector3.up);
                hasAnchor = true;
            }
            else
            {
                GlobalPosition old = anchor;
                anchor = anchor + Vector3.ClampMagnitude(desired - anchor, 150f * dt);
                anchorVelocity = (anchor - old) / dt;
            }
            if (float.IsFinite(leader.radarAlt))
            {
                relativeUp = 300f;
                anchorRadarAlt = leader.radarAlt + 300f;
            }
            float speed = Vector3.ProjectOnPlane(leader.rb.velocity, Vector3.up).magnitude;
            WingmanMode wanted = speed >= 120f ? WingmanMode.Follow
                              : speed <= 90f ? WingmanMode.Loiter
                                              : (modeInitialized ? mode : WingmanMode.Loiter);
            if (!modeInitialized)
            {
                mode = wanted;
                modeInitialized = true;
                epoch = 0f;
                epochTime = Time.fixedTime;
                candidateSince = -1f;
                Log("wingman_mode_changed", leader, "mode=" + mode);
            }
            else if (wanted != mode)
            {
                if (candidateSince < 0f)
                {
                    candidateSince = Time.realtimeSinceStartup;
                    Log("wingman_mode_candidate", leader, "mode=" + wanted);
                }
                else if (Time.realtimeSinceStartup - candidateSince >= 2f)
                    SetMode(wanted);
            }
            else
                candidateSince = -1f;
        }
        else if (hasAnchor)
        {
            leaderAvailable = false;
            reporter.LeaderMissing();
            // Keep the last leader reference so recovery of the same owner preserves the anchor and epoch.
            // AUTO aircraft still use the normal mode hysteresis; explicit FOLLOW falls back only for that record.
            SetMode(WingmanMode.Loiter);
            foreach (KeyValuePair<Aircraft, Record> pair in records)
                if (pair.Value.modeOverride == DroneModeOverride.Follow && !pair.Value.followFallbackActive)
                {
                    pair.Value.followFallbackActive = true;
                    ApplyDesired(pair.Key, pair.Value, "leader_missing");
                }
        }
        if (leaderAvailable && activeLeader != null)
            foreach (KeyValuePair<Aircraft, Record> pair in records)
                if (pair.Value.followFallbackActive && Time.realtimeSinceStartup >= pair.Value.followFallbackUntil &&
                    context.RuntimeEligible(pair.Key, activeLeader, out _))
                {
                    pair.Value.followFallbackActive = false;
                    ApplyDesired(pair.Key, pair.Value, "leader_recovered");
                }
        BuildSeparation();
    }
    private void BuildSeparation()
    {
        separation.Clear();
        List<Kinematics> all = new List<Kinematics>();
        LeaderHullEnvelope envelope = default;
        if (leaderAvailable && activeLeader != null)
            envelope = BuildLeaderHullEnvelope(activeLeader);
        if (leaderAvailable && activeLeader != null && TryKinematics(activeLeader, -1, true, out Kinematics lead))
            all.Add(lead);
        foreach (KeyValuePair<Aircraft, Record> pair in records)
            if (!pair.Value.returningToBase && !context.HasPlayerAssociation(pair.Key) &&
                TryKinematics(pair.Key, pair.Value.slotOrdinal, false, out Kinematics k))
                all.Add(k);
        for (int i = 0; i < all.Count; i++)
            for (int j = i + 1; j < all.Count; j++)
            {
                try
                {
                    Kinematics a = all[i], b = all[j];
                    float t, current, cpa;
                    if (envelope.available && a.leader != b.leader &&
                        HullCpaLogic.TryPointVsTranslatingAabb(ToNumerics(a.leader ? b.position : a.position),
                            ToNumerics(a.leader ? b.velocity : a.velocity), ToNumerics(envelope.center),
                            ToNumerics(a.leader ? a.velocity : b.velocity), ToNumerics(envelope.extents), 6f,
                            out current, out cpa, out t))
                    {
                    }
                    else
                    {
                        Vector3 rel = a.position - b.position, vel = a.velocity - b.velocity;
                        float speedSq = vel.sqrMagnitude;
                        if (!Finite(speedSq) || speedSq < 0f)
                            continue;
                        t = Mathf.Clamp(speedSq > .001f ? -Vector3.Dot(rel, vel) / speedSq : 0f, 0f, 6f);
                        current = rel.magnitude;
                        cpa = (rel + vel * t).magnitude;
                    }
                    if (!Finite(t) || !Finite(current) || !Finite(cpa))
                        continue;
                    bool aYield = a.leader ? false : b.leader ? true : ShouldYield(a, b);
                    if (!a.leader)
                        PutDirective(a.aircraft, new SeparationDirective(aYield, b.pid, current, cpa, t));
                    if (!b.leader)
                        PutDirective(b.aircraft, new SeparationDirective(!aYield, a.pid, current, cpa, t));
                }
                catch
                {
                }
            }
    }
    private void PutDirective(Aircraft d, SeparationDirective next)
    {
        if (!next.shouldYield)
            return;
        if (!separation.TryGetValue(d, out SeparationDirective old) || MoreUrgent(next, old))
            separation[d] = next;
    }
    private static bool MoreUrgent(SeparationDirective next, SeparationDirective old)
    {
        bool nDanger = HullCpaLogic.LaunchClearanceRequired(true, next.currentDistance, next.cpaDistance),
             oDanger = HullCpaLogic.LaunchClearanceRequired(true, old.currentDistance, old.cpaDistance);
        if (nDanger != oDanger)
            return nDanger;
        if (next.cpaDistance != old.cpaDistance)
            return next.cpaDistance < old.cpaDistance;
        if (next.currentDistance != old.currentDistance)
            return next.currentDistance < old.currentDistance;
        return string.CompareOrdinal(next.otherPid, old.otherPid) < 0;
    }
    private static bool ShouldYield(Kinematics a, Kinematics b) => a.slotOrdinal != b.slotOrdinal
                                                                       ? a.slotOrdinal > b.slotOrdinal
                                                                       : string.CompareOrdinal(a.pid, b.pid) > 0;
    private LeaderHullEnvelope BuildLeaderHullEnvelope(Aircraft leader)
    {
        int accepted = 0, trigger = 0, body = 0, disabledInactive = 0, invalidBounds = 0;
        bool available = false;
        Vector3 min = default, max = default;
        foreach (Collider collider in leader.GetComponentsInChildren<Collider>(true))
        {
            if (collider == null) { body++; continue; }
            if (!collider.enabled || !collider.gameObject.activeInHierarchy) { disabledInactive++; continue; }
            if (collider.isTrigger) { trigger++; continue; }
            if (collider.attachedRigidbody != leader.rb) { body++; continue; }
            Bounds bounds = collider.bounds;
            if (!Finite(bounds.center) || !Finite(bounds.extents) || bounds.extents.x < 0f || bounds.extents.y < 0f || bounds.extents.z < 0f)
            { invalidBounds++; continue; }
            if (!available) { min = bounds.min; max = bounds.max; available = true; }
            else { min = Vector3.Min(min, bounds.min); max = Vector3.Max(max, bounds.max); }
            accepted++;
        }
        Vector3 center = available ? (min + max) * .5f : default, extents = available ? (max - min) * .5f : default;
        if (hullEnvelopeLogged.Add(leader))
            log("state=leader_hull_envelope pid=" + Pid(leader) + " accepted=" + accepted + " trigger=" + trigger +
                " body=" + body + " disabled_inactive=" + disabledInactive + " invalid_bounds=" + invalidBounds +
                " center=" + center + " extents=" + extents + " fallback=" + !available);
        return new LeaderHullEnvelope(available, center, extents);
    }
    private bool TryMeasureLaunchSeparation(Aircraft drone, Aircraft carrier, PersistentID carrierId,
                                             out float currentClearance, out float minimumClearance,
                                             out float timeToMinimum, out string source)
    {
        currentClearance = minimumClearance = timeToMinimum = -1f;
        source = "unavailable";
        try
        {
            if (carrier == null || carrier.persistentID != carrierId ||
                !TryKinematics(drone, 0, false, out Kinematics point) ||
                !TryKinematics(carrier, -1, true, out Kinematics box))
                return false;
            LeaderHullEnvelope envelope = BuildLeaderHullEnvelope(carrier);
            if (envelope.available && HullCpaLogic.TryPointVsTranslatingAabb(
                    ToNumerics(point.position), ToNumerics(point.velocity), ToNumerics(envelope.center),
                    ToNumerics(box.velocity), ToNumerics(envelope.extents), 6f, out currentClearance,
                    out minimumClearance, out timeToMinimum))
            {
                source = "hull";
                return true;
            }
            Vector3 relative = point.position - box.position, velocity = point.velocity - box.velocity;
            float speedSquared = velocity.sqrMagnitude;
            if (!Finite(speedSquared) || speedSquared < 0f)
                return false;
            timeToMinimum = Mathf.Clamp(speedSquared > .001f
                ? -Vector3.Dot(relative, velocity) / speedSquared
                : 0f, 0f, 6f);
            currentClearance = relative.magnitude;
            minimumClearance = (relative + velocity * timeToMinimum).magnitude;
            if (!Finite(currentClearance) || !Finite(minimumClearance) || !Finite(timeToMinimum))
                return false;
            source = "point";
            return true;
        }
        catch
        {
            return false;
        }
    }
    private static System.Numerics.Vector3 ToNumerics(Vector3 value) => new System.Numerics.Vector3(value.x, value.y, value.z);
    private static bool TryKinematics(Aircraft a, int slot, bool leader, out Kinematics k)
    {
        k = default;
        try
        {
            if (a == null || a.disabled || a.HasEjected() || a.unitState == Unit.UnitState.Abandoned ||
                a.unitState == Unit.UnitState.Returned || !a.gameObject.activeInHierarchy || a.Identity == null ||
                !a.Identity.IsSpawned || a.rb == null)
                return false;
            Vector3 p = a.transform.position, v = a.rb.velocity;
            if (!Finite(p) || !Finite(v))
                return false;
            k = new Kinematics(a, slot, leader, Pid(a), p, v);
            return true;
        }
        catch
        {
            return false;
        }
    }
    private void SetMode(WingmanMode next)
    {
        if (modeInitialized && mode == next)
            return;
        mode = next;
        modeInitialized = true;
        candidateSince = -1f;
        Log("wingman_mode_changed", activeLeader ?? null!, "mode=" + mode);
        reporter.ModeChanged(mode);
        foreach (KeyValuePair<Aircraft, Record> pair in new List<KeyValuePair<Aircraft, Record>>(records))
            ApplyDesired(pair.Key, pair.Value, "mode_change");
    }
    private void ApplyDesired(Aircraft d, Record r, string reason, bool allowNativeLanding = false)
    {
        if (r.returningToBase || d == null)
            return;
        if (context.HasPlayerAssociation(d))
        {
            ClearLaunchClearance(r);
            return;
        }
        if (r.state != null && !InstalledStateOwns(d, r))
        {
            // Detach our stale state.  Admission below decides whether the current native state is safe to reclaim.
            PilotBaseState displaced = r.state;
            r.state = null;
            ClearLaunchClearance(r);
            CancelOnly(displaced);
            Log("wingman_external_state", d, "current=" + CurrentPilotStateName(d));
        }
        if (r.state == null && !context.EligibleForInstall(d, out _) &&
            !(allowNativeLanding && d.pilots != null && d.pilots.Length > 0 && d.pilots[0] != null &&
              d.pilots[0].currentState is AIPilotLandingState))
            return;
        if (r.launchClearanceActive)
        {
            if (r.state is FixedWingDroneLaunchClearanceState clearance && d.pilots != null &&
                d.pilots.Length > 0 && d.pilots[0] != null && d.pilots[0].currentState == clearance)
                return;
            if (r.launchCarrier != null)
                SwitchTo(d, r, new FixedWingDroneLaunchClearanceState(this, d, r.launchCarrier,
                         r.launchCarrierId, r.egressDirection), reason);
            return;
        }
        if (context.CombatEnabled && HasEffectiveDefense(d, r, out _))
            return;
        if (r.mission == WingmanMission.Cap)
        {
            if (r.capTarget != null && !CapTargetValid(d, r, r.capTarget))
                ClearCapTarget(d, r, "target_invalid");
            if (CapMissionLogic.ResolveDesired(context.CombatEnabled, r.capTarget != null) == WingmanDesired.AirToAir)
            {
                Aircraft capTarget = r.capTarget!;
                if (r.state is FixedWingDroneAirToAirAttackState current && current.Target == capTarget) return;
                SwitchTo(d, r, new FixedWingDroneAirToAirAttackState(this, d, capTarget), reason);
                return;
            }
            if (r.state is FixedWingDroneLoiterState) return;
            SwitchTo(d, r, new FixedWingDroneLoiterState(this, d, r.loiterPhase), reason);
            return;
        }
        bool targetReady = r.mission == WingmanMission.None || r.mission == WingmanMission.AirToAir
                               ? r.mission == WingmanMission.None || r.airToAirTarget != null && CombatSupport.ValidHostileTrackedAircraft(d, r.airToAirTarget)
                               : r.mission == WingmanMission.Sead
                                   ? r.seadTarget != null && CombatSupport.ValidHostileSam(d, r.seadTarget)
                                   : r.mission == WingmanMission.AntiShip
                                       ? r.antiShipTarget != null && CombatSupport.ValidHostileShip(d, r.antiShipTarget)
                                       : r.mission == WingmanMission.Cas ? r.hasCasCenter : r.strikeTargetIds.Length > 0;
        if (context.CombatEnabled && r.mission != WingmanMission.None && !targetReady)
        {
            if (r.mission == WingmanMission.AntiShip) ClearAntiShip(d, "target_invalid");
            else if (r.mission == WingmanMission.Sead) ClearSead(d, "target_invalid");
            else if (r.mission == WingmanMission.Cas) ClearCas(d, "center_invalid");
            else if (r.mission == WingmanMission.Strike) ClearStrike(d, "target_set_invalid");
            else ClearAirToAir(d, "target_invalid");
            return;
        }
        WingmanDesired desired = CombatCommandLogic.ResolveDesired(context.CombatEnabled, r.mission,
                                                                    targetReady,
                                                                    r.modeOverride, mode == WingmanMode.Follow);
        desired = CombatCommandLogic.ResolveFollowFallback(desired, r.followFallbackActive);
        if (desired == WingmanDesired.AirToAir)
        {
            Aircraft target = r.airToAirTarget!;
            if (r.state is FixedWingDroneAirToAirAttackState a2a && a2a.Target == target)
                return;
            SwitchTo(d, r, new FixedWingDroneAirToAirAttackState(this, d, target), reason);
            return;
        }
        if (desired == WingmanDesired.Sead)
        {
            Unit target = r.seadTarget!;
            if (r.state is FixedWingDroneSeadState sead && sead.Target == target)
                return;
            SwitchTo(d, r, new FixedWingDroneSeadState(this, d, target), reason);
            return;
        }
        if (desired == WingmanDesired.AntiShip)
        {
            Ship target = r.antiShipTarget!;
            if (r.state is FixedWingDroneAntiShipAttackState antiShip && antiShip.Target == target)
                return;
            SwitchTo(d, r, new FixedWingDroneAntiShipAttackState(this, d, target), reason);
            return;
        }
        if (desired == WingmanDesired.Cas)
        {
            if (r.state is FixedWingDroneCasState cas && CasOwns(d, cas, r.casCenterRevision)) return;
            SwitchTo(d, r, new FixedWingDroneCasState(this, d, r.casCenterRevision), reason);
            return;
        }
        if (desired == WingmanDesired.Strike)
        {
            if (r.state is FixedWingDroneStrikeState strike && StrikeOwns(d, strike, r.strikeRevision)) return;
            SwitchTo(d, r, new FixedWingDroneStrikeState(this, d, r.strikeRevision), reason);
            return;
        }
        if (desired == WingmanDesired.Follow)
        {
            if (r.state is FixedWingDroneFollowState)
                return;
            SwitchTo(d, r, new FixedWingDroneFollowState(this, d, r.slot), reason);
            return;
        }
        if (r.state is FixedWingDroneLoiterState)
            return;
        SwitchTo(d, r, new FixedWingDroneLoiterState(this, d, r.loiterPhase), reason);
    }
    private void ObserveRtbPilotState(Aircraft drone, Record r)
    {
        try
        {
            PilotBaseState? state = drone.pilots[0].currentState;
            string current = StateName(state);
            if (current != r.lastRtbPilotState)
            {
                log("state=rtb_pilot_state pid=" + Pid(drone) + " from=" +
                    (r.lastRtbPilotState ?? "none") + " to=" + current);
                r.lastRtbPilotState = current;
            }
            // Landing may pass through taxi/completion states.  Only a player takeover or native combat proves
            // that landing released ownership; other native states remain RTB-owned and are merely observed.
            if ((context.HasPlayerAssociation(drone) || state is AIPilotCombatModes) && !drone.HasEjected() &&
                !drone.disabled && drone.unitState != Unit.UnitState.Abandoned && drone.unitState != Unit.UnitState.Returned)
            {
                ReleaseNativeLandingUsage(drone, drone.pilots[0].AILandingState, "rtb_native_exit");
                r.returningToBase = false;
                r.lastRtbPilotState = null;
                log("state=rtb_native_exit pid=" + Pid(drone) + " current=" + current);
            }
        }
        catch
        {
        }
    }
    internal void ResumeDesired(Aircraft d, PilotBaseState caller, string reason)
    {
        if (!records.TryGetValue(d, out Record r) || r.returningToBase || r.state != caller)
            return;
        if (r.mission == WingmanMission.Cap && r.capTarget == null)
            CombatLog(d, "cap_resume_patrol");
        ApplyDesired(d, r, "resume_" + reason);
    }
    internal bool CompleteMission(Aircraft drone, WingmanMission expectedMission, string reason)
    {
        if (!records.TryGetValue(drone, out Record r) || r.returningToBase || r.mission != expectedMission)
            return false;
        ClearAirToAirRecord(r);
        if (expectedMission == WingmanMission.AntiShip) ClearAntiShipRecord(r);
        if (expectedMission == WingmanMission.Sead) ClearSeadRecord(r);
        if (expectedMission == WingmanMission.Cas) ClearCasRecord(r);
        if (expectedMission == WingmanMission.Strike) ClearStrikeRecord(r);
        r.mission = WingmanMission.None;
        ApplyDesired(drone, r, reason);
        return true;
    }
    private static bool InstalledStateOwns(Aircraft d, Record r) =>
        d != null && r.state != null && d.pilots != null && d.pilots.Length > 0 && d.pilots[0] != null &&
        d.pilots[0].currentState == r.state;
    private static string CurrentPilotStateName(Aircraft d) =>
        d.pilots != null && d.pilots.Length > 0 && d.pilots[0] != null ? StateName(d.pilots[0].currentState) : "missing";
    private bool SwitchTo(Aircraft d, Record r, PilotBaseState next, string reason)
    {
        if (r.returningToBase || r.launchClearanceActive && next is not FixedWingDroneLaunchClearanceState)
            return false;
        if (d.pilots == null || d.pilots.Length == 0 || d.pilots[0] == null)
            return false;
        Pilot p = d.pilots[0];
        PilotBaseState? old = r.state;
        Exception? switchException = null;
        try
        {
            p.SwitchState(next);
        }
        catch (Exception e)
        {
            switchException = e;
            log("state=wingman_switch_exception pid=" + Pid(d) + " requested=" + StateName(next) + " exception=" +
                e.GetType().Name);
        }
        if (p.currentState != next)
            return false;
        if (switchException != null)
            log("state=wingman_switch_exception_installed pid=" + Pid(d) + " requested=" + StateName(next) +
                " exception=" + switchException.GetType().Name);
        r.state = next;
        rejected.Remove(d);
        if (next is FixedWingDroneLaunchClearanceState)
            return true;
        if (next is FixedWingDroneAirToAirAttackState attack)
        {
            reporter.CombatEntered(d, attack.Target);
            return true;
        }
        if (next is FixedWingDroneMissileDefenseState)
        {
            if (old is FixedWingDroneMissileDefenseState)
                reporter.MissileDefenseReplaced(d);
            else
                reporter.MissileDefenseEntered(d);
            return true;
        }
        if (old is FixedWingDroneMissileDefenseState)
            reporter.MissileDefenseExited(d);
        if (next is FixedWingDroneSeadState sead)
        {
            reporter.MissionEntered(d, WingmanMission.Sead, sead.Target);
            return true;
        }
        if (next is FixedWingDroneAntiShipAttackState antiShip)
        {
            reporter.MissionEntered(d, WingmanMission.AntiShip, antiShip.Target);
            return true;
        }
        if (next is FixedWingDroneCasState)
            return true;
        if (next is FixedWingDroneStrikeState)
            return true;
        if (old is FixedWingDroneAirToAirAttackState || old is FixedWingDroneSeadState ||
            old is FixedWingDroneAntiShipAttackState || old is FixedWingDroneCasState || old is FixedWingDroneStrikeState)
            reporter.Rejoined(d);
        WingmanMode actual = next is FixedWingDroneFollowState ? WingmanMode.Follow : WingmanMode.Loiter;
        Log("wingman_state_started", d, "mode=" + actual + " " + reason);
        if (!r.initialStateReported)
        {
            reporter.DroneStateStarted(d, actual);
            r.initialStateReported = true;
        }
        return true;
    }
    // Native landing tracks both a runway reservation and Airbase.ControlledAircraft usage.
    private void ReleaseNativeLandingUsage(Aircraft drone, AIPilotLandingState landing, string source)
    {
        try
        {
            landing.runwayUsage.Runway.DeregisterLanding(drone);
            landing.runwayUsage.Runway.airbase.RpcRegisterUsage(drone, false, landing.runwayUsage.Runway.index);
        }
        catch (Exception e)
        {
            log("state=" + source + "_release_exception pid=" + Pid(drone) + " exception=" + e.GetType().Name);
        }
    }
    internal bool PrepareReturningFqForTakeControl(Aircraft drone, out string reason)
    {
        reason = "";
        if (!records.TryGetValue(drone, out Record r) || !r.returningToBase)
        {
            reason = "rtb_in_progress";
            return false;
        }
        ReleaseNativeLandingUsage(drone, drone.pilots[0].AILandingState, "rtb_takeover");
        r.returningToBase = false;
        r.lastRtbPilotState = null;
        return true;
    }
    private bool HasEffectiveDefense(Aircraft d, Record r, out FixedWingDroneMissileDefenseState? defense)
    {
        defense = r.state as FixedWingDroneMissileDefenseState;
        return defense != null && TacticalOwns(d, defense) && ActiveDefenseThreat(d, defense.Threat);
    }
    internal bool TryGetLoiter(Aircraft d, PilotBaseState c, out GlobalPosition value, out Vector3 velocity,
                               out float up, out float altitude, out float sharedEpoch, out float sharedEpochTime,
                               out bool fixedPoint, out int revision, out string reason)
    {
        value = anchor;
        velocity = anchorVelocity;
        up = relativeUp;
        altitude = anchorRadarAlt;
        sharedEpoch = epoch;
        sharedEpochTime = epochTime;
        fixedPoint = false;
        revision = 0;
        if (!RuntimeOwns(d, c, out reason))
            return false;
        if (!hasAnchor)
        {
            reason = "no_anchor";
            return false;
        }
        Record r = records[d];
        fixedPoint = r.mission == WingmanMission.Cap && r.hasCapAnchor || r.hasLoiterPoint;
        // CAP and CRUISE own distinct counters; this shared generation cannot collide across their handoff.
        revision = r.loiterAnchorRevision;
        if (fixedPoint)
        {
            GlobalPosition point = r.mission == WingmanMission.Cap && r.hasCapAnchor ? r.capAnchor : r.loiterPoint;
            value.x = point.x;
            value.z = point.z;
            velocity = Vector3.zero;
        }
        return true;
    }
    internal void Unavailable(Aircraft d, PilotBaseState c, string reason)
    {
        if (reason != "no_anchor")
            DetachOrFallback(d, c, reason);
    }
    internal void Fault(Aircraft d, PilotBaseState c, string reason) => DetachOrFallback(d, c, "fault_" + reason);
    private void DetachOrFallback(Aircraft d, PilotBaseState c, string reason)
    {
        if (records.TryGetValue(d, out Record terminal) && terminal.returningToBase)
            return;
        if (!records.TryGetValue(d, out Record r) || r.state != c)
        {
            CancelOnly(c);
            return;
        }
        try
        {
            if (context.HasPlayerAssociation(d) || d.pilots == null || d.pilots.Length == 0 ||
                d.pilots[0] == null || d.pilots[0].currentState != c)
            {
                r.antiShipRadarThreat = null; r.state = null;
                ClearLaunchClearance(r);
                CancelOnly(c);
                return;
            }
            Fallback(d, reason);
            Log("wingman_fault", d, reason);
        }
        catch
        {
            r.antiShipRadarThreat = null; r.state = null;
            ClearLaunchClearance(r);
            CancelOnly(c);
        }
    }
    private static void CancelOnly(PilotBaseState s)
    {
        if (s is FixedWingDroneFollowState f)
            f.Cancel();
        else if (s is FixedWingDroneLoiterState l)
            l.Cancel();
        else if (s is FixedWingDroneLaunchClearanceState clearance)
            clearance.Cancel();
    }
    private bool RuntimeOwns(Aircraft d, PilotBaseState c, out string reason)
    {
        reason = "";
        try
        {
            if (!records.TryGetValue(d, out Record r) || r.returningToBase || r.state != c || d == activeLeader || !d.IsServer ||
                !d.LocalSim || d.pilots == null || d.pilots.Length == 0 || d.pilots[0] == null ||
                d.pilots[0].currentState != c || d.pilots[0].pilotType != Pilot.PilotType.Plane ||
                d.autopilot is not AutopilotPlane)
            {
                reason = "ownership";
                return false;
            }
            if (context.HasPlayerAssociation(d))
            {
                reason = "player_association";
                return false;
            }
            return true;
        }
        catch
        {
            reason = "runtime";
            return false;
        }
    }
    private void ScanCombat()
    {
        if (!context.CombatEnabled || !context.CanReconcile())
            return;
        bool hasHq = context.TryGetHq(out FactionHQ hq, out _);
        foreach (KeyValuePair<Aircraft, Record> pair in new List<KeyValuePair<Aircraft, Record>>(records))
        {
            if (pair.Value.mission == WingmanMission.Cap)
            {
                Record cap = pair.Value;
                if (cap.capTarget != null && !CapTargetValid(pair.Key, cap, cap.capTarget))
                    ClearCapTarget(pair.Key, cap, "target_invalid");
                if (cap.capTarget == null && cap.hasCapAnchor && HasAvailableAam(pair.Key))
                    TryAcquireCapTarget(pair.Key, cap);
            }
            if (pair.Value.airToAirTarget != null && (!hasHq || !CombatSupport.ValidHostileTrackedAircraft(pair.Key, pair.Value.airToAirTarget)))
                InvalidateAirToAirTarget(pair.Key, pair.Value.airToAirTarget, "target_invalid");
            if (pair.Value.antiShipTarget != null && (!hasHq || !CombatSupport.ValidHostileShip(pair.Key, pair.Value.antiShipTarget)))
                ClearAntiShip(pair.Key, "target_invalid");
            if (pair.Value.seadTarget != null && (!hasHq || !CombatSupport.ValidHostileSam(pair.Key, pair.Value.seadTarget)))
                ClearSead(pair.Key, "target_invalid");
            if (pair.Value.casTarget != null && (!hasHq || !CombatSupport.ValidHostileCasTarget(pair.Key, pair.Value.casTarget)))
                InvalidateCasTarget(pair.Key, pair.Value.casTarget, "target_invalid");
            if (pair.Value.strikeTarget != null && (!hasHq || !StrikeContains(pair.Value, pair.Value.strikeTarget.persistentID) ||
                                                    !CombatSupport.ValidHostileGround(pair.Key, pair.Value.strikeTarget)))
                InvalidateStrikeTarget(pair.Key, pair.Value.strikeTarget, "target_invalid");
            if (!pair.Value.returningToBase && pair.Value.mission != WingmanMission.None)
                ApplyDesired(pair.Key, pair.Value, "combat_scan");
        }
    }
    private bool HasAvailableAam(Aircraft defender)
    {
        for (int index = 0; index < defender.weaponStations.Count; index++)
        {
            WeaponStation station = defender.weaponStations[index];
            if (station.Ammo > 0 && CombatSupport.AirToAirStationCompatible(station))
                return true;
        }
        return false;
    }
    internal static StrikeWeaponStatus MissionWeaponStatus(Aircraft defender, WingmanMission mission)
    {
        bool compatible = false, ammo = false, waiting = false;
        foreach (WeaponStation station in defender.weaponStations)
        {
            bool stationCompatible = CombatCommandLogic.UsesAirToAirWeapons(mission) ? CombatSupport.AirToAirStationCompatible(station)
                                    : mission == WingmanMission.Sead ? CombatSupport.SeadStationCompatible(defender, station)
                                   : mission == WingmanMission.Cas ? CombatSupport.CasStationCompatible(defender, station)
                                  : mission == WingmanMission.Strike ? CombatSupport.StrikeBombStationCompatible(station)
                                                                  : CombatSupport.AntiShipStationCompatible(defender, station);
            if (!stationCompatible)
                continue;
            compatible = true;
            ammo |= station.Ammo > 0;
            waiting |= station.Reloading || station.SalvoInProgress;
        }
        return StrikeMissionLogic.ResolveWeaponStatus(compatible, ammo, waiting);
    }
    private bool DefenderOwns(Aircraft d, Record r) => !r.returningToBase && d != null && d != activeLeader &&
                                                       (r.state is FixedWingDroneFollowState ||
                                                        r.state is FixedWingDroneLoiterState) &&
                                                       CombatSupport.ValidAircraft(d) && d.IsServer && d.LocalSim &&
                                                       !context.HasPlayerAssociation(d) && d.pilots != null
                                                       && d.pilots.Length > 0 && d.pilots[0] != null
                                                       && d.pilots[0].pilotType == Pilot.PilotType.Plane
                                                       && d.autopilot is AutopilotPlane
                                                       && d.pilots[0].currentState == r.state;
    internal bool CombatOwns(Aircraft d, FixedWingDroneAirToAirAttackState state) =>
        records.TryGetValue(d, out Record r) && !r.returningToBase && r.state == state &&
        ((r.mission == WingmanMission.AirToAir && r.airToAirTarget != null && state.Target == r.airToAirTarget) ||
         (r.mission == WingmanMission.Cap && r.capTarget != null && state.Target == r.capTarget)) &&
                                                                            context.CombatEnabled && CombatSupport.Runtime(d, state) && !context.HasPlayerAssociation(d);
    internal bool TacticalOwns(Aircraft d, PilotBaseState state) =>
        records.TryGetValue(d, out Record r) && !r.returningToBase && r.state == state && CombatSupport.Runtime(d, state) &&
        !context.HasPlayerAssociation(d);
    internal bool MissionOwns(Aircraft d, PilotBaseState state, WingmanMission mission, Unit target) =>
        records.TryGetValue(d, out Record r) && !r.returningToBase && r.state == state && r.mission == mission &&
        (mission == WingmanMission.AntiShip ? r.antiShipTarget == target :
         mission == WingmanMission.Sead ? r.seadTarget == target : false) &&
        context.CombatEnabled && CombatSupport.Runtime(d, state) &&
        !context.HasPlayerAssociation(d);
    internal bool MayFire(Aircraft d, PilotBaseState state) => state is FixedWingDroneAirToAirAttackState attack
                                                                   ? CombatOwns(d, attack)
                                                                   : context.CombatEnabled && TacticalOwns(d, state);
    internal void CombatLog(Aircraft d, string text) => log("state=" + text + " pid=" + Pid(d));
    internal void CombatMissileAway(Aircraft d) => reporter.MissileAway(d);
    internal void A2aHoldFire() => reporter.A2aHoldFire();
    private readonly struct Candidate
    {
        internal readonly Aircraft defender, hostile;
        internal readonly Record record;
        private readonly float distance;
        private readonly string defenderPid;
        internal Candidate(Aircraft defender, Record record, Aircraft hostile)
        {
            this.defender = defender;
            this.record = record;
            this.hostile = hostile;
            distance = Vector3.Distance(defender.transform.position, hostile.transform.position);
            defenderPid = Pid(defender);
        }
        internal bool BetterThan(Candidate other)
        {
            if (distance != other.distance)
                return distance < other.distance;
            if (record.slotOrdinal != other.record.slotOrdinal)
                return record.slotOrdinal < other.record.slotOrdinal;
            return string.CompareOrdinal(defenderPid, other.defenderPid) < 0;
        }
    }
    private static float PhaseFor(int n)
    {
        float[] slots = { 0f, 180f, 90f, 270f, 45f, 225f, 135f, 315f };
        return n < slots.Length ? slots[n] : n * 360f / (n + 1);
    }
    internal void MarkAntiShipLaserPending(Aircraft drone) { if (records.TryGetValue(drone, out Record r)) r.antiShipLaserPending = true; }
    internal bool AntiShipLaserPending(Aircraft drone) => records.TryGetValue(drone, out Record r) && r.antiShipLaserPending;
    internal int ActiveAntiShipLasers(Aircraft drone) => records.TryGetValue(drone, out Record r) ? r.activeAntiShipLaserMissiles.Count : 0;
    internal void MarkSeadLaserPending(Aircraft drone) { if (records.TryGetValue(drone, out Record r)) r.seadLaserPending = true; }
    internal bool SeadLaserPending(Aircraft drone) => records.TryGetValue(drone, out Record r) && r.seadLaserPending;
    internal int ActiveSeadLasers(Aircraft drone) => records.TryGetValue(drone, out Record r) ? r.activeSeadLaserMissiles.Count : 0;
    internal void MarkCasLaserPending(Aircraft drone) { if (records.TryGetValue(drone, out Record r)) r.casLaserPending = true; }
    internal bool CasLaserPending(Aircraft drone) => records.TryGetValue(drone, out Record r) && r.casLaserPending;
    internal int ActiveCasLasers(Aircraft drone) => records.TryGetValue(drone, out Record r) ? r.activeCasLaserMissiles.Count : 0;
    internal bool InvalidateSeadTarget(Aircraft drone, Unit target, string reason)
    {
        if (!records.TryGetValue(drone, out Record r) || r.seadTarget != target) return false;
        ClearSeadRecord(r);
        if (r.mission == WingmanMission.Sead) { r.mission = WingmanMission.None; ApplyDesired(drone, r, reason); }
        return true;
    }
    private void OnMissionMissileRegistered(Aircraft drone, Missile missile)
    {
        if (!records.TryGetValue(drone, out Record r) || missile == null || missile.owner != drone) return;
        try
        {
            if (missile.GetWeaponInfo()?.laserGuided != true || missile.GetWeaponInfo()?.weaponPrefab?.GetComponent<LaserSeeker>() == null) return;
            if (r.mission == WingmanMission.AntiShip && r.antiShipLaserPending)
            { r.antiShipLaserPending = false; if (!r.activeAntiShipLaserMissiles.Contains(missile)) r.activeAntiShipLaserMissiles.Add(missile); CombatLog(drone, "asuw_laser_registered active=" + r.activeAntiShipLaserMissiles.Count); }
            else if (r.mission == WingmanMission.Sead && r.seadLaserPending)
            { r.seadLaserPending = false; if (!r.activeSeadLaserMissiles.Contains(missile)) r.activeSeadLaserMissiles.Add(missile); CombatLog(drone, "sead_laser_registered active=" + r.activeSeadLaserMissiles.Count); }
            else if (r.mission == WingmanMission.Cas && r.casLaserPending)
            { r.casLaserPending = false; if (!r.activeCasLaserMissiles.Contains(missile)) r.activeCasLaserMissiles.Add(missile); CombatLog(drone, "cas_laser_registered active=" + r.activeCasLaserMissiles.Count); }
        }
        catch { }
    }
    private void OnMissionMissileDeregistered(Aircraft drone, Missile missile)
    {
        if (!records.TryGetValue(drone, out Record r) || missile == null) return;
        if (r.activeAntiShipLaserMissiles.Remove(missile)) CombatLog(drone, "asuw_laser_deregistered active=" + r.activeAntiShipLaserMissiles.Count);
        if (r.activeSeadLaserMissiles.Remove(missile)) CombatLog(drone, "sead_laser_deregistered active=" + r.activeSeadLaserMissiles.Count);
        if (r.activeCasLaserMissiles.Remove(missile)) CombatLog(drone, "cas_laser_deregistered active=" + r.activeCasLaserMissiles.Count);
    }
    private static void ClearAntiShipLaserProgress(Record r) { r.antiShipLaserPending = false; r.activeAntiShipLaserMissiles.Clear(); }
    private static void ClearAntiShipRecord(Record r)
    {
        r.antiShipTarget = null;
        r.antiShipTargetId = null;
        r.antiShipRadarThreat = null;
        ClearAntiShipLaserProgress(r);
    }
    private static void ClearAirToAirRecord(Record r)
    {
        r.airToAirTarget = null;
        r.airToAirTargetId = null;
    }
    private static void ClearCapRecord(Record r)
    {
        r.hasCapAnchor = false; r.capAnchor = default; r.capRevision++; r.loiterAnchorRevision++;
        r.capTarget = null; r.capTargetId = null; r.capTargetIsRetaliation = false;
    }
    private void ClearCapTarget(Aircraft drone, Record r, string reason)
    {
        if (r.capTarget == null) return;
        r.capTarget = null; r.capTargetId = null; r.capTargetIsRetaliation = false;
        CombatLog(drone, "cap_target_cleared reason=" + reason);
    }
    private static void ClearSeadRecord(Record r)
    {
        r.seadTarget = null;
        r.seadTargetId = null;
        r.seadLaserPending = false;
        r.activeSeadLaserMissiles.Clear();
    }
    private static void ClearCasTarget(Record r)
    {
        r.casTarget = null;
        r.casTargetId = null;
        r.casLaserPending = false;
        r.activeCasLaserMissiles.Clear();
    }
    private static void ClearCasRecord(Record r)
    {
        ClearCasTarget(r);
        r.hasCasCenter = false;
        r.casCenter = default;
        r.casCenterRevision++;
    }
    private static bool StrikeContains(Record r, PersistentID id)
    {
        foreach (PersistentID candidate in r.strikeTargetIds)
            if (candidate == id) return true;
        return false;
    }
    private bool CapTargetValid(Aircraft drone, Record r, Aircraft target)
    {
        if (!r.hasCapAnchor || !CombatSupport.ValidHostileTrackedAircraft(drone, target)) return false;
        if (drone.NetworkHQ == null || !drone.NetworkHQ.trackingDatabase.TryGetValue(target.persistentID, out TrackingInfo tracking) ||
            tracking.id != target.persistentID || !tracking.TryGetUnit(out Unit tracked) || tracked != target || !target.airborne ||
            !Finite(tracking.lastKnownPosition.x) || !Finite(tracking.lastKnownPosition.y) || !Finite(tracking.lastKnownPosition.z)) return false;
        CapCandidate candidate = new CapCandidate(target.persistentID.ToString(), tracking.lastKnownPosition.x,
            tracking.lastKnownPosition.z, true, true, true, true);
        return CapMissionLogic.ShouldKeepTarget(candidate, r.capTargetIsRetaliation, HasAvailableAam(drone), r.capAnchor.x, r.capAnchor.z);
    }
    private void TryAcquireCapTarget(Aircraft drone, Record r)
    {
        if (drone.NetworkHQ == null) return;
        Aircraft? best = null; CapCandidate bestCandidate = default;
        foreach (KeyValuePair<PersistentID, TrackingInfo> pair in drone.NetworkHQ.trackingDatabase)
        {
            TrackingInfo tracking = pair.Value;
            if (!tracking.TryGetUnit(out Unit unit) || unit is not Aircraft candidate || tracking.id != candidate.persistentID ||
                !Finite(tracking.lastKnownPosition.x) || !Finite(tracking.lastKnownPosition.y) || !Finite(tracking.lastKnownPosition.z)) continue;
            CapCandidate next = new CapCandidate(candidate.persistentID.ToString(), tracking.lastKnownPosition.x, tracking.lastKnownPosition.z,
                CombatSupport.ValidAircraft(candidate), CombatSupport.IsHostile(drone, candidate), CombatSupport.ValidHostileTrackedAircraft(drone, candidate), candidate.airborne);
            if (!CapMissionLogic.IsEligible(next) || !CapMissionLogic.IsWithinScope(r.capAnchor.x, r.capAnchor.z, next.x, next.z)) continue;
            if (best == null || CapMissionLogic.IsBetterCandidate(next, r.capAnchor.x, r.capAnchor.z, bestCandidate)) { best = candidate; bestCandidate = next; }
        }
        if (best == null) return;
        r.capTarget = best; r.capTargetId = best.persistentID.ToString(); r.capTargetIsRetaliation = false;
        CombatLog(drone, "cap_target_acquired target_pid=" + r.capTargetId);
    }
    private static void ClearStrikeTarget(Record r)
    {
        r.strikeTarget = null;
        r.strikeTargetId = null;
    }
    private static void ClearStrikeRecord(Record r)
    {
        r.strikeTargetIds = Array.Empty<PersistentID>();
        ClearStrikeTarget(r);
        r.strikeReleasedTargetIds.Clear();
        r.strikeRevision++;
    }
    private void UnbindMissionMissileHandlers(Aircraft drone, Record? r)
    {
        try { if (missileRegisterHandlers.TryGetValue(drone, out Action<Missile> add)) drone.onRegisterMissile -= add; } catch { }
        try { if (missileDeregisterHandlers.TryGetValue(drone, out Action<Missile> remove)) drone.onDeregisterMissile -= remove; } catch { }
        missileRegisterHandlers.Remove(drone); missileDeregisterHandlers.Remove(drone); if (r != null) { ClearCapRecord(r); ClearAirToAirRecord(r); ClearAntiShipRecord(r); ClearSeadRecord(r); ClearCasRecord(r); ClearStrikeRecord(r); }
    }
    private static void ClearLaunchClearance(Record r)
    {
        r.launchClearanceActive = false;
        r.launchCarrier = null;
        r.launchCarrierId = default;
        r.egressDirection = Vector3.zero;
    }
    private sealed class Record
    {
        internal readonly int slotOrdinal;
        internal int groupId, groupSlot;
        internal readonly float loiterPhase;
        internal readonly FormationSlot slot;
        internal readonly HashSet<PersistentID> attemptedDefenseThreats = new HashSet<PersistentID>();
        internal PilotBaseState? state;
        internal bool followFallbackActive;
        internal float followFallbackUntil;
        internal bool returningToBase;
        internal string? lastRtbPilotState;
        internal bool initialStateReported;
        internal bool launchClearanceActive;
        internal Aircraft? launchCarrier;
        internal PersistentID launchCarrierId;
        internal Vector3 egressDirection;
        internal WingmanMission mission;
        internal Aircraft? airToAirTarget;
        internal string? airToAirTargetId;
        internal bool hasCapAnchor;
        internal GlobalPosition capAnchor;
        internal int capRevision;
        internal Aircraft? capTarget;
        internal string? capTargetId;
        internal bool capTargetIsRetaliation;
        internal DroneModeOverride modeOverride = DroneModeOverride.Auto;
        internal bool hasLoiterPoint;
        internal GlobalPosition loiterPoint;
        internal int loiterAnchorRevision;
        internal Ship? antiShipTarget;
        internal string? antiShipTargetId;
        internal Missile? antiShipRadarThreat;
        internal Unit? seadTarget;
        internal string? seadTargetId;
        internal bool seadLaserPending;
        internal readonly List<Missile> activeSeadLaserMissiles = new List<Missile>();
        internal bool antiShipLaserPending;
        internal readonly List<Missile> activeAntiShipLaserMissiles = new List<Missile>();
        internal bool hasCasCenter;
        internal GlobalPosition casCenter;
        internal int casCenterRevision;
        internal Unit? casTarget;
        internal string? casTargetId;
        internal bool casLaserPending;
        internal readonly List<Missile> activeCasLaserMissiles = new List<Missile>();
        internal PersistentID[] strikeTargetIds = Array.Empty<PersistentID>();
        internal int strikeRevision;
        internal readonly HashSet<PersistentID> strikeReleasedTargetIds = new HashSet<PersistentID>();
        internal Unit? strikeTarget;
        internal string? strikeTargetId;
        internal Record(int ordinal, float phase, FormationSlot slot, int groupId, int groupSlot)
        {
            slotOrdinal = ordinal;
            loiterPhase = phase;
            this.slot = slot;
            this.groupId = groupId;
            this.groupSlot = groupSlot;
        }
    }
    private readonly struct Kinematics
    {
        internal readonly Aircraft aircraft;
        internal readonly int slotOrdinal;
        internal readonly bool leader;
        internal readonly string pid;
        internal readonly Vector3 position, velocity;
        internal Kinematics(Aircraft a, int slot, bool leader, string pid, Vector3 p, Vector3 v)
        {
            aircraft = a;
            slotOrdinal = slot;
            this.leader = leader;
            this.pid = pid;
            position = p;
            velocity = v;
        }
    }
    private readonly struct LeaderHullEnvelope
    {
        internal readonly bool available;
        internal readonly Vector3 center, extents;
        internal LeaderHullEnvelope(bool available, Vector3 center, Vector3 extents)
        { this.available = available; this.center = center; this.extents = extents; }
    }
    internal bool ActiveDefenseThreat(Aircraft drone, Missile missile) =>
        CombatSupport.ValidMissile(missile) && missile.targetID == drone.persistentID;
    internal bool ValidDefenseThreat(Aircraft drone, Missile missile) =>
        ActiveDefenseThreat(drone, missile) && records.TryGetValue(drone, out Record r) &&
        !r.returningToBase && !r.attemptedDefenseThreats.Contains(missile.persistentID);
    internal bool TryMarkDefenseAttempt(Aircraft drone, Missile missile)
    {
        if (!records.TryGetValue(drone, out Record r) || r.returningToBase || !ActiveDefenseThreat(drone, missile) ||
            r.attemptedDefenseThreats.Contains(missile.persistentID))
            return false;
        r.attemptedDefenseThreats.Add(missile.persistentID);
        return true;
    }
    internal bool TryGetAntiShipRadarThreat(Aircraft drone, out Missile threat, out float tti)
    {
        threat = null!; tti = float.PositiveInfinity;
        if (!records.TryGetValue(drone, out Record r) || r.mission != WingmanMission.AntiShip ||
            r.antiShipRadarThreat == null || !ActiveDefenseThreat(drone, r.antiShipRadarThreat) ||
            !TryDefenseTti(drone, r.antiShipRadarThreat, out tti) || !float.IsFinite(tti) || tti < 0f)
            return false;
        threat = r.antiShipRadarThreat;
        return true;
    }
    private void ScanMissileDefense()
    {
        if (!context.CombatEnabled)
            return;
        foreach (KeyValuePair<Aircraft, Record> pair in new List<KeyValuePair<Aircraft, Record>>(records))
        {
            Aircraft d = pair.Key;
            Record r = pair.Value;
            if (r.returningToBase || r.launchClearanceActive || d == null)
                continue;
            FixedWingDroneMissileDefenseState? current = r.state as FixedWingDroneMissileDefenseState;
            bool currentActive = current != null && ActiveDefenseThreat(d, current.Threat);
            MissileSeekerType currentSeeker = current == null ? MissileSeekerType.Unknown :
                MissileDefenseLogic.ClassifySeekerType(current.Threat.GetSeekerType());
            Missile? best = null;
            float bestTti = float.PositiveInfinity;
            if (d != activeLeader && !context.HasPlayerAssociation(d) &&
                CombatSupport.ValidAircraft(d) && r.state != null && d.pilots != null && d.pilots.Length > 0 &&
                d.pilots[0] != null && d.pilots[0].currentState == r.state)
            {
                MissileWarning warning = d.GetMissileWarningSystem();
                if (warning != null)
                    foreach (Missile m in warning.knownMissiles)
                        if ((ValidDefenseThreat(d, m) || current != null && m == current.Threat && ActiveDefenseThreat(d, m) ||
                             r.mission == WingmanMission.AntiShip &&
                             MissileDefenseLogic.ClassifySeekerType(m.GetSeekerType()) == MissileSeekerType.Radar &&
                             ActiveDefenseThreat(d, m)) && TryDefenseTti(d, m, out float tti) && tti < bestTti)
                        {
                            best = m;
                            bestTti = tti;
                        }
            }
            if (best == null)
            {
                if (r.antiShipRadarThreat != null)
                    CombatLog(d, "asuw_radar_threat_clear threat_pid=" + r.antiShipRadarThreat.persistentID);
                r.antiShipRadarThreat = null;
                if (current != null && !HasEffectiveDefense(d, r, out _))
                    ResumeDesired(d, current, "no_threat");
                continue;
            }
            AntiShipDefenseArbitration arbitration = MissileDefenseLogic.ArbitrateAntiShipDefense(r.mission == WingmanMission.AntiShip,
                currentActive, currentSeeker, MissileDefenseLogic.ClassifySeekerType(best.GetSeekerType()));
            if (arbitration == AntiShipDefenseArbitration.PreserveGenericDefense)
            {
                if (r.antiShipRadarThreat != null)
                    CombatLog(d, "asuw_radar_threat_clear threat_pid=" + r.antiShipRadarThreat.persistentID);
                r.antiShipRadarThreat = null;
                continue;
            }
            if (arbitration == AntiShipDefenseArbitration.DelegateRadarToAntiShip)
            {
                if (r.antiShipRadarThreat == null)
                    CombatLog(d, "asuw_radar_threat_enter threat_pid=" + best.persistentID + " tti=" + bestTti);
                else if (r.antiShipRadarThreat != best)
                    CombatLog(d, "asuw_radar_threat_replaced old_pid=" + r.antiShipRadarThreat.persistentID +
                                 " threat_pid=" + best.persistentID + " tti=" + bestTti);
                r.antiShipRadarThreat = best;
                if (current != null && r.antiShipTarget != null)
                    SwitchTo(d, r, new FixedWingDroneAntiShipAttackState(this, d, r.antiShipTarget), "radar_asuw_resume");
                continue;
            }
            if (r.antiShipRadarThreat != null)
                CombatLog(d, "asuw_radar_threat_clear threat_pid=" + r.antiShipRadarThreat.persistentID);
            r.antiShipRadarThreat = null;
            if (current != null && current.Threat == best)
                continue;
            if (r.mission == WingmanMission.Cap && r.capTarget == null && best.owner is Aircraft launcher &&
                CombatSupport.ValidHostileTrackedAircraft(d, launcher) && launcher.airborne)
            {
                r.capTarget = launcher; r.capTargetId = launcher.persistentID.ToString(); r.capTargetIsRetaliation = true;
                CombatLog(d, "cap_retaliation_acquired target_pid=" + r.capTargetId);
            }
            SwitchTo(d, r, new FixedWingDroneMissileDefenseState(this, d, best), "defense");
        }
    }
    private static bool TryDefenseTti(Aircraft d, Missile m, out float tti)
    {
        tti = float.PositiveInfinity;
        try
        {
            Vector3 delta = d.transform.position - m.transform.position, relative = d.rb.velocity - m.rb.velocity;
            float range = delta.magnitude, closing = range > .1f ? Vector3.Dot(-delta.normalized, relative) : float.NaN;
            tti = closing > 0f ? range / closing : float.PositiveInfinity;
            return true;
        }
        catch
        {
            return false;
        }
    }
    internal void Disarm(Aircraft d)
    {
        try
        {
            ControlInputs i = d.GetInputs();
            i.pitch = i.roll = i.yaw = i.brake = i.throttle = 0f;
        }
        catch
        {
        }
    }
    private static string Pid(Aircraft? d)
    {
        try
        {
            return d == null ? "none" : d.persistentID.ToString();
        }
        catch
        {
            return "unavailable";
        }
    }
    private static string StateName(PilotBaseState? state) => state == null ? "null" : state.GetType().Name;
    private void RejectOnce(Aircraft d, string reason)
    {
        if (rejected.Add(d))
            Log("follow_rejected", d, reason);
    }
    private void Log(string state, Aircraft d, string reason)
    {
        log("state=" + state + " pid=" + Pid(d) + " reason=" + reason);
    }
    private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    private static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
}
