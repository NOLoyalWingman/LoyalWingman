namespace LoyalWingman;

internal static class MultiplayerCleanupLogic
{
    internal static bool ShouldFailClosedDespawn(bool isSinglePlayer, bool serverActive,
                                                  bool hasPlayerAssociation) =>
        !isSinglePlayer && serverActive && !hasPlayerAssociation;

    internal static bool ShouldClearClientSnapshot(bool ok) => !ok;
}
