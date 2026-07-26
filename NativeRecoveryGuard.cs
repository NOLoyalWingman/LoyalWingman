using System;

namespace LoyalWingman;

// Target-only guard for irreversible native ejection/recovery transitions; its result is the shared boundary before
// native control is touched.
internal static class NativeRecoveryGuard
{
    internal static bool TryEvaluate(Aircraft aircraft, out string reason)
    {
        reason = "native_recovery_unavailable";
        try
        {
            if (aircraft == null || !aircraft.gameObject.activeInHierarchy)
                return Reject("native_recovery_inactive", out reason);
            if (aircraft.unitState == Unit.UnitState.Abandoned)
                return Reject("native_recovery_abandoned", out reason);
            if (aircraft.unitState == Unit.UnitState.Returned)
                return Reject("native_recovery_returned", out reason);
            if (aircraft.HasEjected())
                return Reject("native_recovery_ejected", out reason);
            if (aircraft.disabled)
                return Reject("native_recovery_disabled", out reason);
            if (aircraft.Identity == null)
                return Reject("native_recovery_identity_null", out reason);
            if (!aircraft.Identity.IsSpawned)
                return Reject("native_recovery_identity_not_spawned", out reason);
            if (aircraft.Identity.NetId == 0)
                return Reject("native_recovery_identity_netid_zero", out reason);
            if (aircraft.persistentID.Equals(default(PersistentID)))
                return Reject("native_recovery_persistent_id_default", out reason);
            if (!UnitRegistry.TryGetPersistentUnit(aircraft.persistentID, out PersistentUnit unit) || unit == null ||
                unit.unit != aircraft)
                return Reject("native_recovery_registry_mismatch", out reason);
            if (aircraft.pilots == null || aircraft.pilots.Length == 0 || aircraft.pilots[0] == null)
                return Reject("native_recovery_pilot_missing", out reason);
            if (aircraft.NetworkHQ == null)
                return Reject("native_recovery_hq_missing", out reason);
            reason = "ok";
            return true;
        }
        catch
        {
            return Reject("native_recovery_unavailable", out reason);
        }
    }

    internal static string Describe(Aircraft aircraft)
    {
        try
        {
            if (aircraft == null)
                return "target=null";
            string key = aircraft.definition == null ? "unknown" : aircraft.definition.jsonKey;
            string pid =
                aircraft.persistentID.Equals(default(PersistentID)) ? "none" : aircraft.persistentID.ToString();
            string state = aircraft.pilots != null && aircraft.pilots.Length > 0 && aircraft.pilots[0] != null &&
                                   aircraft.pilots[0].currentState != null
                               ? aircraft.pilots[0].currentState.GetType().Name
                               : "null";
            return "target=" + key + "/" + pid + " stateType=" + state + " ejected=" + aircraft.HasEjected() +
                   " unitState=" + aircraft.unitState + " disabled=" + aircraft.disabled;
        }
        catch
        {
            return "target=unavailable";
        }
    }

    private static bool Reject(string value, out string reason)
    {
        reason = value;
        return false;
    }
}
