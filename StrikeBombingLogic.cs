using System;
using System.Collections.Generic;

namespace LoyalWingman;

internal enum StrikeBombWeaponKind { None, Bomb, GlideBomb }
internal enum StrikeBombStage { BombVolley, GlideBombVolley, Complete }
internal enum StrikeRippleDecision { FireNext, ContinueIngress, BreakOff }

internal static class StrikeBombingLogic
{
    internal static string[] CopySortDistinctIds(IEnumerable<string> ids)
    {
        HashSet<string> distinct = new HashSet<string>(StringComparer.Ordinal);
        if (ids != null)
            foreach (string id in ids)
                if (!string.IsNullOrEmpty(id))
                    distinct.Add(id);
        List<string> sorted = new List<string>(distinct);
        sorted.Sort(StringComparer.Ordinal);
        return sorted.ToArray();
    }

    internal static bool IsBetterTarget(float candidateDistanceSq, string candidatePid, float bestDistanceSq,
                                        string bestPid)
    {
        if (!Finite(candidateDistanceSq) || candidateDistanceSq < 0f)
            return false;
        if (!Finite(bestDistanceSq) || bestDistanceSq < 0f)
            return true;
        return candidateDistanceSq < bestDistanceSq ||
               candidateDistanceSq == bestDistanceSq && string.CompareOrdinal(candidatePid, bestPid) < 0;
    }

    internal static StrikeBombStage Advance(StrikeBombStage stage, bool stageHasAmmo, bool stageReloading,
                                            bool stageSalvoInProgress) =>
        stage == StrikeBombStage.BombVolley ? stageHasAmmo || stageReloading || stageSalvoInProgress ? stage : StrikeBombStage.GlideBombVolley :
        stage == StrikeBombStage.GlideBombVolley && !stageHasAmmo && !stageReloading && !stageSalvoInProgress ? StrikeBombStage.Complete : stage;

    internal static bool CanAdvanceStage(bool releaseActive, bool breakOffActive) =>
        !releaseActive && !breakOffActive;


    internal static StrikeBombWeaponKind Classify(bool bomb, bool glideBomb, bool nuclear, bool missile, bool gun,
        bool laserGuided, bool cargo, bool sling, bool troops)
    {
        if (nuclear || missile || gun || laserGuided || cargo || sling || troops)
            return StrikeBombWeaponKind.None;
        return bomb ? StrikeBombWeaponKind.Bomb : glideBomb ? StrikeBombWeaponKind.GlideBomb : StrikeBombWeaponKind.None;
    }

    internal static bool IsBombReleaseWindow(float travelTime, float fallTime) =>
        Finite(travelTime) && Finite(fallTime) && MathF.Abs(travelTime - fallTime) < 1.5f;

    internal static StrikeRippleDecision ResolveConventionalRipple(bool stationReady, bool window, bool futureWindow,
        float forwardProjection, float targetAngle, float minAlignment) =>
        !stationReady || !Finite(forwardProjection) || forwardProjection <= 0f || !Finite(targetAngle) ||
        !Finite(minAlignment) || targetAngle >= minAlignment ? StrikeRippleDecision.BreakOff :
        window ? StrikeRippleDecision.FireNext : futureWindow ? StrikeRippleDecision.ContinueIngress : StrikeRippleDecision.BreakOff;
    internal static bool GlideGeometryValid(float distance, float minRange, bool obscured, float heightAboveTarget,
        float speed, float targetAngle, float minAlignment) =>
        Finite(distance) && Finite(minRange) && Finite(heightAboveTarget) && Finite(speed) && Finite(targetAngle) &&
        Finite(minAlignment) && distance >= minRange && !obscured && distance > 0f &&
        GlideReleaseGradient(heightAboveTarget, speed, distance) > .2f && targetAngle < minAlignment;
    internal static float GlideReleaseGradient(float heightAboveTarget, float speed, float distance) =>
        (heightAboveTarget + speed * speed * .03f) / distance;
    internal static string GlideGateReason(float distance, float minRange, bool obscured, float heightAboveTarget,
        float speed, float targetAngle, float minAlignment, bool stationReady, float elapsedSinceGlide)
    {
        if (!Finite(distance) || distance <= 0f) return "invalid_distance";
        if (!Finite(minRange)) return "invalid_min_range";
        if (!Finite(heightAboveTarget)) return "invalid_height";
        if (!Finite(speed)) return "invalid_speed";
        if (!Finite(targetAngle)) return "invalid_angle";
        if (!Finite(minAlignment)) return "invalid_min_alignment";
        if (distance < minRange) return "inside_min_range";
        if (obscured) return "obscured";
        if (!(GlideReleaseGradient(heightAboveTarget, speed, distance) > .2f)) return "insufficient_gradient";
        if (targetAngle >= minAlignment) return "misaligned";
        if (!stationReady) return "station_not_ready";
        return !Finite(elapsedSinceGlide) || elapsedSinceGlide <= 5f ? "glide_spacing" : "fire_ready";
    }
    internal static bool InitialGlideMissedWindow(bool hasPriorGlideRelease, float distance, float minRange) =>
        !hasPriorGlideRelease && Finite(distance) && Finite(minRange) && distance > 0f && distance < minRange;
    internal static bool GlideMissedWindowCanResume(float distance, float minRange) =>
        Finite(distance) && Finite(minRange) && distance >= minRange;
    internal static StrikeRippleDecision ResolveGlideRipple(bool geometryValid, bool stationReady, float elapsedSinceGlide) =>
        !geometryValid || !stationReady || !Finite(elapsedSinceGlide) ? StrikeRippleDecision.BreakOff :
        elapsedSinceGlide > 5f ? StrikeRippleDecision.FireNext : StrikeRippleDecision.ContinueIngress;
    internal static StrikeRippleDecision PreserveInitialIngress(StrikeRippleDecision decision, bool hasPendingRelease) =>
        decision == StrikeRippleDecision.BreakOff && !hasPendingRelease ? StrikeRippleDecision.ContinueIngress : decision;
    internal static bool NativeBombFreshBreakOff(float travelTime, float fallTime) =>
        Finite(travelTime) && Finite(fallTime) && travelTime < 10f && fallTime < 8f;
    internal static bool ConventionalCloseOffAxis(float range, float radarAltitude, float speed, float angle) =>
        Finite(range) && Finite(radarAltitude) && Finite(speed) && Finite(angle) &&
        range < 2000f + radarAltitude * .5f + speed * 3.6f && angle > 20f;
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
