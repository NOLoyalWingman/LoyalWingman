namespace LoyalWingman;

internal enum CradleNativeReleasePath { None, Cmd, Rpc }

internal static class CradleReleaseProgressionLogic
{
    internal static int RearmCapacityOne(int currentAmmo, int ammoToRearm)
    {
        int current = currentAmmo < 0 ? 0 : currentAmmo > 1 ? 1 : currentAmmo;
        int requested = ammoToRearm < 0 ? 0 : ammoToRearm > 1 ? 1 : ammoToRearm;
        return current + requested > 1 ? 1 : current + requested;
    }

    internal static bool ShouldScheduleWingman(bool isServer, bool resolved) => isServer && resolved;
    internal static CradleNativeReleasePath NativePath(bool isServer, bool hasAuthority) =>
        isServer && hasAuthority ? CradleNativeReleasePath.Rpc :
        !isServer && hasAuthority ? CradleNativeReleasePath.Cmd :
        CradleNativeReleasePath.None;

    internal static string? SessionBlockReason(bool homeReturning, bool stateReady, bool busy, bool releasePending)
    {
        if (homeReturning) return "rtb_in_progress";
        if (!stateReady) return "switch_not_ready";
        if (busy) return "busy";
        if (releasePending) return "native_release_pending";
        return null;
    }
}
