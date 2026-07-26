using System;
using System.Collections.Generic;

namespace LoyalWingman;

internal static class MountedDroneLoadoutLogic
{
    internal static bool TryCandidateValue(int hardpointCount, float emptyCost, float costPerRound, int ammo,
                                           out float value)
    {
        value = 0f;
        if (hardpointCount < 0 || ammo < 0 || !Finite(emptyCost) || !Finite(costPerRound) || emptyCost < 0f ||
            costPerRound < 0f)
            return false;
        value = hardpointCount * (emptyCost + costPerRound * ammo);
        return Finite(value) && value >= 0f;
    }

    internal static bool TryAccumulateCandidateValue(float total, int hardpointCount, float emptyCost,
                                                     float costPerRound, int ammo, out float value)
    {
        value = 0f;
        if (!Finite(total) || total < 0f || !TryCandidateValue(hardpointCount, emptyCost, costPerRound, ammo,
                out float candidate))
            return false;
        value = total + candidate;
        return Finite(value) && value >= 0f;
    }

    internal static bool TryQuote(float baseline, float currentPaid, float newValue, float funds,
                                  out float newSurcharge, out float signedDelta, out float consumed,
                                  out float refunded, out float fundsAfter)
    {
        newSurcharge = signedDelta = consumed = refunded = fundsAfter = 0f;
        if (!ValidNonNegative(baseline) || !ValidNonNegative(currentPaid) || !ValidNonNegative(newValue) ||
            !ValidNonNegative(funds))
            return false;
        newSurcharge = MathF.Max(0f, newValue - baseline);
        if (!ValidNonNegative(newSurcharge)) return false;
        signedDelta = newSurcharge - currentPaid;
        if (!Finite(signedDelta)) return false;
        consumed = MathF.Max(0f, signedDelta);
        refunded = MathF.Max(0f, -signedDelta);
        if (!Finite(consumed) || !Finite(refunded) || refunded > currentPaid || consumed > funds)
            return false;
        fundsAfter = funds - consumed + refunded;
        return ValidNonNegative(fundsAfter);
    }

    internal static bool TryNormalizeKey(string? key, out string normalized)
    {
        normalized = key ?? string.Empty;
        return normalized.Length == 0 || !string.IsNullOrWhiteSpace(normalized);
    }

    internal static bool TrySnapshotLegalKeys(IEnumerable<string?>? keys, out string[] snapshot)
    {
        snapshot = Array.Empty<string>();
        if (keys == null) return false;
        SortedSet<string> sorted = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string? key in keys)
        {
            if (!TryNormalizeKey(key, out string normalized)) return false;
            if (normalized.Length > 0) sorted.Add(normalized);
        }
        snapshot = new string[sorted.Count + 1];
        snapshot[0] = string.Empty;
        int index = 1;
        foreach (string key in sorted) snapshot[index++] = key;
        return true;
    }

    internal static bool IsAmbiguousDuplicateKey(string? existingKey, string? candidateKey,
                                                 bool mapsToDifferentMounts) =>
        mapsToDifferentMounts && TryNormalizeKey(existingKey, out string existing) &&
        TryNormalizeKey(candidateKey, out string candidate) &&
        string.Equals(existing, candidate, StringComparison.Ordinal);

    internal static bool SlotCountMatches(int requestedSlots, int actualSlots) =>
        requestedSlots >= 0 && actualSlots >= 0 && requestedSlots == actualSlots;

    internal static bool RevisionMatches(long expectedRevision, long currentRevision) =>
        expectedRevision >= 0 && currentRevision >= 0 && expectedRevision == currentRevision;

    internal static bool TryNextRevision(long currentRevision, bool applySucceeded, out long nextRevision)
    {
        nextRevision = currentRevision;
        if (!applySucceeded || currentRevision < 0 || currentRevision == long.MaxValue) return false;
        nextRevision = currentRevision + 1;
        return true;
    }

    private static bool ValidNonNegative(float value) => Finite(value) && value >= 0f;
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
