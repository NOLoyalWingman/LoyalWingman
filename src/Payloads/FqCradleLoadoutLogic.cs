namespace LoyalWingman;

internal sealed class CarrierFqLoadoutPlan
{
    private readonly string?[][] rows;
    internal int CradleCount { get; }
    internal int PhysicalRoundsPerCradle => FqCradleLoadoutLogic.PhysicalRoundsPerCradle;
    internal int HardpointCount { get; }
    private CarrierFqLoadoutPlan(string?[][] rows, int cradleCount, int hardpointCount)
    { this.rows = rows; CradleCount = cradleCount; HardpointCount = hardpointCount; }
    internal static bool TryCreate(int cradleCount, string?[] defaultRow, out CarrierFqLoadoutPlan? plan)
    {
        plan = null;
        if (cradleCount < 0 || defaultRow == null || defaultRow.Length == 0) return false;
        var rows = new string?[cradleCount * FqCradleLoadoutLogic.PhysicalRoundsPerCradle][];
        for (int index = 0; index < rows.Length; ++index) rows[index] = (string?[])defaultRow.Clone();
        plan = new CarrierFqLoadoutPlan(rows, cradleCount, defaultRow.Length);
        return true;
    }
    internal bool TryGetRow(int physicalCradleId, int physicalRound, out string?[] rowCopy)
    {
        rowCopy = System.Array.Empty<string?>();
        int index = Index(physicalCradleId, physicalRound);
        if (index < 0) return false;
        rowCopy = (string?[])rows[index].Clone();
        return true;
    }
    internal bool TrySet(int physicalCradleId, int physicalRound, int hardpoint, string? key)
    {
        int index = Index(physicalCradleId, physicalRound);
        if (index < 0 || hardpoint < 0 || hardpoint >= HardpointCount) return false;
        rows[index][hardpoint] = key;
        return true;
    }
    internal CarrierFqLoadoutPlan Copy()
    {
        var copy = new string?[rows.Length][];
        for (int index = 0; index < rows.Length; ++index) copy[index] = (string?[])rows[index].Clone();
        return new CarrierFqLoadoutPlan(copy, CradleCount, HardpointCount);
    }
    private int Index(int physicalCradleId, int physicalRound) => physicalCradleId < 0 || physicalCradleId >= CradleCount || physicalRound < 0 || physicalRound >= PhysicalRoundsPerCradle ? -1 : physicalCradleId * PhysicalRoundsPerCradle + physicalRound;
}

internal static class FqCradleLoadoutLogic
{
    internal const int PhysicalRoundsPerCradle = 4;
    internal static bool TryCreateResetPlan(int cradleCount, string?[] defaultRow, int irHardpointIndex, string? irMountKey,
                                            int roleHardpointIndex, string? aamMountKey, string? armMountKey,
                                            string? antiShipMountKey, out CarrierFqLoadoutPlan? plan)
    {
        plan = null;
        if (!CarrierFqLoadoutPlan.TryCreate(cradleCount, defaultRow, out CarrierFqLoadoutPlan? created) || created == null || irHardpointIndex < -1 || roleHardpointIndex < -1 || irHardpointIndex >= created.HardpointCount || roleHardpointIndex >= created.HardpointCount) return false;
        if (irHardpointIndex >= 0 && irMountKey != null && roleHardpointIndex >= 0 &&
            (aamMountKey != null || armMountKey != null || antiShipMountKey != null) &&
            irHardpointIndex == roleHardpointIndex) return false;
        for (int cradle = 0; cradle < created.CradleCount; ++cradle)
            for (int physicalRound = 0; physicalRound < created.PhysicalRoundsPerCradle; ++physicalRound)
            {
                if (irHardpointIndex >= 0 && irMountKey != null) created.TrySet(cradle, physicalRound, irHardpointIndex, irMountKey);
                string? role = physicalRound < 2 ? aamMountKey : physicalRound == 2 ? armMountKey : antiShipMountKey;
                if (roleHardpointIndex >= 0 && role != null) created.TrySet(cradle, physicalRound, roleHardpointIndex, role);
            }
        plan = created;
        return true;
    }
}
