using System;
using NuclearOption.Networking;
using UnityEngine;

namespace LoyalWingman;

internal sealed class CarrierCruiseController
{
    private readonly WingmanSessionContext context;
    private readonly Action<string> log;
    private readonly WingmanStatusReporter reporter;
    private Aircraft? cruiseCarrier;
    private CarrierCruiseState? cruiseState;

    internal CarrierCruiseController(WingmanSessionContext context, WingmanStatusReporter reporter)
    {
        this.context = context;
        log = context.Log;
        this.reporter = reporter;
    }

    internal bool IsCruisingCarrier(Aircraft aircraft) => cruiseCarrier == aircraft && cruiseState != null;
    internal void DefenseLog(Aircraft carrier, string text) => log("state=" + text + " home_pid=" + Pid(carrier));

    // Called only after Plugin verifies exact session Home departure and configured drone target identity.
    internal void TryInstall(Player player, Aircraft oldAircraft, Aircraft target)
    {
        // A different carrier must never inherit the previous state's ownership record.
        if (cruiseState != null)
        {
            if (cruiseCarrier == oldAircraft)
                return;
            Aircraft previous = cruiseCarrier!;
            if (!CancelAndRestore("replace_carrier"))
                log("state=carrier_cruise_replace_restore_failed pid=" + Pid(previous));
            if (cruiseState != null)
                Clear();
        }
        if (!InstallEligible(player, oldAircraft, target, out Pilot pilot, out Vector3 heading, out float speed,
                             out float altitude, out string reason))
        {
            Refuse(oldAircraft, reason);
            return;
        }
        CarrierCruiseState state = new CarrierCruiseState(this, oldAircraft, heading, speed, altitude);
        try
        {
            pilot.SwitchState(state);
            if (pilot.currentState != state)
            {
                ClassifyInstallFailure(oldAircraft, pilot, state, "postcondition");
                return;
            }
            cruiseCarrier = oldAircraft;
            cruiseState = state;
            log("state=carrier_cruise_enter pid=" + Pid(oldAircraft) + " target_speed=" + speed.ToString("F1") +
                " alt=" + altitude.ToString("F1") + " heading=" + Heading(heading));
            reporter.CarrierCruiseEntered(oldAircraft);
        }
        catch (Exception e)
        {
            ClassifyInstallFailure(oldAircraft, pilot, state, "switch_" + e.GetType().Name);
        }
    }

    internal bool TryOwns(Aircraft carrier, Pilot pilot, CarrierCruiseState state, out string reason)
    {
        reason = "";
        try
        {
            if (carrier != cruiseCarrier || state != cruiseState || state.Cancelled || pilot.currentState != state)
            {
                reason = "ownership";
                return false;
            }
            if (!RuntimeEligible(carrier, pilot, out reason))
                return false;
            return true;
        }
        catch (Exception e)
        {
            reason = "runtime_" + e.GetType().Name;
            return false;
        }
    }

    internal void RuntimeInvalid(CarrierCruiseState state, string reason)
    {
        if (state != cruiseState)
            return;
        CancelAndRestore(reason);
    }

    internal void Tick()
    {
        if (cruiseState == null || cruiseCarrier == null)
            return;
        Pilot? pilot = PilotOf(cruiseCarrier);
        if (pilot == null)
        {
            CancelAndRestore("pilot_missing");
            return;
        }
        if (!TryOwns(cruiseCarrier, pilot, cruiseState, out string reason))
        {
            CancelAndRestore(reason == "" ? "tick_invalid" : reason);
        }
    }

    // The switch backend can only target a carrier after this restores a fresh native AIHelo state.
    internal bool RestoreBeforeTargeting(Aircraft target, string reason)
    {
        if (target != cruiseCarrier || cruiseState == null)
            return true;
        if (!CancelAndRestore(reason))
            return false;
        Pilot? pilot = PilotOf(target);
        return pilot != null && pilot.currentState is AIHeloCombatState;
    }
    internal bool TryResumeAfterRtbCancel(Aircraft home)
    {
        Pilot? pilot = PilotOf(home);
        if (cruiseState != null || pilot == null || !RuntimeEligible(home, pilot, out _) || home.rb == null)
            return false;
        Vector3 horizontal = Vector3.ProjectOnPlane(home.rb.velocity, Vector3.up);
        Vector3 heading = horizontal.sqrMagnitude > .01f ? horizontal : Vector3.ProjectOnPlane(home.transform.forward, Vector3.up);
        if (!Finite(heading) || heading.sqrMagnitude < .01f)
            heading = Vector3.forward;
        else
            heading.Normalize();
        CarrierCruiseState state = new CarrierCruiseState(this, home, heading, horizontal.magnitude, home.radarAlt);
        try
        {
            pilot.SwitchState(state);
        }
        catch
        {
        }
        if (pilot.currentState != state)
        {
            state.Cancel();
            return false;
        }
        cruiseCarrier = home;
        cruiseState = state;
        return true;
    }

    internal void Cleanup(string reason)
    {
        if (cruiseState != null)
            CancelAndRestore("cleanup_" + reason);
    }

    internal void Telemetry(Aircraft carrier, float speedError, float altitudeError, float headingError)
    {
        log("state=carrier_cruise_telemetry pid=" + Pid(carrier) +
            " speed=" + Vector3.ProjectOnPlane(carrier.rb.velocity, Vector3.up).magnitude.ToString("F1") +
            " speed_error=" + speedError.ToString("F1") + " radar_alt=" + carrier.radarAlt.ToString("F1") +
            " alt_error=" + altitudeError.ToString("F1") + " heading_error=" + headingError.ToString("F1"));
    }

    private bool CancelAndRestore(string reason)
    {
        Aircraft? carrier = cruiseCarrier;
        CarrierCruiseState? state = cruiseState;
        if (carrier == null || state == null)
        {
            Clear();
            return true;
        }
        state.Cancel(); // no state can write inputs after this point.
        Pilot? pilot = PilotOf(carrier);
        if (pilot == null)
        {
            FallbackFailed(carrier, "pilot_missing_" + reason);
            return false;
        }
        if (pilot.currentState != state)
        {
            if (pilot.currentState is AIHeloCombatState)
            {
                Exit(carrier, reason);
                return true;
            }
            FallbackFailed(carrier, "state_mismatch_" + reason);
            return false;
        }
        AIHeloCombatState native = new AIHeloCombatState(pilot);
        try
        {
            pilot.AIHeloCombatState = native;
            pilot.SwitchState(native);
            if (pilot.currentState == native)
            {
                Exit(carrier, reason);
                return true;
            }
        }
        catch (Exception e)
        {
            if (pilot.currentState is AIHeloCombatState)
            {
                Exit(carrier, reason + "_exception_native");
                return true;
            }
            FallbackFailed(carrier, "restore_" + e.GetType().Name + "_" + reason);
            return false;
        }
        if (pilot.currentState is AIHeloCombatState)
        {
            Exit(carrier, reason + "_actual_native");
            return true;
        }
        FallbackFailed(carrier, "restore_postcondition_" + reason);
        return false;
    }

    private void ClassifyInstallFailure(Aircraft carrier, Pilot pilot, CarrierCruiseState state, string reason)
    {
        state.Cancel();
        if (pilot.currentState is AIHeloCombatState)
        {
            log("state=carrier_cruise_exit pid=" + Pid(carrier) + " reason=install_" + reason + "_native");
            return;
        }
        if (pilot.currentState == state)
        {
            cruiseCarrier = carrier;
            cruiseState = state;
            CancelAndRestore("install_" + reason);
            return;
        }
        FallbackFailed(carrier, "install_" + reason);
    }

    private bool InstallEligible(Player player, Aircraft oldAircraft, Aircraft target, out Pilot pilot,
                                 out Vector3 heading, out float speed, out float altitude, out string reason)
    {
        pilot = null!;
        heading = Vector3.zero;
        speed = 0f;
        altitude = 0f;
        reason = "";
        try
        {
            if (context.HasPlayerAssociation(oldAircraft))
            {
                reason = "player_association";
                return false;
            }
            if (GameManager.gameState != GameState.SinglePlayer || NetworkManagerNuclearOption.i == null ||
                !NetworkManagerNuclearOption.i.Server.Active || !oldAircraft.IsServer || !oldAircraft.LocalSim)
            {
                reason = "authority";
                return false;
            }
            if (!NativeRecoveryGuard.TryEvaluate(oldAircraft, out reason))
            {
                return false;
            }
            pilot = PilotOf(oldAircraft)!;
            if (pilot == null || pilot.pilotType != Pilot.PilotType.Tiltwing)
            {
                reason = "pilot_type";
                return false;
            }
            if (oldAircraft.autopilot is not AutopilotTiltwing)
            {
                reason = "autopilot_type";
                return false;
            }
            if (oldAircraft.rb == null || !Finite(oldAircraft.rb.velocity) || !Finite(oldAircraft.radarAlt))
            {
                reason = "non_finite";
                return false;
            }
            altitude = oldAircraft.radarAlt;
            Vector3 horizontal = Vector3.ProjectOnPlane(oldAircraft.rb.velocity, Vector3.up);
            speed = horizontal.magnitude;
            if (!Finite(horizontal) || !Finite(speed))
            {
                reason = "non_finite";
                return false;
            }
            heading = horizontal.sqrMagnitude > .01f
                          ? horizontal.normalized
                          : Vector3.ProjectOnPlane(oldAircraft.transform.forward, Vector3.up);
            if (!Finite(heading) || heading.sqrMagnitude < .01f)
                heading = Vector3.forward;
            else
                heading.Normalize();
            return true;
        }
        catch (Exception e)
        {
            reason = "install_" + e.GetType().Name;
            return false;
        }
    }

    private bool RuntimeEligible(Aircraft carrier, Pilot pilot, out string reason)
    {
        reason = "";
        if (context.HasPlayerAssociation(carrier))
        {
            reason = "player_reassociation";
            return false;
        }
        if (GameManager.gameState != GameState.SinglePlayer || NetworkManagerNuclearOption.i == null ||
            !NetworkManagerNuclearOption.i.Server.Active || !carrier.IsServer || !carrier.LocalSim)
        {
            reason = "authority";
            return false;
        }
        if (!NativeRecoveryGuard.TryEvaluate(carrier, out reason))
            return false;
        if (pilot.pilotType != Pilot.PilotType.Tiltwing || carrier.autopilot is not AutopilotTiltwing)
        {
            reason = "tiltwing_runtime";
            return false;
        }
        if (carrier.rb == null || !Finite(carrier.rb.velocity) || !Finite(carrier.radarAlt))
        {
            reason = "non_finite";
            return false;
        }
        return true;
    }

    private void Exit(Aircraft carrier, string reason)
    {
        log("state=carrier_cruise_exit pid=" + Pid(carrier) + " reason=" + reason);
        reporter.CarrierCruiseRestored(carrier);
        Clear();
    }
    private void FallbackFailed(Aircraft carrier, string reason)
    {
        try
        {
            carrier.GetInputs().brake = 0f;
        }
        catch
        {
        }
        log("state=carrier_cruise_fallback_failed pid=" + Pid(carrier) + " reason=" + reason);
        Clear();
    }
    private void Refuse(Aircraft carrier, string reason)
    {
        log("state=carrier_cruise_refuse pid=" + Pid(carrier) + " reason=" + reason);
    }
    private void Clear()
    {
        if (cruiseState != null)
            cruiseState.Cancel();
        cruiseState = null;
        cruiseCarrier = null;
    }
    private static Pilot? PilotOf(Aircraft aircraft) => aircraft.pilots != null
                                                        && aircraft.pilots.Length > 0 ? aircraft.pilots[0] : null;
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    private static string Pid(Aircraft aircraft)
    {
        try
        {
            return aircraft.persistentID.ToString();
        }
        catch
        {
            return "unavailable";
        }
    }
    private static string Heading(Vector3 heading) => heading.x.ToString("F2") + "," + heading.z.ToString("F2");
}
