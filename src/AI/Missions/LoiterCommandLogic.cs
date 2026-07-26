namespace LoyalWingman;

internal static class LoiterCommandLogic
{
    internal static bool ShouldIngress(bool fixedPoint, float horizontalDistance) =>
        fixedPoint && horizontalDistance > 2200f;
    internal static float OrbitTargetSpeed(float cornerSpeed) =>
        float.IsFinite(cornerSpeed) && cornerSpeed > 0f ? cornerSpeed : 165f;
    internal static float OrbitThrottle(float speed, float cornerSpeed, float cruiseThrottle)
    {
        float target = OrbitTargetSpeed(cornerSpeed);
        float cruise = float.IsFinite(cruiseThrottle) ? System.Math.Clamp(cruiseThrottle, .3f, 1f) : .65f;
        float eco = System.Math.Clamp(.75f * cruise, .3f, cruise);
        if (!float.IsFinite(speed)) return cruise;
        if (speed >= target)
        {
            float overspeed = System.Math.Clamp((speed - target) / target, 0f, 1f);
            return System.Math.Clamp(eco - .15f * overspeed, .3f, eco);
        }
        float deficit = System.Math.Clamp((target - speed) / (.15f * target), 0f, 1f);
        return eco + (1f - eco) * deficit;
    }
}
