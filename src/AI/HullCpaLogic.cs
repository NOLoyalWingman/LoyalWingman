using System;
using System.Numerics;

namespace LoyalWingman;

internal static class HullCpaLogic
{
    internal static bool LaunchClearanceRequired(bool measurementAvailable, float currentClearance,
                                                  float minimumClearance) =>
        measurementAvailable && float.IsFinite(currentClearance) && float.IsFinite(minimumClearance) &&
        (currentClearance < 300f || minimumClearance < 220f);

    internal static bool TryPointVsTranslatingAabb(Vector3 pointPosition, Vector3 pointVelocity, Vector3 boxCenter,
                                                    Vector3 boxVelocity, Vector3 boxExtents, float horizon,
                                                    out float currentClearance, out float minimumClearance, out float timeToMinimum)
    {
        currentClearance = minimumClearance = timeToMinimum = 0f;
        if (!Finite(pointPosition) || !Finite(pointVelocity) || !Finite(boxCenter) || !Finite(boxVelocity) || !Finite(boxExtents) ||
            !float.IsFinite(horizon) || horizon <= 0f || boxExtents.X < 0f || boxExtents.Y < 0f || boxExtents.Z < 0f) return false;
        double rx = (double)pointPosition.X - boxCenter.X, ry = (double)pointPosition.Y - boxCenter.Y, rz = (double)pointPosition.Z - boxCenter.Z;
        double vx = (double)pointVelocity.X - boxVelocity.X, vy = (double)pointVelocity.Y - boxVelocity.Y, vz = (double)pointVelocity.Z - boxVelocity.Z;
        double ex = boxExtents.X, ey = boxExtents.Y, ez = boxExtents.Z, end = horizon;
        Span<double> times = stackalloc double[8]; int count = 2; times[0] = 0d; times[1] = end;
        AddRoots(times, ref count, rx, vx, ex, end); AddRoots(times, ref count, ry, vy, ey, end); AddRoots(times, ref count, rz, vz, ez, end);
        for (int i = 1; i < count; i++)
        {
            double value = times[i]; int j = i - 1;
            while (j >= 0 && times[j] > value) { times[j + 1] = times[j]; j--; }
            times[j + 1] = value;
        }
        int unique = 0;
        for (int i = 0; i < count; i++) if (unique == 0 || times[i] != times[unique - 1]) times[unique++] = times[i];
        double bestSquared = double.PositiveInfinity, bestTime = 0d;
        for (int i = 0; i < unique; i++) Consider(times[i], rx, ry, rz, vx, vy, vz, ex, ey, ez, ref bestSquared, ref bestTime);
        for (int i = 0; i + 1 < unique; i++)
        {
            double left = times[i], right = times[i + 1], mid = (left + right) * .5d, a = 0d, b = 0d;
            AddActiveAxis(rx, vx, ex, mid, ref a, ref b); AddActiveAxis(ry, vy, ey, mid, ref a, ref b); AddActiveAxis(rz, vz, ez, mid, ref a, ref b);
            if (a > 0d)
            {
                double vertex = -b / a;
                if (vertex < left) vertex = left; else if (vertex > right) vertex = right;
                Consider(vertex, rx, ry, rz, vx, vy, vz, ex, ey, ez, ref bestSquared, ref bestTime);
            }
        }
        double currentSquared = ClearanceSquared(0d, rx, ry, rz, vx, vy, vz, ex, ey, ez);
        if (!double.IsFinite(bestSquared) || bestSquared < 0d || !double.IsFinite(bestTime) || !double.IsFinite(currentSquared) || currentSquared < 0d) return false;
        double current = Math.Sqrt(currentSquared), minimum = Math.Sqrt(bestSquared);
        if (!double.IsFinite(current) || !double.IsFinite(minimum) || current > float.MaxValue || minimum > float.MaxValue || bestTime > float.MaxValue) return false;
        currentClearance = (float)current; minimumClearance = (float)minimum; timeToMinimum = (float)bestTime;
        return float.IsFinite(currentClearance) && float.IsFinite(minimumClearance) && float.IsFinite(timeToMinimum);
    }
    private static void AddRoots(Span<double> times, ref int count, double r, double v, double extent, double horizon)
    {
        if (v == 0d) return;
        double first = (-extent - r) / v, second = (extent - r) / v;
        if (double.IsFinite(first) && first > 0d && first < horizon) times[count++] = first;
        if (double.IsFinite(second) && second > 0d && second < horizon) times[count++] = second;
    }
    private static void AddActiveAxis(double r, double v, double extent, double time, ref double a, ref double b)
    {
        double value = r + v * time;
        if (value > extent) { double c = r - extent; a += v * v; b += v * c; }
        else if (value < -extent) { double c = r + extent; a += v * v; b += v * c; }
    }
    private static void Consider(double time, double rx, double ry, double rz, double vx, double vy, double vz, double ex, double ey, double ez, ref double bestSquared, ref double bestTime)
    {
        double squared = ClearanceSquared(time, rx, ry, rz, vx, vy, vz, ex, ey, ez);
        if (!double.IsFinite(squared)) return;
        if (squared < 0d && squared > -1e-9d) squared = 0d;
        if (squared < bestSquared || squared == bestSquared && time < bestTime) { bestSquared = squared; bestTime = time; }
    }
    private static double ClearanceSquared(double time, double rx, double ry, double rz, double vx, double vy, double vz, double ex, double ey, double ez)
    {
        double x = Math.Max(Math.Abs(rx + vx * time) - ex, 0d), y = Math.Max(Math.Abs(ry + vy * time) - ey, 0d), z = Math.Max(Math.Abs(rz + vz * time) - ez, 0d);
        return x * x + y * y + z * z;
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
