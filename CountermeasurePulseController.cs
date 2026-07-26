using UnityEngine;

namespace LoyalWingman;

internal sealed class CountermeasurePulseController
{
    private float pulseOffAt, nextPulseAt;
    private byte selectedIndex = 255;
    private bool triggerRequested;

    internal void Update(Aircraft drone, Missile threat, bool allowed)
    {
        float now = Time.realtimeSinceStartup;
        if (triggerRequested && now >= pulseOffAt) Stop(drone);
        if (!allowed) { Stop(drone); return; }
        if (triggerRequested || now < nextPulseAt || drone.countermeasureManager == null) return;
        drone.countermeasureManager.ChooseCountermeasure(threat);
        byte index = drone.countermeasureManager.activeIndex;
        var countermeasure = index == 255 ? null : drone.countermeasureManager.GetActiveCountermeasure();
        if (countermeasure == null || countermeasure.ammo <= 0) return;
        selectedIndex = index;
        drone.Countermeasures(true, index);
        triggerRequested = true;
        pulseOffAt = now + .1f;
        nextPulseAt = now + .5f;
    }

    internal void Stop(Aircraft drone)
    {
        if (!triggerRequested) return;
        try { drone.Countermeasures(false, selectedIndex); }
        finally { triggerRequested = false; selectedIndex = 255; }
    }
}
