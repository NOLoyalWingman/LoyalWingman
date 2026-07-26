using System;
using UnityEngine;
namespace LoyalWingman;
// This is a cascaded formation controller; CPA directives only shape formation avoidance.
internal sealed class FixedWingDroneFollowState : PilotBaseState
{
    private readonly WingmanAiManager manager;
    private readonly Aircraft drone;
    private readonly FormationSlot slot;
    private bool cancelled, hasFilteredDroneSpeed, hasPreviousLeaderForward, hasPreviousDroneHorizontalDirection,
        integrationFrozen, avoidanceActive, catchupArmed;
    private Aircraft? controllerLeader;
    private float closureIntegral, followElapsed, infeasibleElapsed, nextTelemetryTime, filteredDroneSpeed,
        leaderTurnRate, droneTurnRate, avoidanceWeight, clearSince, avoidanceEntered, minSep, minCpa;
    private string avoidanceOther = "none";
    private Vector3 previousLeaderForward, previousDroneHorizontalDirection;
    private int telemetryFrames, saturatedFrames;
    private float minAlignment, maxAbsLeaderTurnRate, maxAbsDroneTurnRate, minDroneSpeed, maxDroneSpeed, minThrottle,
        maxThrottle, maxAbsCrossTrackError, minAimAlongDistance;
    private const float LookaheadTime = 2.5f, MinimumLookahead = 250f, MaximumLookahead = 400f, AimAheadMargin = 100f,
                        OuterTimeConstant = 15f, MaxClosingRate = 35f, CatchupMaxClosingRate = 55f,
                        MaxOpeningRate = 25f, SpeedFilterTau = .50f, ThrottleFeedForward = .10f, MinimumThrottle = .05f,
                        MaximumThrottle = 1f, InnerKp = .004f, InnerKi = .0008f, IntegrationErrorLimit = 25f,
                        IntegralMinimum = -.05f, IntegralMaximum = .60f, TelemetryInterval = 2f;
    internal FixedWingDroneFollowState(WingmanAiManager manager, Aircraft drone, FormationSlot slot)
    {
        this.manager = manager;
        this.drone = drone;
        this.slot = slot;
    }
    public override void EnterState(Pilot p)
    {
        ResetController();
        if (!cancelled)
        {
            drone.flightAssist = true;
            drone.GetInputs().brake = 0f;
        }
    }
    public override void UpdateState(Pilot p)
    {
    }
    public override void FixedUpdateState(Pilot p)
    {
        if (cancelled)
            return;
        try
        {
            if (!manager.TryGetGuidance(drone, this, out Aircraft leader, out _))
                return;
            if (controllerLeader != leader)
            {
                ResetController();
                controllerLeader = leader;
            }
            if (drone.autopilot is not AutopilotPlane autopilot || drone.rb == null || leader.rb == null)
            {
                manager.Fallback(drone, "autopilot_or_rb");
                return;
            }
            float dt = Time.fixedDeltaTime;
            if (!Finite(dt) || dt <= 0f)
            {
                ResetController();
                return;
            }
            if (!Finite(leader.radarAlt) || !Finite(drone.rb.velocity) || !Finite(leader.rb.velocity))
            {
                ResetController();
                manager.Fallback(drone, "non_finite");
                return;
            }
            Vector3 forward = Vector3.ProjectOnPlane(leader.transform.forward, Vector3.up);
            if (forward.sqrMagnitude < .01f)
                forward = Vector3.ProjectOnPlane(leader.rb.velocity, Vector3.up);
            if (forward.sqrMagnitude < .01f)
            {
                manager.Fallback(drone, "leader_forward");
                return;
            }
            forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            if (!Finite(right) || right.sqrMagnitude < .01f)
            {
                manager.Fallback(drone, "leader_right");
                return;
            }
            right.Normalize();
            Vector3 dv = Vector3.ProjectOnPlane(drone.rb.velocity, Vector3.up),
                    lv = Vector3.ProjectOnPlane(leader.rb.velocity, Vector3.up);
            float rawSpeed = dv.magnitude, leaderSpeed = lv.magnitude;
            if (!Finite(dv) || !Finite(lv) || !Finite(rawSpeed) || !Finite(leaderSpeed))
            {
                ResetController();
                manager.Fallback(drone, "non_finite");
                return;
            }
            float alpha = dt / (SpeedFilterTau + dt);
            filteredDroneSpeed =
                hasFilteredDroneSpeed ? filteredDroneSpeed + alpha * (rawSpeed - filteredDroneSpeed) : rawSpeed;
            hasFilteredDroneSpeed = true;
            Vector3 formation =
                leader.transform.position - forward * slot.back + right * slot.lateral + Vector3.up * slot.up;
            float distanceError = Vector3.Dot(formation - drone.transform.position, forward);
            Vector3 horiz = Vector3.ProjectOnPlane(formation - drone.transform.position, Vector3.up);
            float range = horiz.magnitude;
            float speedLook = Mathf.Clamp(filteredDroneSpeed * LookaheadTime, MinimumLookahead, MaximumLookahead),
                  guard = -distanceError + AimAheadMargin,
                  lookahead = Mathf.Clamp(Mathf.Max(speedLook, guard), MinimumLookahead, MaximumLookahead);
            Vector3 baseAim = formation + forward * lookahead;
            float aimAlong = Vector3.Dot(baseAim - drone.transform.position, forward),
                  cross = Vector3.Dot(horiz, right);
            float leaderAlong = Vector3.Dot(lv, forward), droneAlong = Vector3.Dot(dv, forward),
                  diagnostic = droneAlong - leaderAlong;
            Vector3 direction = dv.sqrMagnitude > .01f ? dv.normalized : Vector3.zero;
            Vector3 aimLos = Vector3.ProjectOnPlane(baseAim - drone.transform.position, Vector3.up);
            float aimAlignment = direction == Vector3.zero || aimLos.sqrMagnitude < .01f
                                     ? -1f
                                     : Vector3.Dot(direction, aimLos.normalized);
            float aimGate =
                Finite(aimAlignment) ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.65f, .90f, aimAlignment)) : 0f;
            bool hasDirective = manager.TryGetSeparation(drone, this, out SeparationDirective directive);
            UpdateAvoidance(hasDirective, directive, dt);
            int side = slot.lateral < 0f ? -1 : slot.lateral > 0f ? 1 : (slot.ordinal % 2 == 0 ? -1 : 1);
            Vector3 guidanceAim = baseAim + (right * side * 250f + Vector3.up * 150f) * avoidanceWeight;
            float altitude = leader.radarAlt + slot.up + 150f * avoidanceWeight;
            if (!Finite(formation) || !Finite(baseAim) || !Finite(guidanceAim) || !Finite(distanceError) ||
                !Finite(lookahead) || !Finite(cross) || !Finite(range) || !Finite(altitude))
            {
                ResetController();
                manager.Fallback(drone, "non_finite");
                return;
            }
            leaderTurnRate = TurnRate(ref previousLeaderForward, ref hasPreviousLeaderForward, forward, dt);
            if (!catchupArmed && distanceError > 1800f && range > 2200f)
                catchupArmed = true;
            else if (catchupArmed && (distanceError < 1000f || range < 1500f))
                catchupArmed = false;
            float alongFraction = range > 1f ? Mathf.Clamp01(distanceError / range) : 0f;
            float alongGate = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.25f, .60f, alongFraction));
            float distanceGate = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1000f, 2500f, distanceError));
            float rangeGate = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1500f, 3000f, range));
            float turnGate = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1.5f, 4f, Mathf.Abs(leaderTurnRate)));
            bool avoidanceClear = !avoidanceActive && avoidanceWeight <= .05f;
            float catchupWeight =
                catchupArmed && avoidanceClear ? aimGate * alongGate * distanceGate * rangeGate * turnGate : 0f;
            if (!Finite(catchupWeight))
                catchupWeight = 0f;
            catchupWeight = Mathf.Clamp01(catchupWeight);
            float effectiveMaxClosure = Mathf.Lerp(MaxClosingRate, CatchupMaxClosingRate, catchupWeight);
            if (!Finite(effectiveMaxClosure))
                effectiveMaxClosure = MaxClosingRate;
            float desiredClosure = Mathf.Clamp(distanceError / OuterTimeConstant, -MaxOpeningRate, effectiveMaxClosure),
                  desiredSpeed = Mathf.Max(0f, leaderSpeed + desiredClosure),
                  speedError = desiredSpeed - filteredDroneSpeed;
            droneTurnRate = direction == Vector3.zero
                                ? 0f
                                : TurnRate(ref previousDroneHorizontalDirection,
                                           ref hasPreviousDroneHorizontalDirection, direction, dt);
            float alignment = direction == Vector3.zero ? 0f : Vector3.Dot(direction, forward);
            bool stable = alignment >= .85f;
            float error = Mathf.Clamp(speedError, -IntegrationErrorLimit, IntegrationErrorLimit),
                  pp = InnerKp * speedError, pre = ThrottleFeedForward + pp + closureIntegral;
            bool integrate = stable && avoidanceWeight <= .05f &&
                             ((pre > MinimumThrottle && pre < MaximumThrottle) ||
                              (pre >= MaximumThrottle && error < 0f) || (pre <= MinimumThrottle && error > 0f));
            integrationFrozen = !integrate;
            if (integrate)
                closureIntegral = Mathf.Clamp(closureIntegral + InnerKi * error * dt, IntegralMinimum, IntegralMaximum);
            float raw = ThrottleFeedForward + pp + closureIntegral,
                  throttle = Mathf.Clamp(raw, MinimumThrottle, MaximumThrottle);
            if (!Finite(desiredClosure) || !Finite(desiredSpeed) || !Finite(speedError) || !Finite(raw) ||
                !Finite(throttle))
            {
                ResetController();
                manager.Fallback(drone, "non_finite");
                return;
            }
            if (Time.fixedTime >= nextTelemetryTime)
                manager.LogFollowControl(drone, catchupArmed, catchupWeight, aimAlignment, alongFraction, aimGate,
                                         alongGate, distanceGate, rangeGate, turnGate, effectiveMaxClosure);
            ControlInputs inputs = drone.GetInputs();
            inputs.throttle = throttle;
            inputs.brake = 0f;
            autopilot.AutoAim(guidanceAim.ToGlobalPosition(), true, false, false, .5f, 75f, true, altitude,
                              leader.rb.velocity);
            followElapsed += dt;
            telemetryFrames++;
            if (throttle <= MinimumThrottle + .005f || throttle >= MaximumThrottle - .001f)
                saturatedFrames++;
            minAlignment = Mathf.Min(minAlignment, alignment);
            maxAbsLeaderTurnRate = Mathf.Max(maxAbsLeaderTurnRate, Mathf.Abs(leaderTurnRate));
            maxAbsDroneTurnRate = Mathf.Max(maxAbsDroneTurnRate, Mathf.Abs(droneTurnRate));
            minDroneSpeed = Mathf.Min(minDroneSpeed, rawSpeed);
            maxDroneSpeed = Mathf.Max(maxDroneSpeed, rawSpeed);
            minThrottle = Mathf.Min(minThrottle, throttle);
            maxThrottle = Mathf.Max(maxThrottle, throttle);
            maxAbsCrossTrackError = Mathf.Max(maxAbsCrossTrackError, Mathf.Abs(cross));
            minAimAlongDistance = Mathf.Min(minAimAlongDistance, aimAlong);
            if (avoidanceWeight <= .05f && !avoidanceActive && followElapsed >= 10f && throttle <= .055f &&
                alignment >= .95f && leaderTurnRate <= 2f && droneTurnRate <= 3f && speedError <= -8f &&
                distanceError <= -100f && diagnostic > desiredClosure + 5f)
                infeasibleElapsed += dt;
            else
                infeasibleElapsed = 0f;
            if (infeasibleElapsed >= 5.5f)
            {
                manager.RequestLoiter(drone, this, "infeasible_min_throttle");
                return;
            }
            if (Time.fixedTime >= nextTelemetryTime)
            {
                SeparationDirective telemetry =
                    hasDirective ? directive : new SeparationDirective(false, "none", -1f, -1f, -1f);
                float episodeMinSep = avoidanceActive ? minSep : -1f, episodeMinCpa = avoidanceActive ? minCpa : -1f;
                manager.LogFollowControl(
                    drone, distanceError, desiredClosure, leaderSpeed, rawSpeed, filteredDroneSpeed, desiredSpeed,
                    speedError, leaderAlong, droneAlong, diagnostic, lookahead, speedLook, guard, aimAlong, cross,
                    range, maxAbsCrossTrackError, minAimAlongDistance, alignment, minAlignment, leaderTurnRate,
                    droneTurnRate, maxAbsLeaderTurnRate, maxAbsDroneTurnRate, minDroneSpeed, maxDroneSpeed, pp,
                    closureIntegral, ThrottleFeedForward, raw, throttle, minThrottle, maxThrottle,
                    telemetryFrames == 0 ? 0f : 100f * saturatedFrames / telemetryFrames, integrationFrozen,
                    infeasibleElapsed, slot, avoidanceActive, avoidanceWeight, telemetry, episodeMinSep, episodeMinCpa);
                manager.LogSeparationTelemetry(drone, hasDirective, telemetry, episodeMinSep, episodeMinCpa);
                ResetTelemetryWindow();
                nextTelemetryTime = Time.fixedTime + TelemetryInterval;
            }
        }
        catch (Exception e)
        {
            ResetController();
            manager.Fallback(drone, "fixed_" + e.GetType().Name);
        }
    }
    private void UpdateAvoidance(bool hasDirective, SeparationDirective directive, float dt)
    {
        bool actionable = hasDirective && directive.shouldYield;
        bool danger = actionable && HullCpaLogic.LaunchClearanceRequired(true, directive.currentDistance, directive.cpaDistance);
        if (!avoidanceActive && !danger)
        {
            clearSince = -1f;
            avoidanceWeight = Mathf.MoveTowards(avoidanceWeight, 0f, dt / 3f);
            return;
        }
        if (danger)
        {
            clearSince = -1f;
            minSep = Mathf.Min(minSep, directive.currentDistance);
            minCpa = Mathf.Min(minCpa, directive.cpaDistance);
            if (!avoidanceActive)
            {
                avoidanceActive = true;
                avoidanceEntered = Time.realtimeSinceStartup;
                avoidanceOther = directive.otherPid;
                minSep = directive.currentDistance;
                minCpa = directive.cpaDistance;
                manager.LogAvoidanceEnter(drone, slot, directive);
            }
            avoidanceWeight = Mathf.MoveTowards(avoidanceWeight, 1f, dt);
            return;
        }
        bool clear = !actionable || (directive.currentDistance > 450f && directive.cpaDistance > 350f);
        if (!clear)
        {
            clearSince = -1f;
            avoidanceWeight = Mathf.MoveTowards(avoidanceWeight, 1f, dt);
            return;
        }
        if (clearSince < 0f)
            clearSince = Time.realtimeSinceStartup;
        if (Time.realtimeSinceStartup - clearSince < 2f)
        {
            avoidanceWeight = Mathf.MoveTowards(avoidanceWeight, 1f, dt);
            return;
        }
        avoidanceActive = false;
        manager.LogAvoidanceExit(drone, slot, avoidanceOther, Time.realtimeSinceStartup - avoidanceEntered, minSep,
                                 minCpa);
        avoidanceOther = "none";
        minSep = minCpa = float.PositiveInfinity;
        avoidanceWeight = Mathf.MoveTowards(avoidanceWeight, 0f, dt / 3f);
    }
    public override void LeaveState()
    {
        ResetController();
        try
        {
            drone.GetInputs().brake = 0f;
        }
        catch
        {
        }
    }
    internal void Cancel()
    {
        cancelled = true;
        ResetController();
    }
    private void ResetController()
    {
        closureIntegral = followElapsed = infeasibleElapsed = filteredDroneSpeed = leaderTurnRate = droneTurnRate =
            avoidanceWeight = 0f;
        hasFilteredDroneSpeed = hasPreviousLeaderForward = hasPreviousDroneHorizontalDirection = integrationFrozen =
            avoidanceActive = catchupArmed = false;
        clearSince = -1f;
        avoidanceEntered = 0f;
        avoidanceOther = "none";
        minSep = minCpa = float.PositiveInfinity;
        previousLeaderForward = previousDroneHorizontalDirection = Vector3.zero;
        controllerLeader = null;
        nextTelemetryTime = Time.fixedTime + TelemetryInterval;
        ResetTelemetryWindow();
    }
    private void ResetTelemetryWindow()
    {
        telemetryFrames = saturatedFrames = 0;
        minAlignment = 1f;
        maxAbsLeaderTurnRate = maxAbsDroneTurnRate = maxDroneSpeed = maxThrottle = maxAbsCrossTrackError = 0f;
        minDroneSpeed = minThrottle = minAimAlongDistance = float.PositiveInfinity;
    }
    private static float TurnRate(ref Vector3 p, ref bool has, Vector3 c, float dt)
    {
        float r = has ? Vector3.Angle(p, c) / dt : 0f;
        p = c;
        has = true;
        return Finite(r) ? r : 0f;
    }
    private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    private static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
}
