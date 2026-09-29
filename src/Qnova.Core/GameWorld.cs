using System.Numerics;

namespace Qnova.Core;

/// <summary>Fixed-step simulation of the human player, bots, dummy targets and projectiles.</summary>
public sealed partial class GameWorld
{
    public const float TickRate = 72f;
    public const float Dt = 1f / TickRate;
    const float RespawnDelay = 3f;
    static readonly Vector3 NailHalf = new(1, 1, 1);
    static readonly Vector3 GrenadeHalf = new(2, 2, 2);

    public readonly World Map;
    public readonly MoveSettings Settings = new();
    public readonly Player Player;
    public readonly GameConsole Console = new();
    public readonly KeyBindings Bindings = new();
    public Vector3 SpawnPoint { get; private set; }
    public readonly List<Vector3> SpawnPoints = new();

    // current map (see LoadMap)
    public string MapName { get; private set; } = "Classic Arena";
    public int MapSeed { get; private set; }
    public float CeilingY { get; private set; } = 768f;
    public float MapHalf { get; private set; } = 2048f;     // interior is 2*MapHalf square
    /// <summary>Raised after <see cref="LoadMap"/> so a UI can drop anything tied to the old map.</summary>
    public event Action? MapLoaded;
    public readonly List<Bot> Bots = new();
    public readonly List<Pickup> Pickups = new();
    public readonly List<DecorBox> Decor = new();      // visual only
    public readonly List<MapLight> Lights = new();
    public readonly List<JumpPad> JumpPads = new();
    public float HookRange = 1600f;       // sv_hookrange
    const float HookFlightSpeed = 2600f;
    const float PadRetrigger = 0.5f;
    public float PickupRespawn = 30f;   // seconds; sv_pickup_respawn
    public bool InfiniteAmmo;
    public bool BotAi = true;
    public int BotSkill = 3;           // 1 (easy) .. 5 (hard)
    public readonly List<Target> Targets = new();
    public readonly List<Projectile> Projectiles = new();
    public readonly List<GameEvent> Events = new();
    public float Time;
    internal readonly Random Rng;
    int _botCounter;

    public GameWorld(World map, Vector3 spawn, int seed = 1)
    {
        Map = map;
        SpawnPoint = spawn;
        Player = new Player(map, spawn, Settings) { Name = "You" };
        Rng = new Random(seed);
        GameCommands.Install(this);
    }

    internal void SetMapInfo(string name, int seed, float half, float height)
    {
        MapName = name; MapSeed = seed; MapHalf = half; CeilingY = height;
    }

    /// <summary>Replace the current map in place. The console, key bindings, settings and player object are kept; geometry,
    /// pickups, pads, lights and dummies are swapped, scores reset, and everyone respawns on the new map.</summary>
    public void LoadMap(MapData m)
    {
        Map.Clear();
        foreach (var s in m.Solids) Map.Add(s);
        Decor.Clear(); Decor.AddRange(m.Decor);
        Lights.Clear(); Lights.AddRange(m.Lights);
        Pickups.Clear(); Pickups.AddRange(m.Pickups);
        JumpPads.Clear(); JumpPads.AddRange(m.JumpPads);
        SpawnPoints.Clear(); SpawnPoints.AddRange(m.Spawns);
        _current = m;
        Targets.Clear();
        foreach (var d in m.Dummies) Targets.Add(new Target { Origin = d });
        SpawnPoint = m.PlayerSpawn;
        SetMapInfo(m.Name, m.Seed, m.Half, m.Height);

        Projectiles.Clear(); Events.Clear();
        foreach (var c in Combatants) { c.Frags = 0; c.Deaths = 0; ReleaseHook(c); }
        SetupMode();
        if (IsCtf) RespawnPlayer(Player); else Reset(Player, SpawnPoint);
        foreach (var b in Bots) RespawnPlayer(b.Body);
        MapLoaded?.Invoke();
    }

    /// <summary>Where new random-map seeds come from. Overridable so tests are deterministic.</summary>
    public Func<int> MapSeedSource = () => Random.Shared.Next(1, 1_000_000);

    /// <summary>Generate and load a random map (seed 0 or less = pick a fresh one) and announce it in the console.</summary>
    public MapData LoadRandomMap(int seed = 0)
    {
        if (seed <= 0) seed = MapSeedSource();
        var m = MapGenerator.Generate(seed);
        LoadMap(m);
        Console.Print($"{m.Summary}  (replay with: map random {m.Seed})");
        return m;
    }

    public void LoadClassicArena()
    {
        LoadMap(Arena.Data());
        Console.Print("Classic Arena");
    }

    public IEnumerable<Player> Combatants
    {
        get { yield return Player; foreach (var b in Bots) yield return b.Body; }
    }

    public void Tick(UserCmd cmd, bool fire, WeaponId? select = null)
    {
        Time += Dt;
        var p = Player;
        p.Yaw = cmd.Yaw; p.Pitch = cmd.Pitch;
        if (select is { } w && p.Owned.Contains(w)) p.Current = w;

        HandleHook(p, cmd.Grapple);
        if (p.Alive)
        {
            p.Move.Tick(cmd, Dt);
            if (fire && Time >= p.NextFire) Fire(p);
        }
        else if (p.RespawnAt > 0 && Time >= p.RespawnAt) RespawnPlayer(p);

        foreach (var b in Bots) b.Update(this);
        UpdateHooks();
        UpdatePads();
        UpdatePickups();
        UpdateFlags();
        UpdateMedics();
        UpdateProjectiles();
        foreach (var t in Targets)
            if (!t.Alive && Time >= t.RespawnAt) t.Health = 100;
    }

    // ---- bots and spawning ----

    public Bot AddBot()
    {
        var body = new Player(Map, SpawnPoint, Settings) { Name = $"Bot{++_botCounter}", Id = _botCounter, IsBot = true };
        body.Owned = new HashSet<WeaponId>(Enum.GetValues<WeaponId>());   // bots spawn with the full arsenal
        var bot = new Bot(body, Rng.Next());
        Bots.Add(bot);
        if (IsCtf) AssignTeams();
        RespawnPlayer(body);
        return bot;
    }

    /// <summary>Spawn at the point farthest from every living opponent, with full health and fresh ammo.</summary>
    public void RespawnPlayer(Player p)
    {
        var best = SpawnPoint; float bestScore = float.MinValue;
        foreach (var sp in SpawnCandidates(p))
        {
            float nearest = float.MaxValue;
            foreach (var o in Combatants) if (o != p && o.Alive) nearest = MathF.Min(nearest, Vector3.Distance(o.Move.Position, sp));
            float score = nearest + (float)Rng.NextDouble() * 50f;   // jitter so ties vary
            if (score > bestScore) { bestScore = score; best = sp; }
        }
        Reset(p, best);
    }

    IEnumerable<Vector3> SpawnCandidates(Player p)
    {
        if (IsCtf && _teamSpawns.TryGetValue(p.Team, out var mine) && mine.Count > 0) return mine;
        return SpawnPoints.Count > 0 ? SpawnPoints : new List<Vector3> { SpawnPoint };
    }

    internal void Reset(Player p, Vector3 at)
    {
        ReleaseHook(p);
        p.Move.Position = at; p.Move.Velocity = default; p.Move.OnGround = false;
        p.RespawnAt = 0;
        if (IsTf) { ApplyClass(p); return; }
        p.Class = PlayerClass.None; p.Move.SpeedScale = 1f; p.MaxHealth = 100; p.Health = 100;
        if (p.IsBot) p.Owned = new HashSet<WeaponId>(Enum.GetValues<WeaponId>());
        if (p.IsBot) { p.Shells = 50; p.Nails = 200; p.Rockets = 25; p.Cells = 200; p.Slugs = 20; }
        else
        {
            // Humans lose their guns on death and start over with the axe and shotgun.
            p.Shells = 25; p.Nails = 100; p.Rockets = 10; p.Cells = 50; p.Slugs = 5;
            p.Owned = new HashSet<WeaponId> { WeaponId.Axe, WeaponId.Shotgun };
            p.Current = WeaponId.Shotgun;
        }
    }

    // ---- grappling hook ----

    /// <summary>Press starts a throw; holding keeps the reel-in going; releasing drops it (momentum is kept).</summary>
    void HandleHook(Player p, bool held)
    {
        var h = p.Hook;
        if (!p.Alive) { ReleaseHook(p); p.GrappleHeld = false; return; }
        if (held && !p.GrappleHeld && h.State == HookState.None && Time >= h.NextAt)
        {
            h.State = HookState.Flying; h.Pos = p.Eye; h.Dir = p.Look; h.Traveled = 0;
            Events.Add(new GameEvent(EventKind.HookFire, p.Eye, Arg: p == Player ? 1 : 0));
        }
        if (!held && h.State != HookState.None) ReleaseHook(p);
        p.GrappleHeld = held;
    }

    public void ReleaseHook(Player p)
    {
        var h = p.Hook;
        if (h.State == HookState.None) return;
        h.State = HookState.None;
        h.NextAt = Time + 0.25f;
        p.Move.HookAnchor = null;
    }

    void UpdateHooks()
    {
        foreach (var c in Combatants)
        {
            var h = c.Hook;
            if (h.State == HookState.Flying)
            {
                float step = HookFlightSpeed * Dt;
                var seg = h.Dir * step;
                var wall = Map.TraceRay(h.Pos, h.Pos + seg);
                float entity = float.MaxValue;
                foreach (var t in Targets)
                    if (t.Alive && World.RayVsBox(h.Pos, seg, t.Bounds.Min, t.Bounds.Max, out float f, out _)) entity = MathF.Min(entity, f);
                foreach (var o in Combatants)
                    if (o != c && o.Alive) { var b = Box(o); if (World.RayVsBox(h.Pos, seg, b.Min, b.Max, out float f, out _)) entity = MathF.Min(entity, f); }

                if (entity < wall.Fraction) { ReleaseHook(c); continue; }            // hooks don't grab people
                if (wall.Hit)
                {
                    h.State = HookState.Attached; h.Pos = wall.EndPos;
                    h.CheckAt = Time + 0.35f; h.LastDist = Vector3.Distance(c.Move.Position, h.Pos);
                    c.Move.HookAnchor = h.Pos;
                    Events.Add(new GameEvent(EventKind.HookAttach, h.Pos, wall.Normal, c == Player ? 1 : 0));
                }
                else
                {
                    h.Pos += seg; h.Traveled += step;
                    if (h.Traveled >= HookRange) ReleaseHook(c);                     // nothing in range
                }
            }
            else if (h.State == HookState.Attached)
            {
                if (!c.Alive) { ReleaseHook(c); continue; }
                if (Time >= h.CheckAt)
                {
                    float dist = Vector3.Distance(c.Move.Position, h.Pos);
                    // Swinging on a fixed-length rope legitimately holds the distance constant, so only reeling can snag:
                    // no longer closing in means we're caught on geometry, so let go rather than dangling forever.
                    if (!c.Move.Swinging && dist > 64f && h.LastDist - dist < 6f) { ReleaseHook(c); continue; }
                    h.LastDist = dist; h.CheckAt = Time + 0.35f;
                }
            }
        }
    }

    // ---- jump pads ----

    void UpdatePads()
    {
        if (JumpPads.Count == 0) return;
        foreach (var c in Combatants)
        {
            if (!c.Alive || Time < c.PadCooldownUntil) continue;
            var box = Box(c);
            foreach (var pad in JumpPads)
            {
                if (!box.Overlaps(pad.Trigger)) continue;
                c.Move.Velocity = pad.LaunchVelocity(c.Move.Position, Settings.Gravity);
                c.Move.OnGround = false;
                c.PadCooldownUntil = Time + PadRetrigger;
                Events.Add(new GameEvent(EventKind.JumpPad, pad.Center, c == Player ? Vector3.UnitX : default));
                break;
            }
        }
    }

    // ---- pickups ----

    void UpdatePickups()
    {
        foreach (var k in Pickups)
        {
            if (!k.Active)
            {
                if (Time < k.RespawnAt) continue;
                k.Active = true;
                Events.Add(new GameEvent(EventKind.ItemRespawn, k.Position, Arg: (int)k.Kind));
            }
            var box = k.Bounds;
            foreach (var c in Combatants)
            {
                if (!c.Alive || !Box(c).Overlaps(box) || !TryCollect(c, k)) continue;
                k.Active = false;
                k.RespawnAt = Time + PickupRespawn;
                Events.Add(new GameEvent(EventKind.Pickup, k.Position, c == Player ? Vector3.UnitX : default, (int)k.Kind));
                if (c == Player) Console.Print($"You got the {k.Name}");
                break;
            }
        }
    }

    /// <summary>Apply a pickup to a combatant. Returns false (leaving the item in place) if it would do nothing.</summary>
    bool TryCollect(Player c, Pickup k)
    {
        switch (k.Kind)
        {
            case PickupKind.Health:
                if (c.Health >= c.MaxHealth) return false;
                c.Health = Math.Min(c.MaxHealth, c.Health + k.Amount);
                return true;
            case PickupKind.Shells: return c.AddAmmo(AmmoType.Shells, k.Amount) > 0;
            case PickupKind.Nails: return c.AddAmmo(AmmoType.Nails, k.Amount) > 0;
            case PickupKind.Rockets: return c.AddAmmo(AmmoType.Rockets, k.Amount) > 0;
            case PickupKind.Cells: return c.AddAmmo(AmmoType.Cells, k.Amount) > 0;
            case PickupKind.Slugs: return c.AddAmmo(AmmoType.Slugs, k.Amount) > 0;
            default:
                var (type, amt) = Pickup.WeaponAmmo(k.Weapon);
                bool isNew = c.Owned.Add(k.Weapon);
                int got = type == AmmoType.None ? 0 : c.AddAmmo(type, k.Amount > 0 ? k.Amount : amt);
                if (!isNew && got == 0) return false;                  // already have it and full of ammo
                if (isNew && !c.IsBot && WeaponDef.Get(k.Weapon).Id > c.Current) c.Current = k.Weapon;   // auto-switch up
                return true;
        }
    }

    // ---- firing ----

    public void TryFire(Player p)
    {
        if (p.Alive && Time >= p.NextFire) Fire(p);
    }

    void Fire(Player p)
    {
        var def = WeaponDef.Get(p.Current);
        if (p.Ammo(def.Ammo) < def.AmmoPerShot)
        {
            p.NextFire = Time + 0.25f;   // rate-limit the empty click
            if (!p.IsBot) Events.Add(new GameEvent(EventKind.DryFire, p.Eye, Arg: (int)def.Id));
            return;
        }
        if (!(InfiniteAmmo && p == Player)) p.Spend(def.Ammo, def.AmmoPerShot);
        p.NextFire = Time + def.Refire;
        Events.Add(new GameEvent(EventKind.Shot, p.Eye, Arg: (int)def.Id));

        var dir = p.Look;
        switch (def.Mode)
        {
            case FireMode.Melee:
                Hitscan(p, def.Id, p.Eye, dir, def.Range, def.Damage, false);
                break;
            case FireMode.Hitscan:
                var right = Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitY));
                var up = Vector3.Cross(right, dir);
                for (int i = 0; i < def.Pellets; i++)
                {
                    var d = Vector3.Normalize(dir + right * (Crandom() * def.SpreadX) + up * (Crandom() * def.SpreadY));
                    Hitscan(p, def.Id, p.Eye, d, def.Range, def.Damage, true);
                }
                break;
            case FireMode.Beam:
                Hitscan(p, def.Id, p.Eye, dir, def.Range, def.Damage, true);   // continuous lightning: many small hits
                break;
            case FireMode.Rail:
                RailShot(p, def, dir);
                break;
            case FireMode.Nail:
                Projectiles.Add(new Projectile { Kind = ProjectileKind.Nail, Weapon = def.Id, Owner = p, Pos = p.Eye, Vel = dir * def.ProjectileSpeed, Damage = def.Damage, Expires = Time + 6 });
                break;
            case FireMode.Rocket:
                Projectiles.Add(new Projectile { Kind = ProjectileKind.Rocket, Weapon = def.Id, Owner = p, Pos = p.Eye, Vel = dir * def.ProjectileSpeed, Damage = def.Damage, Splash = def.SplashRadius, Expires = Time + 5 });
                break;
            case FireMode.Grenade:
                // Q1: forward*600 + up*200, bounces, 2.5s fuse.
                var up2 = Vector3.Normalize(Vector3.Cross(Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitY)), dir));
                Projectiles.Add(new Projectile { Kind = ProjectileKind.Grenade, Weapon = def.Id, Owner = p, Pos = p.Eye, Vel = dir * def.ProjectileSpeed + up2 * 200f, Damage = def.Damage, Splash = def.SplashRadius, Expires = Time + 2.5f });
                break;
        }
    }

    float Crandom() => (float)(Rng.NextDouble() * 2 - 1);

    static Aabb Box(Player p) => Aabb.FromCenter(p.Move.Position, MoveVars.Half);

    /// <summary>Is a living enemy (bot or dummy) the first thing along the shooter's line of sight, within the current
    /// weapon's reach? Drives the red crosshair: "shooting now would hit".</summary>
    public bool AimingAtEnemy(Player shooter)
    {
        if (!shooter.Alive) return false;
        var def = WeaponDef.Get(shooter.Current);
        float range = def.Range;
        var origin = shooter.Eye; var dir = shooter.Look;
        var wall = Map.TraceRay(origin, origin + dir * range);
        float reach = wall.Fraction * range;
        foreach (var t in Targets)
            if (t.Alive && World.RayVsBox(origin, dir * range, t.Bounds.Min, t.Bounds.Max, out float f, out _) && f * range <= reach) return true;
        foreach (var o in Combatants)
            if (o != shooter && o.Alive && !Friendly(shooter, o))
            {
                var b = Box(o);
                if (World.RayVsBox(origin, dir * range, b.Min, b.Max, out float f, out _) && f * range <= reach) return true;
            }
        return false;
    }

    /// <summary>Q3 railgun: an instant beam that passes through every player and dummy in its path (until a wall) for full damage.</summary>
    void RailShot(Player shooter, WeaponDef def, Vector3 dir)
    {
        var origin = shooter.Eye;
        var wall = Map.TraceRay(origin, origin + dir * def.Range);
        float reach = wall.Fraction * def.Range;
        var hits = new List<(float Dist, object Who)>();
        foreach (var t in Targets)
            if (t.Alive && World.RayVsBox(origin, dir * def.Range, t.Bounds.Min, t.Bounds.Max, out float f, out _) && f * def.Range <= reach)
                hits.Add((f, t));
        foreach (var o in Combatants)
            if (o != shooter && o.Alive)
            {
                var b = Box(o);
                if (World.RayVsBox(origin, dir * def.Range, b.Min, b.Max, out float f, out _) && f * def.Range <= reach) hits.Add((f, o));
            }
        Events.Add(new GameEvent(EventKind.Tracer, origin, origin + dir * reach, (int)def.Id));
        if (wall.Hit) Events.Add(new GameEvent(EventKind.Impact, origin + dir * reach, wall.Normal));
        foreach (var (_, who) in hits.OrderBy(h => h.Dist))
        {
            if (who is Target t) Damage(t, def.Damage, dir, shooter);
            else if (who is Player pl) DamagePlayer(pl, def.Damage, dir, shooter, knock: false, def.Id);
        }
    }

    void Hitscan(Player shooter, WeaponId weapon, Vector3 origin, Vector3 dir, float range, int damage, bool tracer)
    {
        var wall = Map.TraceRay(origin, origin + dir * range);
        float best = wall.Fraction * range;
        object? victim = null;
        foreach (var t in Targets)
        {
            if (!t.Alive) continue;
            if (World.RayVsBox(origin, dir * range, t.Bounds.Min, t.Bounds.Max, out float f, out _) && f * range <= best)
            { best = f * range; victim = t; }
        }
        foreach (var o in Combatants)
        {
            if (o == shooter || !o.Alive) continue;
            var b = Box(o);
            if (World.RayVsBox(origin, dir * range, b.Min, b.Max, out float f, out _) && f * range <= best)
            { best = f * range; victim = o; }
        }
        var hit = origin + dir * best;
        if (tracer) Events.Add(new GameEvent(EventKind.Tracer, origin, hit, (int)weapon));
        if (victim is Target t2) Damage(t2, damage, dir, shooter);
        else if (victim is Player p2) DamagePlayer(p2, damage, dir, shooter, knock: false, weapon);
        else if (wall.Hit) Events.Add(new GameEvent(EventKind.Impact, hit, wall.Normal));
    }

    // ---- projectiles ----

    void UpdateProjectiles()
    {
        for (int i = Projectiles.Count - 1; i >= 0; i--)
        {
            var pr = Projectiles[i];
            if (pr.Kind == ProjectileKind.Grenade) pr.Vel.Y -= Settings.Gravity * Dt;

            var end = pr.Pos + pr.Vel * Dt;
            var half = pr.Kind == ProjectileKind.Grenade ? GrenadeHalf : NailHalf;
            var wall = Map.TraceBox(pr.Pos, end, half);
            var segEnd = wall.EndPos;

            object? hitObj = FirstHit(pr, pr.Pos, segEnd);
            if (hitObj != null)
            {
                Detonate(pr, hitObj, pr.Pos);
                Projectiles.RemoveAt(i);
                continue;
            }

            pr.Pos = segEnd;
            if (wall.Hit)
            {
                if (pr.Kind == ProjectileKind.Grenade)
                {
                    pr.Vel = PlayerMove.Clip(pr.Vel, wall.Normal, 1.5f);
                    if (pr.Vel.LengthSquared() > 50f * 50f) Events.Add(new GameEvent(EventKind.Bounce, pr.Pos));
                    if (wall.Normal.Y > 0.7f) pr.Vel *= new Vector3(0.7f, 0.5f, 0.7f); // floor friction
                }
                else
                {
                    Detonate(pr, null, pr.Pos);
                    Projectiles.RemoveAt(i);
                    continue;
                }
            }
            if (Time >= pr.Expires)
            {
                if (pr.Kind == ProjectileKind.Nail) Events.Add(new GameEvent(EventKind.Impact, pr.Pos, Vector3.UnitY));
                else Detonate(pr, null, pr.Pos);
                Projectiles.RemoveAt(i);
            }
        }
    }

    /// <summary>First target dummy or opponent (never the owner) touched by the projectile this step.</summary>
    object? FirstHit(Projectile pr, Vector3 a, Vector3 b)
    {
        var d = b - a;
        float len = d.Length();
        object? best = null; float bestT = float.MaxValue;

        void Consider(object o, Aabb box)
        {
            if (box.Overlaps(new Aabb(a - NailHalf, a + NailHalf))) { if (bestT > 0) { best = o; bestT = 0; } return; }
            if (len > 1e-6f && World.RayVsBox(a, d, box.Min - NailHalf, box.Max + NailHalf, out float f, out _) && f < bestT)
            { bestT = f; best = o; }
        }
        foreach (var t in Targets) if (t.Alive) Consider(t, t.Bounds);
        foreach (var o in Combatants) if (o != pr.Owner && o.Alive) Consider(o, Box(o));
        return best;
    }

    void Detonate(Projectile pr, object? direct, Vector3 at)
    {
        var owner = pr.Owner ?? Player;
        var dir = pr.Vel.LengthSquared() > 0 ? Vector3.Normalize(pr.Vel) : Vector3.UnitY;
        if (pr.Kind == ProjectileKind.Nail)
        {
            if (direct is Target t) Damage(t, pr.Damage, dir, owner);
            else if (direct is Player pl) DamagePlayer(pl, pr.Damage, dir, owner, knock: false, pr.Weapon);
            Events.Add(new GameEvent(EventKind.Impact, at, -dir));
            return;
        }
        // Rocket direct hits deal 100-120 damage on the entity struck (Q1: 100 + rand(0..20)).
        if (direct != null && pr.Kind == ProjectileKind.Rocket)
        {
            int dmg = 100 + (int)(Rng.NextDouble() * 20);
            if (direct is Target t) Damage(t, dmg, dir, owner, knock: false);
            else if (direct is Player pl) DamagePlayer(pl, dmg, dir, owner, knock: false, pr.Weapon);
        }
        RadiusDamage(at, pr.Splash, pr.Damage, direct, owner, pr.Weapon);
        Events.Add(new GameEvent(EventKind.Explosion, at));
    }

    /// <summary>Q1 T_RadiusDamage: points = dmg - 0.5 * distance(origin, target center); self damage halved.</summary>
    public void RadiusDamage(Vector3 at, float radius, int damage, object? ignore, Player? attacker = null, WeaponId? weapon = null)
    {
        attacker ??= Player;
        foreach (var v in Combatants.ToList())
        {
            if (!v.Alive || v == ignore) continue;
            var center = v.Move.Position;
            float pts = damage - 0.5f * Vector3.Distance(at, center);
            if (pts <= 0 || !Visible(at, center)) continue;
            if (v == attacker) pts *= 0.5f;
            Knock(ref v.Move.Velocity, center - at, pts);
            if (v.Move.Velocity.Y > 0) v.Move.OnGround = false;
            DamagePlayer(v, (int)pts, default, attacker, knock: false, weapon);
        }
        foreach (var t in Targets)
        {
            if (!t.Alive || t == ignore) continue;
            float pts = damage - 0.5f * Vector3.Distance(at, t.Origin);
            if (pts > 0 && Visible(at, t.Origin))
                Damage(t, (int)pts, Vector3.Normalize(t.Origin - at), attacker);
        }
    }

    bool Visible(Vector3 a, Vector3 b) => !Map.TraceRay(a, b).Hit;

    static void Knock(ref Vector3 v, Vector3 dir, float damage)
    {
        if (dir.LengthSquared() < 1e-6f) return;
        v += Vector3.Normalize(dir) * (damage * 8f);
    }

    void Damage(Target t, int dmg, Vector3 dir, Player by, bool knock = true)
    {
        if (!t.Alive) return;
        t.Health -= dmg;
        if (knock) t.Velocity += dir * dmg * 8f;
        var flags = (by == Player ? HurtFlags.ByHuman : 0) | (t.Health <= 0 ? HurtFlags.Killing : 0);
        Events.Add(new GameEvent(EventKind.Hurt, t.Origin, new Vector3(dmg, (float)flags, 0), 1000 + Targets.IndexOf(t)));
        if (t.Health <= 0)
        {
            by.Frags++;
            t.RespawnAt = Time + 3f;
            Events.Add(new GameEvent(EventKind.Kill, t.Origin));
        }
    }

    public void DamagePlayer(Player v, int dmg, Vector3 dir, Player? by, bool knock = true, WeaponId? weapon = null)
    {
        if (!v.Alive || dmg <= 0) return;
        if (by != null && by != v && Friendly(by, v)) return;      // no friendly fire
        if (knock) v.Move.Velocity += dir * dmg * 8f;
        if (!v.God) v.Health -= dmg;
        var flags = (by == Player ? HurtFlags.ByHuman : 0) | (v == Player ? HurtFlags.VictimHuman : 0) | (v.Health <= 0 ? HurtFlags.Killing : 0);
        Events.Add(new GameEvent(EventKind.Hurt, v.Move.Position, new Vector3(dmg, (float)flags, 0), v.Id));
        if (v.Health <= 0) Die(v, by, weapon);
    }

    /// <summary>Kill a combatant and announce it in the console, naming the weapon when known
    /// ("Bot1 killed You with the Rocket Launcher"; a self-kill reads "You suicided (Rocket Launcher)").</summary>
    public void Die(Player v, Player? by, WeaponId? weapon = null)
    {
        v.Health = Math.Min(v.Health, 0);
        v.Deaths++;
        if (by != null && by != v && Carrying(v) != null) by.Frags += 2;   // carrier kill bonus
        DropFlag(v); 
        v.RespawnAt = Time + RespawnDelay;
        string gun = weapon is { } w ? WeaponDef.Get(w).Name : "";
        if (by != null && by != v) { by.Frags++; Console.Print($"{by.Name} killed {v.Name}" + (gun.Length > 0 ? $" with the {gun}" : "")); }
        else { v.Frags--; Console.Print($"{v.Name} suicided" + (gun.Length > 0 ? $" ({gun})" : "")); }
        Events.Add(new GameEvent(EventKind.Kill, v.Move.Position, Arg: v == Player ? 1 : 0));
    }
}

public static class GameWorldExtensions
{
    /// <summary>Full restore: you, bots, dummies and projectiles.</summary>
    public static void Respawn(this GameWorld g)
    {
        g.Projectiles.Clear();
        g.Reset(g.Player, g.SpawnPoint);
        foreach (var b in g.Bots) g.RespawnPlayer(b.Body);
        foreach (var t in g.Targets) { t.Health = 100; t.Velocity = default; }
        foreach (var k in g.Pickups) k.Active = true;
    }
}
