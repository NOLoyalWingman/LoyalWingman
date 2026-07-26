using System;
using UnityEngine;

namespace LoyalWingman;

internal sealed class FixedWingDroneLaunchClearanceState : PilotBaseState
{
    private readonly WingmanAiManager manager;
    private readonly Aircraft drone;
    private readonly Aircraft carrier;
    private readonly PersistentID carrierId;
    private readonly Vector3 egressDirection;
    private bool cancelled, completionRequested, firstControlLogged;

    internal FixedWingDroneLaunchClearanceState(WingmanAiManager manager, Aircraft drone, Aircraft carrier,
                                                 PersistentID carrierId, Vector3 normalizedEgressDirection)
    {
        this.manager = manager;
        this.drone = drone;
        this.carrier = carrier;
        this.carrierId = carrierId;
        egressDirection = normalizedEgressDirection;
    }

    public override void EnterState(Pilot pilot)
    {
        if (cancelled)
            return;
        drone.flightAssist = true;
        manager.Disarm(drone);
        ControlInputs inputs = drone.GetInputs();
        inputs.brake = 0f;
        inputs.throttle = 0f;
        manager.LogLaunchClearanceEnter(drone, carrier, carrierId, egressDirection);
    }

    public override void UpdateState(Pilot pilot)
    {
    }

    public override void FixedUpdateState(Pilot pilot)
    {
        if (cancelled || completionRequested)
            return;
        try
        {
            bool available = manager.TrySampleLaunchClearance(drone, this, carrier, carrierId,
                out float current, out float minimum, out float timeToMinimum, out string source);
            if (!firstControlLogged)
            {
                firstControlLogged = true;
                float alignment = drone.rb != null && Finite(drone.rb.velocity) &&
                                  drone.rb.velocity.sqrMagnitude > 0f
                                      ? Vector3.Dot(egressDirection, drone.rb.velocity.normalized)
                                      : float.NaN;
                manager.LogLaunchClearanceControl(drone, carrier, carrierId, source, available, current, minimum,
                                                  timeToMinimum, alignment);
            }
            if (!HullCpaLogic.LaunchClearanceRequired(available, current, minimum))
            {
                completionRequested = true;
                manager.CompleteLaunchClearance(drone, this, carrier, carrierId,
                                                available ? "carrier_clear" : "measurement_unavailable");
                return;
            }
            if (drone.autopilot is not AutopilotPlane autopilot || drone.rb == null)
            {
                completionRequested = true;
                manager.CompleteLaunchClearance(drone, this, carrier, carrierId, "autopilot_unavailable");
                return;
            }
            ControlInputs inputs = drone.GetInputs();
            inputs.throttle = 0f;
            inputs.brake = 0f;
            autopilot.AutoAim(drone.GlobalPosition() + egressDirection * 10000f, true, true, false, .5f, 75f, false,
                              0f, drone.rb.velocity);
        }
        catch (Exception e)
        {
            completionRequested = true;
            manager.CompleteLaunchClearance(drone, this, carrier, carrierId, "exception_" + e.GetType().Name);
        }
    }

    public override void LeaveState()
    {
        try
        {
            ControlInputs inputs = drone.GetInputs();
            inputs.brake = 0f;
            inputs.throttle = 0f;
        }
        catch { }
    }

    internal void Cancel() => cancelled = true;
    private static bool Finite(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                                                  !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                                                  !float.IsNaN(value.z) && !float.IsInfinity(value.z);
}
