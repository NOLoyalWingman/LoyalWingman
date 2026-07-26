using System;
using UnityEngine;

namespace LoyalWingman;

internal sealed class FixedWingDroneLoiterState : PilotBaseState
{
    private readonly WingmanAiManager manager;
    private readonly Aircraft drone;
    private readonly float phase;
    private bool cancelled;
    private bool cruisingToLoiterPoint;
    private int loiterRevision = -1;
    internal FixedWingDroneLoiterState(WingmanAiManager manager, Aircraft drone, float phase)
    {
        this.manager = manager;
        this.drone = drone;
        this.phase = phase;
    }
    internal bool CruisingToLoiterPoint => cruisingToLoiterPoint;
    public override void EnterState(Pilot pilot)
    {
        if (!cancelled)
        {
            drone.flightAssist = true;
            drone.GetInputs().brake = 0f;
        }
    }
    public override void UpdateState(Pilot pilot)
    {
    }
    public override void FixedUpdateState(Pilot pilot)
    {
        if (cancelled)
            return;
        try
        {
            if (!manager.TryGetLoiter(drone, this, out GlobalPosition anchor, out Vector3 anchorVelocity,
                                       out float relativeUp, out float altitude, out float epoch, out float epochTime,
                                       out bool fixedPoint, out int revision, out string reason))
            {
                manager.Unavailable(drone, this, reason);
                return;
            }
            if (drone.autopilot is not AutopilotPlane autopilot)
            {
                manager.Fault(drone, this, "autopilot");
                return;
            }
            if (drone.rb == null)
            {
                manager.Fault(drone, this, "rb");
                return;
            }
            if (fixedPoint && revision != loiterRevision)
            {
                loiterRevision = revision;
                cruisingToLoiterPoint = true;
            }
            if (!fixedPoint)
                cruisingToLoiterPoint = false;
            GlobalPosition dronePosition = drone.transform.position.ToGlobalPosition();
            float horizontalDistance = new Vector3(anchor.x - dronePosition.x, 0f, anchor.z - dronePosition.z).magnitude;
            if (!LoiterCommandLogic.ShouldIngress(fixedPoint, horizontalDistance))
                cruisingToLoiterPoint = false;
            AircraftDefinition? definition = drone.definition as AircraftDefinition;
            float cornerSpeed = definition?.aircraftParameters.cornerSpeed ?? float.NaN;
            float orbitSpeed = LoiterCommandLogic.OrbitTargetSpeed(cornerSpeed);
            float theta = epoch + phase * Mathf.Deg2Rad + (orbitSpeed / 2200f) * (Time.fixedTime - epochTime);
            float aimTheta = theta + 25f * Mathf.Deg2Rad;
            Vector3 radial = new Vector3(Mathf.Cos(aimTheta), 0f, Mathf.Sin(aimTheta));
            Vector3 aimTangent = new Vector3(-Mathf.Sin(aimTheta), 0f, Mathf.Cos(aimTheta));
            GlobalPosition destination = cruisingToLoiterPoint ? anchor + Vector3.up * relativeUp : anchor + radial * 2200f + Vector3.up * relativeUp;
            Vector3 aimDirection = cruisingToLoiterPoint ? new Vector3(anchor.x - dronePosition.x, 0f, anchor.z - dronePosition.z).normalized : aimTangent;
            float along = Vector3.Dot(destination - dronePosition, aimDirection);
            float speed = Vector3.ProjectOnPlane(drone.rb.velocity, Vector3.up).magnitude;
            float cruise = definition?.aircraftParameters.cruiseThrottle ?? .65f;
            float throttle = cruisingToLoiterPoint ? Mathf.Clamp(cruise + (165f - speed) / 200f + along / 3000f, .45f, 1f)
                                                   : LoiterCommandLogic.OrbitThrottle(speed, cornerSpeed, cruise);
            Vector3 targetVelocity = cruisingToLoiterPoint ? anchorVelocity : anchorVelocity + aimTangent * orbitSpeed;
            if (!Finite(relativeUp) || !Finite(altitude) || !Finite(throttle) || !Finite(destination.x) ||
                !Finite(destination.y) || !Finite(destination.z) || !Finite(targetVelocity.x) ||
                !Finite(targetVelocity.y) || !Finite(targetVelocity.z))
            {
                manager.Fault(drone, this, "non_finite");
                return;
            }
            ControlInputs inputs = drone.GetInputs();
            inputs.throttle = throttle;
            inputs.brake = 0f;
            autopilot.AutoAim(destination, true, false, false, .45f, 60f, true, altitude, targetVelocity);
        }
        catch (Exception e)
        {
            manager.Fault(drone, this, e.GetType().Name);
        }
    }
    public override void LeaveState()
    {
        if (!cancelled)
            try
            {
                drone.GetInputs().brake = 0f;
            }
            catch
            {
            }
    }
    internal void Cancel() => cancelled = true;
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
