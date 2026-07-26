using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoyalWingman;

// Local-only HUD status messages. This intentionally does not use chat or network APIs.
internal sealed class WingmanStatusReporter
{
    private const float CooldownSeconds = 2.5f;
    private readonly Func<bool> enabled;
    private readonly Action<string> log;
    private readonly Dictionary<string, float> recent = new Dictionary<string, float>();
    private readonly HashSet<string> combatDisabledReasons = new HashSet<string>();
    private Func<Aircraft?, string?>? managedCallsignResolver;
    private bool leaderMissingReported;

    internal WingmanStatusReporter(Func<bool> enabled, Action<string> log)
    {
        this.enabled = enabled;
        this.log = log;
    }
    internal void SetManagedCallsignResolver(Func<Aircraft?, string?> resolver) => managedCallsignResolver = resolver;

    internal void DroneStateStarted(Aircraft drone, WingmanMode mode)
    {
        if (!IsCurrentPlayerAircraft(drone))
            Report("drone_started_" + Pid(drone),
                   "[" + Callsign(drone) + "] " + (mode == WingmanMode.Follow ? "Following lead." : "Loitering."));
    }
    internal void ModeChanged(WingmanMode mode)
    {
        if (mode == WingmanMode.Follow)
            leaderMissingReported = false;
        Report(mode == WingmanMode.Loiter ? "mode_loiter" : "mode_follow",
               "[Wingmen] " + (mode == WingmanMode.Follow ? "Following lead." : "Loitering."));
    }
    internal void LeaderMissing()
    {
        if (leaderMissingReported)
            return;
        leaderMissingReported = true;
        Report("leader_missing", "[Wingmen] Lead lost. Holding orbit.");
    }
    internal void NativeFallback(Aircraft drone)
    {
        if (!IsCurrentPlayerAircraft(drone))
            Report("native_fallback_" + Pid(drone), "[" + Callsign(drone) + "] Leaving formation control.");
    }
    internal void LeaderChanged(Aircraft leader)
    {
        leaderMissingReported = false;
        Report("leader_changed", "[Wingmen] New lead: " + Callsign(leader) + ".");
    }
    internal void EmergencyReturned() => Report("emergency_returned", "[Wingmen] Returned to lead.");
    internal void CombatEntered(Aircraft drone, Aircraft target) => Report("combat_enter_" + Pid(target),
                                                           Callsign(drone) + " attacking designated " + SafeTarget(target));
    internal void A2aHoldFire() => Report("a2a_hold_fire", "[A2A] HOLD FIRE: no suitable missile solution.");
    internal void TargetDesignated(Aircraft target) => Report("target_designated_" + Pid(target),
                                                               "[Wingmen] Target designated: " + SafeTarget(target));
    internal void TargetCleared() => Report("target_cleared", "[Wingmen] Target cleared.");
    internal void TargetInvalidated() => Report("target_invalidated", "[Wingmen] Target lost.");
    internal void MissionTargetDesignated(WingmanMission mission, Unit target) =>
        Report("mission_target_designated_" + mission + "_" + Pid(target),
               "[" + MissionName(mission) + "] Target designated: " + SafeTarget(mission, target));
    internal void MissionTargetCleared(WingmanMission mission, bool invalidated) =>
        Report("mission_target_" + (invalidated ? "invalidated_" : "cleared_") + mission,
               "[" + MissionName(mission) + "] Target " + (invalidated ? "lost." : "cleared."));
    internal void MissionEntered(Aircraft drone, WingmanMission mission, Unit target) =>
        Report("mission_enter_" + mission + "_" + Pid(drone),
               "[" + Callsign(drone) + "] " + MissionName(mission) + " engaged.");
    internal void NoEnemyAircraftSelected() => Report("target_rejected_none", "[Wingmen] No enemy aircraft selected.");
    internal void InvalidHostileSelection() => Report("target_rejected_hostile",
                                                      "[Wingmen] Selected aircraft is not a valid hostile.");
    internal void TargetConflict() => Report("target_conflict", "[Wingmen] Clear the current target first.");
    internal void MissileDefenseEntered(Aircraft drone) => Report("missile_defense_" + Pid(drone),
                                                                   Callsign(drone) + " defending against incoming missile");
    internal void MissileDefenseExited(Aircraft drone) => Report("missile_defense_exit_" + Pid(drone),
                                                                  Callsign(drone) + " missile defense complete.");
    internal void MissileDefenseReplaced(Aircraft drone) => Report("missile_defense_replace_" + Pid(drone),
                                                                    Callsign(drone) + " engaging new missile threat.");
    internal void MissileAway(Aircraft drone) => Report("combat_missile_away_" + Pid(drone),
                                                         Callsign(drone) + " missile away");
    internal void Returning(Aircraft drone) => Report("combat_returning_" + Pid(drone),
                                                       Callsign(drone) + " returning to formation");
    internal void Rejoined(Aircraft drone) => Report("combat_rejoined_" + Pid(drone),
                                                      Callsign(drone) + " rejoined formation");
    internal void CombatDisabled(string reason)
    {
        string shortReason = SafeReason(reason);
        if (!combatDisabledReasons.Add(shortReason))
            return;
        Report("combat_disabled_" + shortReason, "FQ-106 combat disabled for this mission: " + shortReason);
    }
    internal void CombatAborted(Aircraft drone, string reason)
    {
        string shortReason = SafeReason(reason);
        Report("combat_aborted_" + Pid(drone) + "_" + shortReason,
               Callsign(drone) + " combat aborted: " + shortReason);
    }
    internal void CarrierCruiseEntered(Aircraft carrier) => Report("carrier_cruise_enter_" + Pid(carrier),
                                                                   "[Carrier] Cruise control engaged.");
    internal void CarrierCruiseRestored(Aircraft carrier) => Report("carrier_cruise_restored_" + Pid(carrier),
                                                                    "[Carrier] Native control restored.");

    internal void Cleanup()
    {
        recent.Clear();
        combatDisabledReasons.Clear();
        leaderMissingReported = false;
    }

    private void Report(string key, string message)
    {
        float now = Time.realtimeSinceStartup;
        if (recent.TryGetValue(key, out float previous) && now - previous < CooldownSeconds)
            return;
        recent[key] = now;

        if (GameManager.gameState != GameState.SinglePlayer || !enabled())
        {
            log("state=status_message key=" + key + " ui=disabled");
            return;
        }
        try
        {
            // Fetch every time: GameplayUI belongs to the currently loaded scene.
            GameplayUI? ui = SceneSingleton<GameplayUI>.i;
            if (ui == null)
            {
                log("state=status_message key=" + key + " ui=unavailable");
                return;
            }
            ui.GameMessage(message);
            log("state=status_message key=" + key + " ui=shown");
        }
        catch (Exception e)
        {
            log("state=status_message key=" + key + " ui=failed reason=" + e.GetType().Name);
        }
    }

    private string Callsign(Aircraft? aircraft)
    {
        try
        {
            string? managedLabel = managedCallsignResolver?.Invoke(aircraft);
            if (!string.IsNullOrWhiteSpace(managedLabel)) return managedLabel;
            if (aircraft != null && !string.IsNullOrWhiteSpace(aircraft.unitName))
                return aircraft.unitName;
            if (aircraft != null && aircraft.definition != null && !string.IsNullOrWhiteSpace(aircraft.definition.name))
                return aircraft.definition.name;
            if (aircraft != null && aircraft.definition != null &&
                !string.IsNullOrWhiteSpace(aircraft.definition.jsonKey))
                return aircraft.definition.jsonKey;
        }
        catch
        {
        }
        return "FQ-106";
    }

    private string SafeTarget(Aircraft? aircraft)
    {
        string value = Callsign(aircraft);
        if (string.IsNullOrWhiteSpace(value) || value == "FQ-106")
            return "hostile aircraft";
        return TrimSafe(value, 32);
    }
    private static string SafeTarget(WingmanMission mission, Unit? target)
    {
        try
        {
            if (target != null && !string.IsNullOrWhiteSpace(target.unitName))
                return TrimSafe(target.unitName, 32);
            if (target != null && target.definition != null && !string.IsNullOrWhiteSpace(target.definition.name))
                return TrimSafe(target.definition.name, 32);
            if (target != null && target.definition != null && !string.IsNullOrWhiteSpace(target.definition.jsonKey))
                return TrimSafe(target.definition.jsonKey, 32);
        }
        catch
        {
        }
        return mission == WingmanMission.Sead ? "radar emitter"
             : mission == WingmanMission.AntiShip ? "surface ship"
             : "hostile aircraft";
    }
    private static string MissionName(WingmanMission mission) => mission == WingmanMission.Sead ? "SEAD"
                                                       : mission == WingmanMission.AntiShip ? "ASUW" : "A2A";
    private static string SafeReason(string? value) => TrimSafe(string.IsNullOrWhiteSpace(value) ? "safety check"
                                                                                                 : value,
                                                                32);
    private static string TrimSafe(string value, int limit)
    {
        try
        {
            char[] buffer = new char[Math.Min(value.Length, limit)];
            int count = 0;
            foreach (char c in value)
            {
                if (count == buffer.Length)
                    break;
                if (char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '-')
                    buffer[count++] = c;
            }
            string result = new string(buffer, 0, count).Trim();
            return string.IsNullOrWhiteSpace(result) ? "safety check" : result;
        }
        catch
        {
            return "safety check";
        }
    }

    private static string Pid(Aircraft? aircraft)
    {
        try
        {
            return aircraft == null ? "none" : aircraft.persistentID.ToString();
        }
        catch
        {
            return "unavailable";
        }
    }

    private static string Pid(Unit? unit)
    {
        try
        {
            return unit == null ? "none" : unit.persistentID.ToString();
        }
        catch
        {
            return "unavailable";
        }
    }

    private static bool IsCurrentPlayerAircraft(Aircraft aircraft)
    {
        try
        {
            return GameManager.GetLocalAircraft(out Aircraft local) && local == aircraft;
        }
        catch
        {
            return false;
        }
    }
}
