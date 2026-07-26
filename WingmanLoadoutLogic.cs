namespace LoyalWingman;

internal readonly struct WingmanLoadoutPlan
{
    internal readonly int physicalRound;
    internal readonly string role;
    internal readonly string? mountKey;
    internal WingmanLoadoutPlan(int physicalRound, string role, string? mountKey)
    { this.physicalRound = physicalRound; this.role = role; this.mountKey = mountKey; }
    internal bool UsesCustomLoadout => mountKey != null;
}

internal static class WingmanLoadoutLogic
{
    internal const int RoleHardpointSetIndex = 2;
    internal static WingmanLoadoutPlan PlanForPhysicalRound(int physicalRound) => physicalRound == 2
        ? new WingmanLoadoutPlan(physicalRound, "FQ3", "ARM1_single")
        : physicalRound == 3 ? new WingmanLoadoutPlan(physicalRound, "FQ4", "AShM1_single")
        : new WingmanLoadoutPlan(physicalRound, "default", null);
    internal static bool HasCompleteLoadout(WingmanLoadoutPlan plan, int defaultCount, int hardpointCount,
                                            bool mountFound, bool mountBlocked, out string reason)
    {
        reason = "";
        if (!plan.UsesCustomLoadout) return true;
        if (defaultCount < 0) reason = "default_missing";
        else if (hardpointCount <= RoleHardpointSetIndex) reason = "hardpoint_missing";
        else if (defaultCount != hardpointCount) reason = "count_mismatch";
        else if (!mountFound) reason = "mount_missing";
        else if (mountBlocked) reason = "mount_blocked";
        return reason == "";
    }
}
