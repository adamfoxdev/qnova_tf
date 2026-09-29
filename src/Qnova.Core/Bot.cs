using System.Numerics;

namespace Qnova.Core;

/// <summary>An AI opponent: a <see cref="Player"/> body driven by the same movement and weapon rules as the
/// human. It sees you by line-of-sight, hunts your last known position, strafes and jumps in a fight,
/// leads projectile shots and aims rockets at your feet. Skill (1-5) scales aim error, reaction and turn speed.</summary>
public sealed class Bot
{
    public readonly Player Body;
    public BotRole Role;
    readonly Random _rng;

    Vector3 _goal, _lastSeen;
    float _goalUntil, _lastSeenTime, _reactAt, _strafeUntil, _nextJump, _weaponCheck, _errUntil, _stuckCheck, _detourUntil;
    bool _hasLast, _wasSeeing;
    Pickup? _goalPickup;      // non-null while heading for a specific item
    float _strafe = 1, _errYaw, _errPitch, _detour;
    Vector3 _stuckPos;

    public Bot(Player body, int seed) { Body = body; _rng = new Random(seed); }

    float Rand(float a, float b) => a + (float)_rng.NextDouble() * (b - a);

    public static float YawOf(Vector3 d) => MathF.Atan2(-d.X, -d.Z) * 180f / MathF.PI;
    public static float PitchOf(Vector3 d) => MathF.Asin(Math.Clamp(d.Y / d.Length(), -1f, 1f)) * 180f / MathF.PI;

    static float Wrap(float a) { a %= 360f; if (a > 180f) a -= 360f; if (a < -180f) a += 360f; return a; }
    static float Approach(float cur, float target, float maxStep) => cur + Math.Clamp(Wrap(target - cur), -maxStep, maxStep);

    public void Update(GameWorld g)
    {
        var me = Body; float t = g.Time;
        if (!me.Alive)
        {
            if (t >= me.RespawnAt) g.RespawnPlayer(me);
            return;
        }
        if (!g.BotAi) { me.Move.Tick(default, GameWorld.Dt); return; }

        int skill = Math.Clamp(g.BotSkill, 1, 5);
        // Bots don't scavenge for ammo; they simply never run dry.
        foreach (var w in me.Owned)
        {
            var def0 = WeaponDef.Get(w);
            int floor = def0.Ammo switch { AmmoType.Shells => 20, AmmoType.Nails => 100, AmmoType.Rockets => 10, AmmoType.Cells => 100, AmmoType.Slugs => 10, _ => 0 };
            if (floor > 0 && me.Ammo(def0.Ammo) < floor) me.AddAmmo(def0.Ammo, floor - me.Ammo(def0.Ammo));
        }

        var enemy = g.Player;
        var pos = me.Move.Position;
        bool see = false; float dist = 0;
        if (g.IsCtf)
        {
            // team play: fight the nearest visible opponent, human or bot
            float bestD = float.MaxValue;
            foreach (var o in g.Combatants)
            {
                if (o == me || !o.Alive || g.Friendly(me, o)) continue;
                float d = Vector3.Distance(pos, o.Move.Position);
                if (d < bestD && d < 3500 && (!g.Map.TraceRay(me.Eye, o.Move.Position).Hit || !g.Map.TraceRay(me.Eye, o.Eye).Hit))
                { bestD = d; enemy = o; see = true; dist = d; }
            }
        }
        else if (enemy.Alive)
        {
            dist = Vector3.Distance(pos, enemy.Move.Position);
            see = dist < 3500 && (!g.Map.TraceRay(me.Eye, enemy.Move.Position).Hit || !g.Map.TraceRay(me.Eye, enemy.Eye).Hit);
        }
        if (see)
        {
            if (!_wasSeeing) _reactAt = t + Rand(0.15f, 0.9f) * (6 - skill) / 3f;   // reaction time shrinks with skill
            _lastSeen = enemy.Move.Position; _lastSeenTime = t; _hasLast = true;
        }
        _wasSeeing = see;

        var cmd = new UserCmd();
        float desiredYaw = me.Yaw, desiredPitch = 0f;
        bool wantFire = false;

        // a flag carrier runs for home instead of duelling, unless the enemy is right on top of them
        bool flee = see && g.IsCtf && g.Carrying(me) != null && dist > 350f;
        if (see && !flee)
        {
            ChooseWeapon(me, dist, t, skill);
            var def = WeaponDef.Get(me.Current);
            var aim = AimPoint(enemy, def, dist, skill);
            var d = aim - me.Eye;
            desiredYaw = YawOf(d); desiredPitch = PitchOf(d);
            wantFire = t >= _reactAt;

            // Strafe around the target; close in when far, back off when near.
            if (t >= _strafeUntil) { _strafe = _rng.Next(2) == 0 ? -1f : 1f; _strafeUntil = t + Rand(0.5f, 1.6f); }
            cmd.Side = _strafe;
            cmd.Forward = dist > 700 ? 1f : dist < 300 ? -0.8f : 0f;
            if (skill >= 2 && me.Move.OnGround && t >= _nextJump) { cmd.Jump = true; _nextJump = t + Rand(1.2f, 3.5f); }

            // Don't strafe into walls.
            var wish = PlayerMove.RightFlat(me.Yaw) * cmd.Side + PlayerMove.ForwardFlat(me.Yaw) * cmd.Forward;
            if (wish.LengthSquared() > 0.01f && Blocked(g, pos, Vector3.Normalize(wish), 48f, out _))
            { _strafe = -_strafe; _strafeUntil = t + Rand(0.4f, 1f); cmd.Side = _strafe; }
        }
        else
        {
            Navigate(g, me, t, ref cmd, out desiredYaw);
        }

        // Turn toward the desired heading at a skill-limited rate, with a little re-rolled aim error.
        if (t >= _errUntil)
        {
            float e = (6 - skill) * 2.2f;   // degrees of aim wobble: 11 at skill 1 .. 2.2 at skill 5
            _errYaw = Rand(-e, e); _errPitch = Rand(-e, e) * 0.6f; _errUntil = t + 0.25f;
        }
        float rate = (220f + 110f * skill) * GameWorld.Dt;
        me.Yaw = Approach(me.Yaw, desiredYaw + (see ? _errYaw : 0f), rate);
        me.Pitch = Approach(me.Pitch, Math.Clamp(desiredPitch + (see ? _errPitch : 0f), -80f, 80f), rate);
        cmd.Yaw = me.Yaw; cmd.Pitch = me.Pitch;

        me.Move.Tick(cmd, GameWorld.Dt);

        if (wantFire)
        {
            float err = MathF.Abs(Wrap(desiredYaw - me.Yaw)) + MathF.Abs(desiredPitch - me.Pitch);
            if (err < 6f + 3f * (5 - skill) && t >= me.NextFire)
            {
                g.TryFire(me);
                // Low-skill bots hesitate between shots (0-2s at skill 1, none at skill 5).
                me.NextFire += Rand(0f, 1f) * (5 - skill) * 0.5f;
            }
        }
    }

    void ChooseWeapon(Player me, float dist, float t, int skill)
    {
        if (t < _weaponCheck) return;
        _weaponCheck = t + 0.8f;
        // Close: double shotgun. Mid range: lightning up to its reach, rockets beyond. Far: the rail (better bots only).
        var prefs = dist < 200 ? Close : dist < 700 ? Mid : dist < 1500 ? Far : (skill >= 3 ? Sniping : Distant);
        foreach (var w in prefs)
            if (me.Owned.Contains(w) && me.Ammo(WeaponDef.Get(w).Ammo) >= WeaponDef.Get(w).AmmoPerShot) { me.Current = w; return; }
    }

    // Weapon preferences by range; class loadouts in Team Fortress own only some of these.
    static readonly WeaponId[] Close = { WeaponId.SuperShotgun, WeaponId.SuperNailgun, WeaponId.Nailgun, WeaponId.Shotgun, WeaponId.Axe };
    static readonly WeaponId[] Mid = { WeaponId.LightningGun, WeaponId.RocketLauncher, WeaponId.SuperNailgun, WeaponId.Nailgun, WeaponId.GrenadeLauncher, WeaponId.SuperShotgun, WeaponId.Shotgun, WeaponId.Axe };
    static readonly WeaponId[] Far = { WeaponId.RocketLauncher, WeaponId.SuperNailgun, WeaponId.Nailgun, WeaponId.GrenadeLauncher, WeaponId.Railgun, WeaponId.Shotgun, WeaponId.Axe };
    static readonly WeaponId[] Distant = { WeaponId.SuperNailgun, WeaponId.Nailgun, WeaponId.RocketLauncher, WeaponId.GrenadeLauncher, WeaponId.Shotgun, WeaponId.Axe };
    static readonly WeaponId[] Sniping = { WeaponId.Railgun, WeaponId.RocketLauncher, WeaponId.SuperNailgun, WeaponId.Nailgun, WeaponId.GrenadeLauncher, WeaponId.Shotgun, WeaponId.Axe };

    static Vector3 AimPoint(Player enemy, WeaponDef def, float dist, int skill)
    {
        var target = enemy.Move.Position;
        if (def.ProjectileSpeed > 0)
        {
            // Lead the target; skill scales how much of the ideal lead the bot applies.
            float travel = dist / def.ProjectileSpeed;
            var vel = new Vector3(enemy.Move.Velocity.X, 0, enemy.Move.Velocity.Z);
            target += vel * travel * (0.4f + 0.12f * skill);
            // Rockets: aim at the floor by their feet so splash lands even on a miss.
            if (def.Mode == FireMode.Rocket && enemy.Move.OnGround) target.Y -= 22f;
        }
        return target;
    }

    static float FlatDist(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>Hurt bots go for the nearest health; otherwise roughly half the time they head for a random ready item.</summary>
    Pickup? PickPickup(GameWorld g, Player me)
    {
        Pickup? best = null; float bestD = float.MaxValue;
        bool hurt = me.Health < 70;
        var ready = new List<Pickup>();
        foreach (var k in g.Pickups)
        {
            if (!k.Active) continue;
            if (MathF.Abs(k.Position.Y - me.Move.Position.Y) > 70f) continue;   // steering can't climb, so only chase items on this level
            if (hurt) { if (k.Kind != PickupKind.Health) continue; float d = FlatDist(me.Move.Position, k.Position); if (d < bestD) { bestD = d; best = k; } }
            else ready.Add(k);
        }
        if (hurt) return best;
        return ready.Count > 0 && _rng.NextDouble() < 0.5 ? ready[_rng.Next(ready.Count)] : null;
    }

    static bool Blocked(GameWorld g, Vector3 pos, Vector3 dir, float len, out Trace tr)
    {
        tr = g.Map.TraceBox(pos, pos + dir * len, MoveVars.Half);
        return tr.Hit && tr.Normal.Y < 0.7f;
    }

    void Navigate(GameWorld g, Player me, float t, ref UserCmd cmd, out float desiredYaw)
    {
        var pos = me.Move.Position;
        if (g.IsCtf && CtfGoal(g, me, t) is { } obj)
        {
            _goal = obj; _goalPickup = null;
            if (t >= _stuckCheck)
            {
                if (_stuckCheck > 0 && Vector3.Distance(pos, _stuckPos) < 30f) { _detour = Rand(-100f, 100f); _detourUntil = t + 1.0f; }
                _stuckPos = pos; _stuckCheck = t + 1.5f;
            }
            Steer(g, me, t, ref cmd, out desiredYaw);
            return;
        }
        bool chasing = _hasLast && t - _lastSeenTime < 8f && Vector3.Distance(pos, _lastSeen) > 150f;
        if (chasing) _goal = _lastSeen;
        else if (t >= _goalUntil || (_goalPickup != null ? !_goalPickup.Active : FlatDist(pos, _goal) < 120f))
        {
            var want = PickPickup(g, me);
            _goalPickup = want;
            if (want != null) { _goal = want.Position; _goalUntil = t + 10f; }
            else
            for (int tries = 0; tries < 12; tries++)
            {
                float lim = MathF.Max(300f, g.MapHalf - 250f);      // wander anywhere inside the current map
                _goal = new Vector3(Rand(-lim, lim), 28f, Rand(-lim, lim));
                if (g.Map.IsEmpty(_goal, MoveVars.Half)) break;   // don't wander into pillars and walls
            }
            if (want == null) _goalUntil = t + 6f;
        }

        // Stuck detection: barely moved in 1.5s -> new goal and a random detour.
        if (t >= _stuckCheck)
        {
            if (_stuckCheck > 0 && Vector3.Distance(pos, _stuckPos) < 30f)
            {
                _goalUntil = 0; _detour = Rand(-100f, 100f); _detourUntil = t + 1.0f;
            }
            _stuckPos = pos; _stuckCheck = t + 1.5f;
        }

        Steer(g, me, t, ref cmd, out desiredYaw);
    }

    Vector3 _patrol; float _patrolUntil;

    /// <summary>Where a capture-the-flag bot should be heading, or null to fall back on the ordinary roaming.</summary>
    Vector3? CtfGoal(GameWorld g, Player me, float t)
    {
        var own = g.FlagOf(me.Team); var theirs = g.FlagOf(me.Team.Other());
        if (own == null || theirs == null) return null;
        if (g.Carrying(me) != null) return own.Home;                                           // bring it home
        if (own.State == FlagState.Dropped) return own.Pos;                                    // send our flag back
        if (own.State == FlagState.Carried && own.Carrier != null) return own.Carrier.Move.Position;   // hunt the thief
        if (Role == BotRole.Attack)
        {
            if (theirs.State == FlagState.Carried && theirs.Carrier != null && g.Friendly(me, theirs.Carrier))
                return theirs.Carrier.Move.Position;                                           // escort our carrier
            return theirs.Pos;
        }
        if (t >= _patrolUntil)                                                                 // defend: loiter around our base
        {
            _patrol = own.Home + new Vector3(Rand(-350f, 350f), 0, Rand(-350f, 350f));
            _patrolUntil = t + Rand(3f, 6f);
        }
        return _patrol;
    }

    void Steer(GameWorld g, Player me, float t, ref UserCmd cmd, out float desiredYaw)
    {
        var pos = me.Move.Position;
        var flat = new Vector3(_goal.X - pos.X, 0, _goal.Z - pos.Z);
        if (flat.LengthSquared() < 1f) flat = PlayerMove.ForwardFlat(me.Yaw);
        float heading = YawOf(flat) + (t < _detourUntil ? _detour : 0f);

        // Look ahead; hop small obstacles, otherwise swing toward whichever side is clear.
        var ahead = PlayerMove.ForwardFlat(heading);
        if (Blocked(g, pos, ahead, 72f, out _))
        {
            var high = g.Map.TraceBox(pos + new Vector3(0, 44f, 0), pos + new Vector3(0, 44f, 0) + ahead * 72f, MoveVars.Half);
            if (!high.Hit && me.Move.OnGround) cmd.Jump = true;
            else
            {
                foreach (float off in new[] { 60f, -60f, 100f, -100f, 140f, -140f })
                    if (!Blocked(g, pos, PlayerMove.ForwardFlat(heading + off), 96f, out _))
                    { _detour = off; _detourUntil = t + 0.8f; heading += off; break; }
            }
        }
        cmd.Forward = 1f;
        desiredYaw = heading;
    }
}
