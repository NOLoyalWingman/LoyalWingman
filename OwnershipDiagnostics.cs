using System;
using BepInEx.Logging;
using NuclearOption.Networking;

namespace LoyalWingman;

// Diagnostic-only: snapshots bracket ownership postconditions, and every read is isolated so a destroyed Unity object
// cannot affect switching.
internal static class OwnershipDiagnostics
{
    internal static void Snapshot(ManualLogSource log, string phase, string reason, Player current,
                                  Aircraft oldAircraft, Aircraft target)
    {
        try
        {
            log.LogInfo("[LoyalWingman] state=ownership_snapshot phase=" + phase + " reason=" + reason +
                        " player_aircraft=" + ReadPlayerAircraftLabel(current) + " local_aircraft=" +
                        ReadLocalAircraftLabel() + " hud_aircraft=" + ReadHudAircraftLabel() + " old={" +
                        Describe(oldAircraft, current) + "}" + " target={" + Describe(target, current) + "}");
        }
        catch
        { /* Diagnostics must never alter switching. */
        }
    }

    private static string Describe(Aircraft aircraft, Player current)
    {
        try
        {
            if (aircraft == null)
                return "aircraft=null";
            string pilot = "unavailable";
            try
            {
                pilot =
                    PlayerRelation(aircraft.pilots != null && aircraft.pilots.Length > 0 && aircraft.pilots[0] != null
                                       ? aircraft.pilots[0].player
                                       : null,
                                   current);
            }
            catch
            {
            }
            string persistent = "unavailable";
            try
            {
                persistent =
                    UnitRegistry.TryGetPersistentUnit(aircraft.persistentID, out PersistentUnit unit) && unit != null
                        ? PlayerRelation(unit.player, current)
                        : "missing";
            }
            catch
            {
            }
            return "key=" + SafeKey(aircraft) + " pid=" + SafePersistentId(aircraft) +
                   " player=" + PlayerRelation(aircraft.Player, current) + " pilot=" + pilot +
                   " owner=" + OwnerRelation(aircraft, current) + " persistent=" + persistent +
                   " registered=" + SafeRegistered(aircraft) + " disabled=" + SafeDisabled(aircraft);
        }
        catch
        {
            return "aircraft=unavailable";
        }
    }

    private static string AircraftLabel(Aircraft aircraft) => aircraft == null ? "null"
                                                                               : SafeKey(aircraft) + "/" +
                                                                                     SafePersistentId(aircraft);
    private static string ReadPlayerAircraftLabel(Player player)
    {
        try
        {
            return player == null ? "null" : AircraftLabel(player.Aircraft);
        }
        catch
        {
            return "unavailable";
        }
    }
    private static string ReadLocalAircraftLabel()
    {
        try
        {
            return GameManager.GetLocalAircraft(out Aircraft aircraft) ? AircraftLabel(aircraft) : "null";
        }
        catch
        {
            return "unavailable";
        }
    }
    private static string ReadHudAircraftLabel()
    {
        try
        {
            return CombatHUD.i == null ? "null" : AircraftLabel(CombatHUD.i.aircraft);
        }
        catch
        {
            return "unavailable";
        }
    }
    private static string SafeKey(Aircraft aircraft)
    {
        try
        {
            return aircraft.definition == null ? "unknown" : aircraft.definition.jsonKey;
        }
        catch
        {
            return "unavailable";
        }
    }
    private static string SafePersistentId(Aircraft aircraft)
    {
        try
        {
            return aircraft.persistentID.Equals(default(PersistentID)) ? "none" : aircraft.persistentID.ToString();
        }
        catch
        {
            return "unavailable";
        }
    }
    private static string SafeRegistered(Aircraft aircraft)
    {
        try
        {
            return UnitRegistry.TryGetPersistentUnit(aircraft.persistentID, out PersistentUnit unit) && unit != null &&
                           unit.unit == aircraft
                       ? "true"
                       : "false";
        }
        catch
        {
            return "unavailable";
        }
    }
    private static string SafeDisabled(Aircraft aircraft)
    {
        try
        {
            return aircraft.disabled ? "true" : "false";
        }
        catch
        {
            return "unavailable";
        }
    }
    private static string PlayerRelation(Player? value, Player? current)
    {
        try
        {
            return value == null ? "null" : value == current ? "current" : "other";
        }
        catch
        {
            return "unavailable";
        }
    }
    private static string OwnerRelation(Aircraft aircraft, Player current)
    {
        try
        {
            if (aircraft.Identity == null || aircraft.Identity.Owner == null)
                return "null";
            if (current == null || current.Identity == null || current.Identity.Owner == null)
                return "other";
            return aircraft.Identity.Owner == current.Identity.Owner ? "current" : "other";
        }
        catch
        {
            return "unavailable";
        }
    }
}
