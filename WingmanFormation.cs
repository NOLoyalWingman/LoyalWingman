namespace LoyalWingman;

internal readonly struct FormationSlot
{
    internal readonly int ordinal;
    internal readonly float back, lateral, up;
    internal FormationSlot(int ordinal, float back, float lateral, float up)
    {
        this.ordinal = ordinal;
        this.back = back;
        this.lateral = lateral;
        this.up = up;
    }
    internal static FormationSlot For(int ordinal)
    {
        switch (ordinal)
        {
            case 0:
                return new FormationSlot(0, 650f, -300f, 200f);
            case 1:
                return new FormationSlot(1, 650f, 300f, 200f);
            case 2:
                return new FormationSlot(2, 1050f, -600f, 300f);
            case 3:
                return new FormationSlot(3, 1050f, 600f, 300f);
            default:
                return new FormationSlot(ordinal, 1450f + 400f * (ordinal - 4), 0f, 400f);
        }
    }
}

internal readonly struct SeparationDirective
{
    internal readonly bool shouldYield;
    internal readonly string otherPid;
    internal readonly float currentDistance, cpaDistance, timeToCpa;
    internal SeparationDirective(bool shouldYield, string otherPid, float currentDistance, float cpaDistance,
                                 float timeToCpa)
    {
        this.shouldYield = shouldYield;
        this.otherPid = otherPid;
        this.currentDistance = currentDistance;
        this.cpaDistance = cpaDistance;
        this.timeToCpa = timeToCpa;
    }
}
