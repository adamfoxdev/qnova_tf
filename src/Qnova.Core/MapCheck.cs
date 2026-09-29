using System.Numerics;

namespace Qnova.Core;

/// <summary>A gentler sanity check for hand-made maps than <see cref="MapGenerator.Validate"/>: errors are things that break play
/// (stuck spawns, unreachable items, pads that fling you into a wall); warnings are just missing content.</summary>
public static class MapCheck
{
    static readonly Vector3 Hull = new(16, 28, 16);

    public sealed record Result(List<string> Errors, List<string> Warnings)
    {
        public bool Ok => Errors.Count == 0;
    }

    public static Result Check(MapData m)
    {
        var errors = new List<string>(); var warnings = new List<string>();
        var w = new World();
        foreach (var s in m.Solids) w.Add(s);

        if (m.Spawns.Count < 4) warnings.Add($"only {m.Spawns.Count} spawn point(s); 4+ recommended for bots");
        foreach (var sp in m.Spawns)
        {
            if (!w.IsEmpty(sp, Hull)) errors.Add($"spawn {Fmt(sp)} is inside geometry");
            else if (!w.TraceBox(sp, sp - new Vector3(0, 12, 0), Hull).Hit) errors.Add($"spawn {Fmt(sp)} is not on the ground (place its Y 28 above the floor)");
        }
        foreach (var k in m.Pickups)
        {
            if (!w.IsEmpty(k.Position, Pickup.Half)) errors.Add($"{k.Name} at {Fmt(k.Position)} is inside geometry");
            else if (!w.TraceBox(k.Position, k.Position - new Vector3(0, 12, 0), Pickup.Half).Hit) errors.Add($"{k.Name} at {Fmt(k.Position)} floats");
        }
        foreach (var d in m.Dummies)
            if (!w.IsEmpty(d, new Vector3(16, 28, 16))) errors.Add($"dummy {Fmt(d)} is inside geometry");
        foreach (var f in new[] { m.RedFlag, m.BlueFlag })
            if (f is { } fp && !w.IsEmpty(fp, Flag.Half)) errors.Add($"flag base {Fmt(fp)} is inside geometry");

        errors.AddRange(Reachability(m, w));

        foreach (var pad in m.JumpPads)
        {
            var t = pad.Target;
            if (MathF.Abs(t.X) > m.Half || MathF.Abs(t.Z) > m.Half || t.Y > m.Height - 56f) errors.Add($"pad at {Fmt(pad.Center)} aims outside the map or into the ceiling (target {Fmt(t)})");
            else if (!w.IsEmpty(t, Hull)) errors.Add($"pad at {Fmt(pad.Center)} aims into solid geometry (target {Fmt(t)})");
        }

        var weapons = m.Pickups.Where(k => k.Kind == PickupKind.Weapon).Select(k => k.Weapon).ToHashSet();
        var missing = Enum.GetValues<WeaponId>().Where(x => x > WeaponId.Shotgun && !weapons.Contains(x)).Select(x => WeaponDef.Get(x).Name).ToList();
        if (missing.Count > 0) warnings.Add($"no pickup for: {string.Join(", ", missing)}");
        if (!m.Pickups.Any(k => k.Kind == PickupKind.Health)) warnings.Add("no health pickups");
        if ((m.RedFlag == null) != (m.BlueFlag == null)) errors.Add("only one flag base is set");
        return new Result(errors, warnings);
    }

    /// <summary>Flood-fill the ground from the first ground-level spawn. Ground-level spawns, pickups and pads must be reachable;
    /// anything raised (ledges, mesas) is skipped because reaching it depends on stairs and pads the flood can't model.</summary>
    static List<string> Reachability(MapData m, World w)
    {
        var problems = new List<string>();
        var start = m.Spawns.Cast<Vector3?>().FirstOrDefault(s => s!.Value.Y < 40f);
        if (start == null) return problems;
        const float Cell = 48f;
        int n = (int)MathF.Ceiling(m.Half * 2f / Cell);
        var free = new bool[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                free[i, j] = w.IsEmpty(new Vector3(-m.Half + Cell * (i + 0.5f), 28f, -m.Half + Cell * (j + 0.5f)), Hull);
        (int, int) CellOf(Vector3 p) => (Math.Clamp((int)((p.X + m.Half) / Cell), 0, n - 1), Math.Clamp((int)((p.Z + m.Half) / Cell), 0, n - 1));
        var seen = new bool[n, n];
        var (si, sj) = CellOf(start.Value);
        if (!free[si, sj]) return problems;                     // already reported as a bad spawn
        var q = new Queue<(int, int)>(); q.Enqueue((si, sj)); seen[si, sj] = true;
        int[] dx = { 1, -1, 0, 0 }, dz = { 0, 0, 1, -1 };
        while (q.Count > 0)
        {
            var (ci, cj) = q.Dequeue();
            for (int d = 0; d < 4; d++)
            {
                int ni = ci + dx[d], nj = cj + dz[d];
                if (ni < 0 || nj < 0 || ni >= n || nj >= n || seen[ni, nj] || !free[ni, nj]) continue;
                seen[ni, nj] = true; q.Enqueue((ni, nj));
            }
        }
        bool Reached(Vector3 p)
        {
            var (ci, cj) = CellOf(p);
            for (int i = Math.Max(0, ci - 1); i <= Math.Min(n - 1, ci + 1); i++)
                for (int j = Math.Max(0, cj - 1); j <= Math.Min(n - 1, cj + 1); j++)
                    if (seen[i, j]) return true;
            return false;
        }
        foreach (var sp in m.Spawns) if (sp.Y < 40f && !Reached(sp)) problems.Add($"spawn {Fmt(sp)} is walled off from the first spawn");
        foreach (var k in m.Pickups) if (k.Position.Y < 40f && !Reached(k.Position)) problems.Add($"{k.Name} at {Fmt(k.Position)} can't be reached on foot");
        foreach (var pad in m.JumpPads) if (pad.Trigger.Min.Y < 8f && !Reached(pad.Center)) problems.Add($"pad at {Fmt(pad.Center)} can't be reached on foot");
        return problems;
    }

    static string Fmt(Vector3 v) => $"({v.X:0} {v.Y:0} {v.Z:0})";
}
