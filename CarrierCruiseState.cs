using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoyalWingman;

// This state deliberately delegates all authority checks to its Plugin-owned controller.
internal sealed class CarrierCruiseState : PilotBaseState
{
    private readonly CarrierCruiseController controller;
    private readonly Aircraft carrier;
    private readonly Vector3 heading;
    private readonly float speed;
    private readonly float radarAltitude;
    private bool cancelled;
    private bool left;
    private float nextTelemetry;
    private float pulseOffAt, nextPulseAt;
    private byte selectedCountermeasureIndex = 255;
    private bool countermeasureOn;
    private readonly HashSet<PersistentID> firedThreats = new HashSet<PersistentID>();

    internal CarrierCruiseState(CarrierCruiseController controller, Aircraft carrier, Vector3 heading, float speed,
                                float radarAltitude)
    {
        this.controller = controller;
        this.carrier = carrier;
        this.heading = heading;
        this.speed = speed;
        this.radarAltitude = radarAltitude;
    }

    internal Aircraft Carrier => carrier;
    internal bool Cancelled => cancelled;
    internal void Cancel()
    {
        cancelled = true;
        CountermeasureOff();
    }
    public override void EnterState(Pilot pilot)
    {
    }
    public override void UpdateState(Pilot pilot)
    {
    }
    public override void LeaveState()
    {
        if (left)
            return;
        left = true;
        cancelled = true;
        CountermeasureOff();
    }

    public override void FixedUpdateState(Pilot pilot)
    {
        if (cancelled)
        {
            CountermeasureOff();
            return;
        }
        try
        {
            if (!controller.TryOwns(carrier, pilot, this, out string reason))
            {
                controller.RuntimeInvalid(this, reason);
                return;
            }
            if (carrier.autopilot is not AutopilotTiltwing tiltwing)
            {
                controller.RuntimeInvalid(this, "autopilot_type");
                return;
            }
            // Hold the actual speed, heading, and radar altitude captured when the carrier lost player ownership.
            Vector3 position = carrier.transform.position;
            if (!Finite(position) || !Finite(heading) || !Finite(speed) || !Finite(radarAltitude))
            {
                controller.RuntimeInvalid(this, "non_finite");
                return;
            }
            Vector3 destination = position + heading * 4000f;
            float altitudeHold = radarAltitude;
            Vector3 aimDirection = heading;
            Vector3 targetVelocity = heading * speed;
            Missile? threat = null;
            try
            {
                threat = FindCurrentThreat();
                WeaponStation? station = null;
                int stationIndex = -1;
                if (threat != null && carrier.weaponStations != null && !firedThreats.Contains(threat.persistentID))
                    foreach (WeaponStation candidate in carrier.weaponStations)
                        if (CombatSupport.StationSafe(carrier, candidate, true) &&
                            CombatSupport.RequiredLineOfSight(carrier, threat, candidate) &&
                            CombatSupport.HudShoot(carrier, threat, candidate, false))
                        {
                            station = candidate;
                            stationIndex = carrier.weaponStations.IndexOf(candidate);
                            break;
                        }
                if (threat != null && station != null && carrier.weaponManager != null)
                {
                    carrier.weaponManager.currentWeaponStation = station;
                    carrier.weaponManager.ClearTargetList();
                    carrier.weaponManager.AddTargetList(threat);
                    firedThreats.Add(threat.persistentID);
                    pilot.Fire();
                    controller.DefenseLog(carrier, "carrier_ir_fire threat_pid=" + threat.persistentID + " seeker=" +
                                                 threat.GetSeekerType() + " station=" + stationIndex);
                }
                if (threat != null)
                {
                    Vector3 evade = Vector3.Cross((carrier.GlobalPosition() - threat.GetEvasionPoint()).normalized, Vector3.up);
                    if (Vector3.Dot(evade, carrier.transform.forward) < 0f)
                        evade *= -1f;
                    if (Finite(evade) && evade.sqrMagnitude > 0f)
                    {
                        destination = position + evade * 10000f;
                        altitudeHold = carrier.radarAlt;
                        aimDirection = Vector3.zero;
                        targetVelocity = Vector3.zero;
                    }
                }
            }
            catch
            {
                threat = null;
                destination = position + heading * 4000f;
                altitudeHold = radarAltitude;
                aimDirection = heading;
                targetVelocity = heading * speed;
            }
            if (!Finite(destination) || !Finite(targetVelocity))
            {
                controller.RuntimeInvalid(this, "guidance_non_finite");
                return;
            }
            // Tiltwing navigation is the sole flight-control writer for this state.
            tiltwing.AutoAim(destination.ToGlobalPosition(), altitudeHold, aimDirection, targetVelocity, true);
            if (cancelled)
            {
                CountermeasureOff();
                return;
            }
            carrier.GetInputs().brake = 0f;
            PulseCountermeasure(threat);
            if (Time.fixedTime >= nextTelemetry)
            {
                nextTelemetry = Time.fixedTime + 2f;
                Vector3 horizontal = Vector3.ProjectOnPlane(carrier.rb.velocity, Vector3.up);
                float actualSpeed = horizontal.magnitude;
                Vector3 actualHeading = horizontal.sqrMagnitude > .01f ? horizontal.normalized : heading;
                float headingError = Vector3.Angle(heading, actualHeading);
                controller.Telemetry(carrier, actualSpeed - speed, carrier.radarAlt - radarAltitude, headingError);
            }
        }
        catch (Exception e)
        {
            CountermeasureOff();
            controller.RuntimeInvalid(this, "fixed_" + e.GetType().Name);
        }
    }

    private Missile? FindCurrentThreat()
    {
        Missile? threat = null;
        float bestTti = float.PositiveInfinity;
        MissileWarning warning = carrier.GetMissileWarningSystem();
        HashSet<PersistentID> present = new HashSet<PersistentID>();
        if (warning != null)
            foreach (Missile missile in warning.knownMissiles)
            {
                if (!CombatSupport.ValidMissile(missile) || missile.rb == null)
                    continue;
                if (missile.targetID == carrier.persistentID)
                    present.Add(missile.persistentID);
                Vector3 delta = carrier.transform.position - missile.transform.position;
                float range = delta.magnitude;
                float closing = range > .1f
                                    ? Vector3.Dot(-delta.normalized, carrier.rb.velocity - missile.rb.velocity)
                                    : float.NaN;
                if (MissileDefenseLogic.TrySelectShorterSelfTargetedTti(missile.targetID == carrier.persistentID,
                                                                         range, closing, bestTti, out float tti))
                {
                    threat = missile;
                    bestTti = tti;
                }
            }
        firedThreats.RemoveWhere(pid => !present.Contains(pid));
        return threat;
    }
    private void PulseCountermeasure(Missile? threat)
    {
        float now = Time.fixedTime;
        if (countermeasureOn && now >= pulseOffAt)
            CountermeasureOff();
        if (countermeasureOn || now < nextPulseAt || carrier.countermeasureManager == null || threat == null)
            return;

        carrier.countermeasureManager.ChooseCountermeasure(threat);
        byte index = carrier.countermeasureManager.activeIndex;
        var countermeasure = index == 255 ? null : carrier.countermeasureManager.GetActiveCountermeasure();
        if (countermeasure == null || countermeasure.ammo <= 0)
            return;
        selectedCountermeasureIndex = index;
        carrier.Countermeasures(true, index);
        countermeasureOn = true;
        pulseOffAt = now + .1f;
        nextPulseAt = now + .5f;
    }

    private void CountermeasureOff()
    {
        if (!countermeasureOn)
            return;
        try
        {
            carrier.Countermeasures(false, selectedCountermeasureIndex);
        }
        catch
        {
        }
        finally
        {
            countermeasureOn = false;
            selectedCountermeasureIndex = 255;
        }
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
}
