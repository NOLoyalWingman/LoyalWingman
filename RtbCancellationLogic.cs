namespace LoyalWingman;

internal static class RtbCancellationLogic
{
    internal static bool CanCancel(bool returningToBase, bool disabled, bool ejected, bool abandoned, bool returned,
                                   float radarAlt, out string reason)
    {
        if (!returningToBase)
        {
            reason = "not_returning";
            return false;
        }
        if (disabled || ejected || abandoned || returned || !(radarAlt > .2f))
        {
            reason = "rtb_committed";
            return false;
        }
        reason = "";
        return true;
    }
}
