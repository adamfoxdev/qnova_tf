namespace Qnova.Core;

public enum PlayerClass { None, Scout, Soldier, Demoman, Medic, Heavy, Sniper }

/// <summary>A Team Fortress class: a health pool, a run-speed scale and a fixed loadout (with starting ammo).
/// Medics also heal: <see cref="HealAura"/> hp/s to teammates within <see cref="GameWorld.MedicRange"/>.</summary>
public sealed record ClassDef(
    PlayerClass Id, string Name, int Health, float Speed, WeaponId[] Weapons, WeaponId Primary,
    int Shells = 0, int Nails = 0, int Rockets = 0, int Cells = 0, int Slugs = 0, float HealAura = 0, string Blurb = "")
{
    public static readonly ClassDef[] All =
    {
        new(PlayerClass.Scout,   "Scout",   75,  1.30f, new[] { WeaponId.Axe, WeaponId.Shotgun, WeaponId.SuperShotgun }, WeaponId.SuperShotgun,
            Shells: 50, Blurb: "fast and frail, double shotgun"),
        new(PlayerClass.Soldier, "Soldier", 125, 0.90f, new[] { WeaponId.Axe, WeaponId.Shotgun, WeaponId.RocketLauncher }, WeaponId.RocketLauncher,
            Shells: 25, Rockets: 40, Blurb: "rocket launcher, rocket jumps"),
        new(PlayerClass.Demoman, "Demoman", 100, 1.00f, new[] { WeaponId.Axe, WeaponId.Shotgun, WeaponId.GrenadeLauncher }, WeaponId.GrenadeLauncher,
            Shells: 25, Rockets: 50, Blurb: "grenade launcher, area denial"),
        new(PlayerClass.Medic,   "Medic",   90,  1.10f, new[] { WeaponId.Axe, WeaponId.Shotgun, WeaponId.Nailgun }, WeaponId.Nailgun,
            Shells: 25, Nails: 150, HealAura: 8f, Blurb: "nailgun, heals nearby teammates"),
        new(PlayerClass.Heavy,   "Heavy",   200, 0.75f, new[] { WeaponId.Axe, WeaponId.SuperShotgun, WeaponId.SuperNailgun }, WeaponId.SuperNailgun,
            Shells: 50, Nails: 200, Blurb: "huge health, super nailgun, slow"),
        new(PlayerClass.Sniper,  "Sniper",  80,  1.00f, new[] { WeaponId.Axe, WeaponId.Shotgun, WeaponId.Railgun }, WeaponId.Railgun,
            Shells: 25, Slugs: 20, Blurb: "railgun, long range"),
    };

    public static ClassDef Get(PlayerClass c) => All[(int)c - 1];

    public static bool TryParse(string s, out PlayerClass c)
    {
        c = PlayerClass.None;
        if (int.TryParse(s, out int n) && n >= 1 && n <= All.Length) { c = All[n - 1].Id; return true; }
        foreach (var d in All)
            if (d.Name.Equals(s, StringComparison.OrdinalIgnoreCase)) { c = d.Id; return true; }
        return false;
    }
}
