using System.Collections.Generic;

namespace LoyalWingman;

internal enum DroneModeOverride
{
    Auto = 0,
    Follow = 1,
    Loiter = 2
}

internal enum WingmanDesired
{
    Follow,
    Loiter,
    AirToAir,
    Sead,
    AntiShip,
    Cas,
    Strike
}

internal enum WingmanMission
{
    None,
    AirToAir,
    Sead,
    AntiShip,
    Cas,
    Strike,
    Cap
}

internal static class CombatCommandLogic
{
    internal static bool MatchesTarget(string? assignedId, string targetId)
    {
        return !string.IsNullOrEmpty(assignedId) && !string.IsNullOrEmpty(targetId) &&
               string.Equals(assignedId, targetId, System.StringComparison.Ordinal);
    }

    internal static bool IsSingleTargetProjection(IReadOnlyList<string?> targetIds, out string? targetId)
    {
        targetId = null;
        if (targetIds == null || targetIds.Count == 0 || string.IsNullOrEmpty(targetIds[0]))
            return false;
        targetId = targetIds[0];
        for (int i = 1; i < targetIds.Count; i++)
            if (string.IsNullOrEmpty(targetIds[i]) || !string.Equals(targetId, targetIds[i], System.StringComparison.Ordinal))
            {
                targetId = null;
                return false;
            }
        return true;
    }

    internal static bool IsBetterOpportunity(float candidate, int candidateIndex, float best, int bestIndex)
    {
        return candidate > best || (candidate == best && candidateIndex < bestIndex);
    }
    internal static bool IsSupportedSeeker(bool hasIr, bool hasArh, bool irOnly)
    {
        return hasIr || (!irOnly && hasArh);
    }
    internal static bool IsAirToAirStation(bool missile, bool hasIr, bool hasArh) =>
        missile && (hasIr || hasArh);
    internal static bool UsesAirToAirWeapons(WingmanMission mission) =>
        mission == WingmanMission.AirToAir || mission == WingmanMission.Cap;
    internal static bool TryGetCommonMission(IReadOnlyList<WingmanMission> missions, out WingmanMission mission)
    {
        mission = WingmanMission.None;
        if (missions.Count == 0 || missions[0] == WingmanMission.None)
            return false;
        mission = missions[0];
        for (int i = 1; i < missions.Count; i++)
            if (missions[i] == WingmanMission.None || missions[i] != mission)
            {
                mission = WingmanMission.None;
                return false;
            }
        return true;
    }

    internal static WingmanDesired ResolveDesired(bool combatEnabled, WingmanMission mission, bool targetReady,
                                                  DroneModeOverride modeOverride, bool autoFollow)
    {
        if (combatEnabled && targetReady)
            switch (mission)
            {
                case WingmanMission.AirToAir:
                    return WingmanDesired.AirToAir;
                case WingmanMission.Cap:
                    return WingmanDesired.AirToAir;
                case WingmanMission.Sead:
                    return WingmanDesired.Sead;
                case WingmanMission.AntiShip:
                    return WingmanDesired.AntiShip;
                case WingmanMission.Cas:
                    return WingmanDesired.Cas;
                case WingmanMission.Strike:
                    return WingmanDesired.Strike;
            }
        if (modeOverride == DroneModeOverride.Follow)
            return WingmanDesired.Follow;
        if (modeOverride == DroneModeOverride.Loiter)
            return WingmanDesired.Loiter;
        return autoFollow ? WingmanDesired.Follow : WingmanDesired.Loiter;
    }
}
