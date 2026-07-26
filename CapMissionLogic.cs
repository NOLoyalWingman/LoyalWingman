namespace LoyalWingman;

internal readonly struct CapCandidate
{
    internal readonly string pid;
    internal readonly float x, z;
    internal readonly bool valid, hostile, tracked, airborne;
    internal CapCandidate(string pid, float x, float z, bool valid, bool hostile, bool tracked, bool airborne)
    { this.pid = pid; this.x = x; this.z = z; this.valid = valid; this.hostile = hostile; this.tracked = tracked; this.airborne = airborne; }
}

internal static class CapMissionLogic
{
    internal const float SearchRadius = 20000f;
    internal static bool IsWithinScope(float anchorX, float anchorZ, float x, float z)
    {
        float dx = x - anchorX, dz = z - anchorZ;
        return float.IsFinite(dx) && float.IsFinite(dz) && dx * dx + dz * dz <= SearchRadius * SearchRadius;
    }
    internal static bool IsEligible(CapCandidate candidate) => candidate.valid && candidate.hostile && candidate.tracked && candidate.airborne;
    internal static bool ShouldKeepTarget(CapCandidate candidate, bool retaliation, bool hasAam, float anchorX, float anchorZ) =>
        hasAam && IsEligible(candidate) && (retaliation || IsWithinScope(anchorX, anchorZ, candidate.x, candidate.z));
    internal static bool IsBetterCandidate(CapCandidate candidate, float anchorX, float anchorZ, CapCandidate best)
    {
        float dx = candidate.x - anchorX, dz = candidate.z - anchorZ;
        float bestDx = best.x - anchorX, bestDz = best.z - anchorZ;
        float distance = dx * dx + dz * dz, bestDistance = bestDx * bestDx + bestDz * bestDz;
        return distance < bestDistance || distance == bestDistance && string.CompareOrdinal(candidate.pid, best.pid) < 0;
    }
    internal static WingmanDesired ResolveDesired(bool combatEnabled, bool hasTarget) =>
        combatEnabled && hasTarget ? WingmanDesired.AirToAir : WingmanDesired.Loiter;
}
