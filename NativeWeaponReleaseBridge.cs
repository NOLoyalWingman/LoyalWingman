using BepInEx.Logging;

namespace LoyalWingman;

// The static bridge accepts one owner so native weapon instances cannot schedule releases against a stale Plugin
// lifecycle.
internal static class NativeWeaponReleaseBridge
{
    private static ManualLogSource? log;
    internal delegate bool ReleaseCallback(Aircraft attachedCarrier, LoyalWingmanCradleWeapon firingRound, int physicalCradleId,
                                           int physicalRound, out string reason);

    private static object? releaseOwner;
    private static ReleaseCallback? releaseCallback;

    internal static void Configure(ManualLogSource source) => log = source;
    internal static void Log(string text)
    {
        try { log?.LogInfo("[LoyalWingman] " + text); }
        catch { }
    }

    internal static bool RegisterReleaseCallback(object owner, ReleaseCallback callback, out string reason)
    {
        if (releaseCallback != null && !ReferenceEquals(releaseOwner, owner))
        {
            reason = "callback_owned";
            Log("state=native_release_rejected reason=" + reason);
            return false;
        }
        releaseOwner = owner;
        releaseCallback = callback;
        reason = "ok";
        return true;
    }

    internal static void ClearReleaseCallback(object owner)
    {
        if (!ReferenceEquals(releaseOwner, owner)) return;
        releaseCallback = null;
        releaseOwner = null;
    }

    internal static bool RequestRelease(Aircraft attachedCarrier, LoyalWingmanCradleWeapon firingRound, int physicalCradleId,
                                        int physicalRound, out string reason)
    {
        ReleaseCallback? callback = releaseCallback;
        if (callback == null) { reason = "callback_unavailable"; return false; }
        return callback(attachedCarrier, firingRound, physicalCradleId, physicalRound, out reason);
    }
}
