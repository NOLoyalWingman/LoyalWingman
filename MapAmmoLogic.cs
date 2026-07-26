using System;
using System.Collections.Generic;
namespace LoyalWingman;
internal enum MapAmmoKind { None, Gun, Ir, Arh, Sarh, Agm, Arm, Ash, Rkt, Bomb, Glide, Nuclear }
internal readonly struct MapAmmoInput
{
    internal readonly int ammo; internal readonly bool nuclear, glide, bomb, gun, cargo, sling, troops, jammer, arm, ash, agm, lineOfSight, laserGuided, laserSeeker, ir, arh; internal readonly string? seekerType;
    internal MapAmmoInput(bool nuclear, bool glide, bool bomb, bool gun, bool cargo, bool sling, bool troops, bool arm, bool ash, bool agm, bool lineOfSight, bool laserGuided, bool laserSeeker, bool ir, bool arh, string? seekerType, int ammo, bool jammer)
    { this.nuclear = nuclear; this.glide = glide; this.bomb = bomb; this.gun = gun; this.cargo = cargo; this.sling = sling; this.troops = troops; this.arm = arm; this.ash = ash; this.agm = agm; this.lineOfSight = lineOfSight; this.laserGuided = laserGuided; this.laserSeeker = laserSeeker; this.ir = ir; this.arh = arh; this.seekerType = seekerType; this.ammo = ammo; this.jammer = jammer; }
}
internal static class MapAmmoLogic
{
    internal static MapAmmoKind Classify(MapAmmoInput i)
    {
        if (i.cargo || i.sling || i.troops || i.jammer) return MapAmmoKind.None; if (i.nuclear) return MapAmmoKind.Nuclear; if (i.glide) return MapAmmoKind.Glide; if (i.bomb) return MapAmmoKind.Bomb; if (i.gun) return MapAmmoKind.Gun; if (i.arm || Is(i.seekerType, "ARM")) return MapAmmoKind.Arm;
        AntiShipWeaponKind k = StrikeMissionLogic.ResolveAntiShipWeaponKind(i.ash, i.agm, i.lineOfSight, i.laserGuided, i.laserSeeker); if (k == AntiShipWeaponKind.Ashm) return MapAmmoKind.Ash; if (k == AntiShipWeaponKind.Agm) return MapAmmoKind.Agm; if (k == AntiShipWeaponKind.LaserRocket) return MapAmmoKind.Rkt;
        if (i.ir || Is(i.seekerType, "IR")) return MapAmmoKind.Ir; if (i.arh || Is(i.seekerType, "ARH")) return MapAmmoKind.Arh; return Is(i.seekerType, "SARH") ? MapAmmoKind.Sarh : MapAmmoKind.None;
    }
    internal static bool TryGetContribution(MapAmmoInput i, out MapAmmoKind kind, out int amount) { kind = MapAmmoKind.None; amount = 0; if (i.ammo <= 0) return false; kind = Classify(i); if (kind == MapAmmoKind.None) return false; amount = i.ammo; return true; }
    internal static string Format(IReadOnlyList<int> ammo) { MapAmmoKind[] order = { MapAmmoKind.Gun, MapAmmoKind.Ir, MapAmmoKind.Arh, MapAmmoKind.Sarh, MapAmmoKind.Agm, MapAmmoKind.Arm, MapAmmoKind.Ash, MapAmmoKind.Rkt, MapAmmoKind.Bomb, MapAmmoKind.Glide, MapAmmoKind.Nuclear }; List<string> values = new List<string>(); foreach (MapAmmoKind k in order) { int n = ammo[(int)k]; if (n > 0) values.Add(Label(k) + " " + (n < 100 ? n.ToString("00") : n.ToString())); } return values.Count == 0 ? "WEAPONS --" : string.Join(" · ", values); }
    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    private static string Label(MapAmmoKind k) => k switch { MapAmmoKind.Gun => "GUN", MapAmmoKind.Ir => "IR", MapAmmoKind.Arh => "ARH", MapAmmoKind.Sarh => "SARH", MapAmmoKind.Agm => "AGM", MapAmmoKind.Arm => "ARM", MapAmmoKind.Ash => "ASH", MapAmmoKind.Rkt => "RKT", MapAmmoKind.Bomb => "BMB", MapAmmoKind.Glide => "GLD", MapAmmoKind.Nuclear => "NUC", _ => "" };
}
