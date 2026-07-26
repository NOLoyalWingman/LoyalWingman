using System;
namespace LoyalWingman;

internal enum MissileDefensePhase
{
    Maneuver,
    TerminalAcquire,
    FireAttempt,
    ResumeManeuver,
    Exit
}
internal enum MissileSeekerType
{
    Ir,
    Radar,
    Unknown
}
internal enum AntiShipDefenseArbitration { None, PreserveGenericDefense, DelegateRadarToAntiShip }
internal readonly struct RadarBeam
{
    internal readonly float x, z, error;
    internal readonly int side;
    internal RadarBeam(float x, float z, float error, int side)
    {
        this.x = x;
        this.z = z;
        this.error = error;
        this.side = side;
    }
}

internal static class MissileDefenseLogic
{
    internal static MissileSeekerType ClassifySeekerType(string? seekerType) =>
        seekerType == "IR" ? MissileSeekerType.Ir
      : seekerType == "ARH" || seekerType == "SARH" ? MissileSeekerType.Radar
                                                     : MissileSeekerType.Unknown;
    internal static bool ShouldPulseRadarCountermeasure(float tti, float alignmentDegrees) =>
        float.IsFinite(tti) && float.IsFinite(alignmentDegrees) && tti < 8f && alignmentDegrees < 20f;
    internal static bool ShouldPulseAntiShipRadarCountermeasure(float tti) => float.IsFinite(tti) && tti > 0f && tti < 8f;
    internal static AntiShipDefenseArbitration ArbitrateAntiShipDefense(bool antiShipMission,
                                                                         bool currentDefenseActive,
                                                                         MissileSeekerType currentSeeker,
                                                                         MissileSeekerType candidateSeeker)
    {
        if (!antiShipMission || candidateSeeker != MissileSeekerType.Radar)
            return AntiShipDefenseArbitration.None;
        return currentDefenseActive && currentSeeker != MissileSeekerType.Radar
            ? AntiShipDefenseArbitration.PreserveGenericDefense
            : AntiShipDefenseArbitration.DelegateRadarToAntiShip;
    }
    internal static bool ShouldTerminalClimb(float tti) => tti < 2f;
    internal static bool TrySelectShorterSelfTargetedTti(bool targetsSelf, float range, float closing, float bestTti,
                                                         out float tti)
    {
        tti = float.PositiveInfinity;
        if (!targetsSelf || !float.IsFinite(range) || !float.IsFinite(closing) || range <= .1f || closing <= 0f)
            return false;
        tti = range / closing;
        return float.IsFinite(tti) && tti >= 0f && tti < bestTti;
    }

    internal static bool ShouldEnter(string? currentThreatId, string candidateThreatId)
    {
        return currentThreatId == null || currentThreatId != candidateThreatId;
    }

    internal static MissileDefensePhase AdvancePhase(MissileDefensePhase current, float tti, float maneuverTime = 2.5f)
    {
        if (current == MissileDefensePhase.Maneuver)
            return IsManeuverFeasible(tti, maneuverTime) ? current : MissileDefensePhase.TerminalAcquire;

        if (current == MissileDefensePhase.TerminalAcquire)
            return MissileDefensePhase.FireAttempt;

        return current;
    }
    internal static bool IsManeuverFeasible(float tti,
                                            float maneuverTime) => float.IsInfinity(tti) || tti >= maneuverTime + 2.5f;
    internal static float RadarManeuverTime(float headingError) => .75f + headingError / 12f;
    internal static RadarBeam ChooseRadarBeam(float headingX, float headingZ, float bearingX, float bearingZ)
    {
        float hm = (float)Math.Sqrt(headingX * headingX + headingZ * headingZ),
              bm = (float)Math.Sqrt(bearingX * bearingX + bearingZ * bearingZ);
        if (!float.IsFinite(hm) || hm < .0001f)
        {
            headingX = 1f;
            headingZ = 0f;
            hm = 1f;
        }
        if (!float.IsFinite(bm) || bm < .0001f)
        {
            bearingX = 1f;
            bearingZ = 0f;
            bm = 1f;
        }
        headingX /= hm;
        headingZ /= hm;
        bearingX /= bm;
        bearingZ /= bm;
        float leftX = bearingZ, leftZ = -bearingX, rightX = -leftX, rightZ = -leftZ;
        float left = (float)(Math.Acos(Math.Clamp(headingX * leftX + headingZ * leftZ, -1f, 1f)) * 180d / Math.PI);
        float right = (float)(Math.Acos(Math.Clamp(headingX * rightX + headingZ * rightZ, -1f, 1f)) * 180d / Math.PI);
        return left <= right ? new RadarBeam(leftX, leftZ, left, 1) : new RadarBeam(rightX, rightZ, right, -1);
    }
}
