using System.Globalization;

namespace LoyalWingman;

internal static class WingmanGroupLogic
{
    internal const int GroupSize = 4;
    internal static string FqPresentationLabel(int slotOrdinal) =>
        "FQ-" + (slotOrdinal + 1).ToString("00", CultureInfo.InvariantCulture);
}

internal readonly struct LogicalPlacement
{
    internal readonly int Ordinal;
    internal readonly int GroupId;
    internal readonly int GroupSlot;

    internal LogicalPlacement(int ordinal)
    {
        Ordinal = ordinal;
        GroupId = ordinal / WingmanGroupLogic.GroupSize;
        GroupSlot = ordinal % WingmanGroupLogic.GroupSize;
    }
}

// Session-local presentation placement. Physical release topology is intentionally absent.
internal sealed class LogicalPlacementAllocator
{
    private int nextOrdinal;

    internal LogicalPlacement Preview() => new LogicalPlacement(nextOrdinal);

    internal bool TryCommit(LogicalPlacement placement)
    {
        if (placement.Ordinal != nextOrdinal)
            return false;
        nextOrdinal++;
        return true;
    }
}
