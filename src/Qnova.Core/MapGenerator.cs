using System.Numerics;

namespace Qnova.Core;

/// <summary>Procedural arenas. <c>Generate(seed)</c> is deterministic (same seed, same map) and only returns maps that pass
/// <see cref="Validate"/>: spawns and pickups on solid ground, everything reachable on foot, stairs and launch-pad arcs checked.
/// A map that fails is discarded and another attempt is made from the same seed.</summary>
public static class MapGenerator
{
    const float Gravity = 800f;
    static readonly Vector3 PlayerHalf = MoveVars.Half;

    public static MapData Generate(int seed)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            var m = new Builder(seed, attempt).Build();
            if (m != null && Validate(m).Count == 0) return m;
        }
        // Practically unreachable, but never leave the player without a map.
        var fallback = Arena.Data();
        fallback.Name = $"Random #{seed} (fallback)"; fallback.Seed = seed;
        return fallback;
    }

    // ------------------------------------------------------------------ validation

    /// <summary>Everything wrong with a map (empty = playable).</summary>
    public static List<string> Validate(MapData m)
    {
        var problems = new List<string>();
        var w = new World();
        foreach (var s in m.Solids) w.Add(s);

        if (m.Spawns.Count < 8) problems.Add($"only {m.Spawns.Count} spawn points");
        foreach (var sp in m.Spawns)
        {
            if (!w.IsEmpty(sp, PlayerHalf)) problems.Add($"spawn {sp} is inside geometry");
            else if (!w.TraceBox(sp, sp - new Vector3(0, 12, 0), PlayerHalf).Hit) problems.Add($"spawn {sp} is not on the ground");
        }
        foreach (var k in m.Pickups)
        {
            if (!w.IsEmpty(k.Position, Pickup.Half)) problems.Add($"{k.Name} at {k.Position} is inside geometry");
            else if (!w.TraceBox(k.Position, k.Position - new Vector3(0, 12, 0), Pickup.Half).Hit) problems.Add($"{k.Name} at {k.Position} floats");
        }
        foreach (var d in m.Dummies)
            if (!w.IsEmpty(d, new Vector3(16, 28, 16))) problems.Add($"dummy {d} is inside geometry");

        var weapons = m.Pickups.Where(k => k.Kind == PickupKind.Weapon).Select(k => k.Weapon).ToHashSet();
        foreach (var wid in Enum.GetValues<WeaponId>())
            if (wid > WeaponId.Shotgun && !weapons.Contains(wid)) problems.Add($"no {WeaponDef.Get(wid).Name} pickup");
        if (m.Pickups.Count(k => k.Kind == PickupKind.Health) < 5) problems.Add("too little health");
        foreach (var kind in new[] { PickupKind.Shells, PickupKind.Nails, PickupKind.Rockets, PickupKind.Cells, PickupKind.Slugs })
            if (m.Pickups.Count(k => k.Kind == kind) < 2) problems.Add($"too little {kind} ammo");

        if (m.Spawns.Count > 0) problems.AddRange(CheckReachability(m, w));

        foreach (var pad in m.JumpPads)
            if (!m.Platforms.Any(p => PadLands(w, pad, p))) problems.Add($"pad at {pad.Center} does not land on any platform");
        return problems;
    }

    static bool Inside(Aabb r, float x, float z, float inset) =>
        x >= r.Min.X + inset && x <= r.Max.X - inset && z >= r.Min.Z + inset && z <= r.Max.Z - inset;

    /// <summary>Follow the pad's ballistic arc with the player hull; it must reach the platform top without touching anything else first.</summary>
    static bool PadLands(World w, JumpPad pad, Platform plat)
    {
        var from = new Vector3(pad.Center.X, pad.Trigger.Min.Y + 28f, pad.Center.Z);
        var v = pad.LaunchVelocity(from, Gravity);
        var prev = from;
        for (int i = 1; i <= 500; i++)
        {
            float t = i * 0.02f;
            var pos = from + new Vector3(v.X * t, v.Y * t - 0.5f * Gravity * t * t, v.Z * t);
            var tr = w.TraceBox(prev, pos, PlayerHalf);
            if (tr.Hit) return tr.Normal.Y > 0.7f && Inside(plat.Top, tr.EndPos.X, tr.EndPos.Z, 8f) && MathF.Abs(tr.EndPos.Y - (plat.Top.Min.Y + 28f)) < 6f;
            prev = pos;
        }
        return false;
    }

    /// <summary>Flood-fill the floor (player-sized cells) from the first spawn; every spawn, floor pickup, pad and stair foot must be reached.</summary>
    internal static List<string> CheckReachability(MapData m, World w)
    {
        const float Cell = 48f;
        int n = (int)MathF.Ceiling(m.Half * 2f / Cell);
        var free = new bool[n, n];
        var hull = new Vector3(16, 28, 16);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                free[i, j] = w.IsEmpty(new Vector3(-m.Half + Cell * (i + 0.5f), 28f, -m.Half + Cell * (j + 0.5f)), hull);

        (int, int) CellOf(Vector3 p) => (Math.Clamp((int)((p.X + m.Half) / Cell), 0, n - 1), Math.Clamp((int)((p.Z + m.Half) / Cell), 0, n - 1));

        var seen = new bool[n, n];
        var (si, sj) = CellOf(m.Spawns[0]);
        var problems = new List<string>();
        if (!free[si, sj]) { problems.Add("first spawn cell is blocked"); return problems; }
        var q = new Queue<(int, int)>();
        q.Enqueue((si, sj)); seen[si, sj] = true;
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

        void Need(Vector3 p, string what)
        {
            var (ci, cj) = CellOf(p);
            if (!free[ci, cj] || !seen[ci, cj]) problems.Add($"{what} at {p} is not reachable on foot");
        }
        foreach (var sp in m.Spawns) Need(sp, "spawn");
        foreach (var k in m.Pickups) if (k.Position.Y < 40f) Need(k.Position, k.Name);
        foreach (var p in m.Platforms) Need(p.Foot, "stair foot");
        foreach (var pad in m.JumpPads) Need(pad.Center, "jump pad");
        return problems;
    }

    // ------------------------------------------------------------------ construction

    sealed class Builder
    {
        readonly Random _r;
        readonly MapData _m = new();
        readonly List<(float X0, float Z0, float X1, float Z1)> _used = new();
        readonly List<Vector3> _bunkerSpots = new();
        float _half, _height;
        World _w = new();

        public Builder(int seed, int attempt)
        {
            _r = new Random(unchecked(seed * 7919 + attempt * 104729 + 17));
            _m.Seed = seed; _m.Name = $"Random #{seed}";
        }

        float F(float a, float b) => a + (float)_r.NextDouble() * (b - a);
        int I(int a, int b) => _r.Next(a, b + 1);
        T Pick<T>(params T[] xs) => xs[_r.Next(xs.Length)];

        bool Free(float x0, float z0, float x1, float z1, float margin)
        {
            if (x0 < -_half + 96 || z0 < -_half + 96 || x1 > _half - 96 || z1 > _half - 96) return false;
            foreach (var u in _used)
                if (x0 - margin < u.X1 && x1 + margin > u.X0 && z0 - margin < u.Z1 && z1 + margin > u.Z0) return false;
            return true;
        }
        void Use(float x0, float z0, float x1, float z1) => _used.Add((x0, z0, x1, z1));

        void Solid(Vector3 min, Vector3 max) => _m.Solids.Add(new Aabb(min, max));
        void Decor(Vector3 min, Vector3 max, Surface s, Vector3 color = default) => _m.Decor.Add(new DecorBox(new Aabb(min, max), s, color));
        void Fixture(Vector3 c, Vector3 half, Vector3 color) => Decor(c - half, c + half, Surface.Emissive, color);
        void Light(Vector3 p, Vector3 col, float radius, bool flicker = false) => _m.Lights.Add(new MapLight { Position = p, Color = col, Radius = radius, Flicker = flicker });

        static readonly Vector3 Warm = new(1.9f, 0.95f, 0.38f), Cold = new(0.35f, 0.85f, 1.3f), Red = new(1.8f, 0.18f, 0.12f);

        public MapData? Build()
        {
            _half = 1536f + 256f * I(0, 4);
            _height = 640f + 128f * I(0, 2);
            _m.Half = _half; _m.Height = _height;
            Shell();
            Spawns();
            int platforms = I(2, 3);
            for (int i = 0; i < platforms; i++) AddPlatform();
            int bunkers = I(0, 2);
            for (int i = 0; i < bunkers; i++) AddBunker();
            AddPillars(I(6, 12));
            AddCover(I(4, 8));
            AddCrates(I(4, 8));
            CeilingAndWallDecor();

            _w = new World();
            foreach (var s in _m.Solids) _w.Add(s);
            AddPads();
            AddPickups();
            for (int i = 0; i < 3; i++) if (FloorSpot(28f) is { } d) _m.Dummies.Add(d);
            return _m;
        }

        void Shell()
        {
            float h = _half, c = _height;
            Solid(new(-h, -64, -h), new(h, 0, h));
            Solid(new(-h, c, -h), new(h, c + 64, h));
            Solid(new(-h - 64, -64, -h), new(-h, c + 64, h));
            Solid(new(h, -64, -h), new(h + 64, c + 64, h));
            Solid(new(-h, -64, -h - 64), new(h, c + 64, -h));
            Solid(new(-h, -64, h), new(h, c + 64, h + 64));
        }

        void Spawns()
        {
            float e = _half - 170f;
            foreach (var (x, z) in new[] { (-e, -e), (e, e), (e, -e), (-e, e), (0f, -e), (0f, e), (-e, 0f), (e, 0f) })
            {
                _m.Spawns.Add(new Vector3(x, 28, z));
                Use(x - 100, z - 100, x + 100, z + 100);
            }
            _m.PlayerSpawn = _m.Spawns[0];
        }

        // ---- platforms: a raised slab reached by 16-high stairs on one side ----
        void AddPlatform()
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                float sx = Pick(384f, 448f, 512f, 576f), sz = Pick(384f, 448f, 512f, 576f);
                int steps = I(6, 12); float h = steps * 16f, len = steps * 48f;
                int dir = _r.Next(4);   // 0:+X 1:-X 2:+Z 3:-Z
                float cx = F(-_half + sx / 2 + 250f, _half - sx / 2 - 250f), cz = F(-_half + sz / 2 + 250f, _half - sz / 2 - 250f);
                float px0 = cx - sx / 2, px1 = cx + sx / 2, pz0 = cz - sz / 2, pz1 = cz + sz / 2;
                float ax0 = px0, ax1 = px1, az0 = pz0, az1 = pz1;
                switch (dir) { case 0: ax1 += len; break; case 1: ax0 -= len; break; case 2: az1 += len; break; default: az0 -= len; break; }
                if (!Free(ax0, az0, ax1, az1, 220f)) continue;
                Use(ax0, az0, ax1, az1);

                Solid(new(px0, 0, pz0), new(px1, h, pz1));
                for (int k = 1; k <= steps; k++)
                {
                    float o = 48f * (steps - k);
                    switch (dir)
                    {
                        case 0: Solid(new(px1 + o, 0, cz - 128), new(px1 + o + 48, 16f * k, cz + 128)); break;
                        case 1: Solid(new(px0 - o - 48, 0, cz - 128), new(px0 - o, 16f * k, cz + 128)); break;
                        case 2: Solid(new(cx - 128, 0, pz1 + o), new(cx + 128, 16f * k, pz1 + o + 48)); break;
                        default: Solid(new(cx - 128, 0, pz0 - o - 48), new(cx + 128, 16f * k, pz0 - o)); break;
                    }
                }
                Vector3 foot, toward;
                switch (dir)
                {
                    case 0: foot = new(px1 + len + 40, 28, cz); toward = new(-1, 0, 0); break;
                    case 1: foot = new(px0 - len - 40, 28, cz); toward = new(1, 0, 0); break;
                    case 2: foot = new(cx, 28, pz1 + len + 40); toward = new(0, 0, -1); break;
                    default: foot = new(cx, 28, pz0 - len - 40); toward = new(0, 0, 1); break;
                }
                var top = new Aabb(new(px0 + 24, h, pz0 + 24), new(px1 - 24, h, pz1 - 24));
                _m.Platforms.Add(new Platform(top, foot, toward));

                // glowing rim, a lamp and a beacon
                var glow = new Vector3(255, 90, 30);
                Fixture(new(cx, h + 4, pz0), new(sx / 2, 4, 3), glow); Fixture(new(cx, h + 4, pz1), new(sx / 2, 4, 3), glow);
                Fixture(new(px0, h + 4, cz), new(3, 4, sz / 2), glow); Fixture(new(px1, h + 4, cz), new(3, 4, sz / 2), glow);
                Light(new(cx, h + 130, cz), Warm, 620f, flicker: true);
                Fixture(new(px0 + 30, h + 14, pz0 + 30), new(10, 10, 10), new Vector3(255, 40, 30));
                Light(new(px0 + 30, h + 50, pz0 + 30), Red, 420f, flicker: true);
                return;
            }
        }

        // ---- bunkers: open-topped walled compounds with a doorway on the two sides facing the map centre ----
        void AddBunker()
        {
            const float r = 256, t = 32, h = 192, door = 96;
            for (int attempt = 0; attempt < 50; attempt++)
            {
                float cx = F(-_half + r + 250f, _half - r - 250f), cz = F(-_half + r + 250f, _half - r - 250f);
                if (!Free(cx - r, cz - r, cx + r, cz + r, 240f)) continue;
                Use(cx - r, cz - r, cx + r, cz + r);
                int sx = cx >= 0 ? 1 : -1, sz = cz >= 0 ? 1 : -1;
                Solid(new(cx - r, 0, cz + sz * r - t / 2), new(cx + r, h, cz + sz * r + t / 2));       // outer Z wall
                Solid(new(cx + sx * r - t / 2, 0, cz - r), new(cx + sx * r + t / 2, h, cz + r));       // outer X wall
                float iz = cz - sz * r, ix = cx - sx * r;
                Solid(new(cx - r, 0, iz - t / 2), new(cx - door, h, iz + t / 2)); Solid(new(cx + door, 0, iz - t / 2), new(cx + r, h, iz + t / 2));
                Solid(new(ix - t / 2, 0, cz - r), new(ix + t / 2, h, cz - door)); Solid(new(ix - t / 2, 0, cz + door), new(ix + t / 2, h, cz + r));
                Solid(new(cx - 40, 0, cz - 40), new(cx + 40, 64, cz + 40));                             // cover block
                Fixture(new(cx, 176, cz + 140), new(28, 6, 8), new Vector3(120, 210, 255));
                Light(new(cx, 150, cz + 140), Cold, 760f);
                _bunkerSpots.Add(new Vector3(cx + 130, 16, cz + 130));
                return;
            }
        }

        void AddPillars(int n)
        {
            for (int i = 0; i < n; i++)
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    float w = Pick(64f, 80f, 96f), x = F(-_half + 200, _half - 200), z = F(-_half + 200, _half - 200);
                    if (!Free(x - w / 2, z - w / 2, x + w / 2, z + w / 2, 230f)) continue;
                    Use(x - w / 2, z - w / 2, x + w / 2, z + w / 2);
                    Solid(new(x - w / 2, 0, z - w / 2), new(x + w / 2, _height, z + w / 2));
                    var toward = new Vector3(-x, 0, -z);
                    toward = toward.LengthSquared() < 1 ? Vector3.UnitZ : Vector3.Normalize(toward);
                    var torch = new Vector3(x, 300, z) + toward * (w / 2 + 14);
                    Fixture(torch, new(9, 22, 9), new Vector3(255, 150, 60));
                    Light(torch + toward * 30f, Warm, 760f, flicker: true);
                    break;
                }
        }

        void AddCover(int n)
        {
            for (int i = 0; i < n; i++)
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    float len = Pick(240f, 320f, 400f), x = F(-_half + 300, _half - 300), z = F(-_half + 300, _half - 300);
                    bool alongX = _r.Next(2) == 0;
                    float hx = alongX ? len / 2 : 16f, hz = alongX ? 16f : len / 2;
                    if (!Free(x - hx, z - hz, x + hx, z + hz, 200f)) continue;
                    Use(x - hx, z - hz, x + hx, z + hz);
                    Solid(new(x - hx, 0, z - hz), new(x + hx, 96, z + hz));
                    break;
                }
        }

        void AddCrates(int n)
        {
            for (int i = 0; i < n; i++)
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    float s = Pick(64f, 96f) / 2, x = F(-_half + 200, _half - 200), z = F(-_half + 200, _half - 200);
                    if (!Free(x - s, z - s, x + s, z + s, 130f)) continue;
                    Use(x - s, z - s, x + s, z + s);
                    Solid(new(x - s, 0, z - s), new(x + s, 40, z + s));
                    break;
                }
        }

        void CeilingAndWallDecor()
        {
            float h = _half, c = _height;
            for (float z = -h + 512; z <= h - 511; z += 1024) Decor(new(-h, c - 64, z - 48), new(h, c, z + 48), Surface.Metal);
            for (float x = -h + 512; x <= h - 511; x += 1024) Decor(new(x - 48, c - 96, -h), new(x + 48, c - 64, h), Surface.Metal);
            Decor(new(-h, 0, -h), new(h, 20, -h + 24), Surface.Metal); Decor(new(-h, 0, h - 24), new(h, 20, h), Surface.Metal);
            Decor(new(-h, 0, -h), new(-h + 24, 20, h), Surface.Metal); Decor(new(h - 24, 0, -h), new(h, 20, h), Surface.Metal);
            var lamp = new Vector3(255, 214, 150);
            foreach (float lx in new[] { -0.6f * h, 0f, 0.6f * h })
                foreach (float lz in new[] { -0.6f * h, 0f, 0.6f * h })
                {
                    Fixture(new(lx, c - 100, lz), new(56, 4, 56), lamp);
                    Light(new(lx, c - 130, lz), new Vector3(1.0f, 0.82f, 0.6f), 1500f);
                }
            foreach (float tt in new[] { -0.55f * h, 0f, 0.55f * h })
                foreach (var (x, z, dx, dz) in new[] { (tt, -h + 30, 0, 1), (tt, h - 30, 0, -1), (-h + 30, tt, 1, 0), (h - 30, tt, -1, 0) })
                {
                    Fixture(new(x, 230, z), new(dx == 0 ? 22 : 6, 30, dz == 0 ? 22 : 6), new Vector3(255, 160, 70));
                    Light(new(x + dx * 40f, 230, z + dz * 40f), Warm * 0.85f, 800f, flicker: tt == 0);
                }
        }

        // ---- launch pads: only kept if the arc really lands on the platform it aims at ----
        void AddPads()
        {
            var cyan = new Vector3(40, 190, 255); var rim = new Vector3(190, 235, 255);
            var order = _m.Platforms.OrderBy(_ => _r.Next()).Take(Math.Max(1, _m.Platforms.Count)).ToList();
            int wanted = Math.Min(order.Count, I(1, 3));
            int made = 0;
            foreach (var plat in order)
            {
                if (made >= wanted) break;
                float cx = (plat.Top.Min.X + plat.Top.Max.X) / 2, cz = (plat.Top.Min.Z + plat.Top.Max.Z) / 2, topY = plat.Top.Min.Y;
                for (int attempt = 0; attempt < 80; attempt++)
                {
                    float ang = F(0, MathF.Tau), dist = F(450f, 1000f);
                    float x = cx + MathF.Cos(ang) * dist, z = cz + MathF.Sin(ang) * dist;
                    if (!Free(x - 64, z - 64, x + 64, z + 64, 120f)) continue;
                    // The player keeps moving sideways while falling, so aim the *apex* short of the platform such that the
                    // landing point comes down on its centre: landing = apex distance * (1 + t_fall / t_rise).
                    float apexY = topY + 28f + F(120f, 220f);
                    float tRise = MathF.Sqrt(2f * (apexY - 28f) / Gravity), tFall = MathF.Sqrt(2f * (apexY - (topY + 28f)) / Gravity);
                    float a = dist / (1f + tFall / tRise);
                    var pad = new JumpPad
                    {
                        Trigger = new Aabb(new(x - 48, 0, z - 48), new(x + 48, 40, z + 48)),
                        Target = new Vector3(x + (cx - x) / dist * a, apexY, z + (cz - z) / dist * a),
                    };
                    if (!PadLands(_w, pad, plat)) continue;
                    Use(x - 64, z - 64, x + 64, z + 64);
                    _m.JumpPads.Add(pad);
                    Decor(new(x - 44, 0.5f, z - 44), new(x + 44, 3f, z + 44), Surface.Emissive, cyan);
                    Decor(new(x - 48, 0.5f, z - 48), new(x + 48, 4f, z - 42), Surface.Emissive, rim);
                    Decor(new(x - 48, 0.5f, z + 42), new(x + 48, 4f, z + 48), Surface.Emissive, rim);
                    Decor(new(x - 48, 0.5f, z - 42), new(x - 42, 4f, z + 42), Surface.Emissive, rim);
                    Decor(new(x + 42, 0.5f, z - 42), new(x + 48, 4f, z + 42), Surface.Emissive, rim);
                    Light(new(x, 70, z), new Vector3(0.25f, 0.85f, 1.5f), 480f);
                    made++;
                    break;
                }
            }
        }

        // ---- pickups: the best guns on the platforms and in the bunkers, everything else on the open floor ----
        Vector3? FloorSpot(float y)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                float x = F(-_half + 220, _half - 220), z = F(-_half + 220, _half - 220);
                if (!Free(x - 44, z - 44, x + 44, z + 44, 60f)) continue;
                Use(x - 44, z - 44, x + 44, z + 44);
                return new Vector3(x, y, z);
            }
            return null;
        }

        void AddPickups()
        {
            var premium = new List<Vector3>();
            foreach (var p in _m.Platforms) premium.Add(new Vector3((p.Top.Min.X + p.Top.Max.X) / 2, p.Top.Min.Y + 16f, (p.Top.Min.Z + p.Top.Max.Z) / 2));
            premium.AddRange(_bunkerSpots);

            var guns = new[] { WeaponId.Railgun, WeaponId.RocketLauncher, WeaponId.LightningGun, WeaponId.GrenadeLauncher, WeaponId.SuperNailgun, WeaponId.Nailgun, WeaponId.SuperShotgun };
            for (int i = 0; i < guns.Length; i++)
            {
                var spot = i < premium.Count ? premium[i] : FloorSpot(16f);
                if (spot is { } s) _m.Pickups.Add(new Pickup { Kind = PickupKind.Weapon, Weapon = guns[i], Position = s });
            }
            int healthBoxes = I(6, 8);
            for (int i = 0; i < healthBoxes; i++)
                if (FloorSpot(16f) is { } s) _m.Pickups.Add(new Pickup { Kind = PickupKind.Health, Amount = 25, Position = s });
            foreach (var (kind, amount) in new[] { (PickupKind.Shells, 20), (PickupKind.Nails, 50), (PickupKind.Rockets, 5), (PickupKind.Cells, 60), (PickupKind.Slugs, 10) })
                for (int i = 0; i < 2; i++)
                    if (FloorSpot(16f) is { } s) _m.Pickups.Add(new Pickup { Kind = kind, Amount = amount, Position = s });
        }
    }
}
