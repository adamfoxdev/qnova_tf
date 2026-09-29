using System.Numerics;

namespace Qnova.Core;

public struct UserCmd
{
    public float Forward;   // -1..1
    public float Side;      // -1..1 (positive = right)
    public bool Jump;
    public bool Grapple;    // grappling-hook key held
    public float Yaw;       // degrees, 0 looks down -Z, positive turns left
    public float Pitch;     // degrees, positive looks up
}

/// <summary>Quake movement constants (sv_* defaults from Quake 1 / QuakeWorld).</summary>
public static class MoveVars
{
    public const float Gravity = 800f;
    public const float MaxSpeed = 320f;
    public const float StopSpeed = 100f;
    public const float Accelerate = 10f;
    public const float AirAccelerate = 10f;   // Q1 air-accel uses sv_accelerate with a 30 u/s wishspeed cap
    public const float AirWishCap = 30f;
    public const float Friction = 4f;
    public const float JumpSpeed = 270f;
    public const float StepHeight = 18f;
    public const float MoveScale = 400f;      // cl_forwardspeed
    public static readonly Vector3 Half = new(16f, 28f, 16f);
    public const float EyeHeight = 22f;       // above box center
}

/// <summary>Live-tunable copy of the movement constants; the console's sv_* cvars write into this.</summary>
public sealed class MoveSettings
{
    public float Gravity = MoveVars.Gravity;
    public float MaxSpeed = MoveVars.MaxSpeed;
    public float StopSpeed = MoveVars.StopSpeed;
    public float Accelerate = MoveVars.Accelerate;
    public float AirAccelerate = MoveVars.AirAccelerate;
    public float AirWishCap = MoveVars.AirWishCap;
    public float Friction = MoveVars.Friction;
    public float JumpSpeed = MoveVars.JumpSpeed;
    public float StepHeight = MoveVars.StepHeight;
    public float HookSpeed = 800f;   // pull speed toward the grapple anchor (Q3: 800)
}

/// <summary>Quake-style player physics: ground friction, accelerate, air-strafe, slide-move, step-up.</summary>
public sealed class PlayerMove
{
    public Vector3 Position;    // box center
    public Vector3 Velocity;
    public bool OnGround;
    public bool AutoHop;        // false = must release jump between jumps (classic Quake)
    bool _jumpHeld;

    public float SpeedScale = 1f;   // class run-speed multiplier
    public bool NoClip;         // fly through geometry (cheat)
    public Vector3? HookAnchor; // set while a grappling hook is attached: the player is reeled in toward it
    bool _swinging;             // hooked with jump held: the rope acts as a fixed-length line (pendulum) instead of reeling
    float _ropeLen;
    public bool Swinging => _swinging && HookAnchor != null;

    readonly World _world;
    readonly MoveSettings _s;
    public PlayerMove(World world, MoveSettings? settings = null) { _world = world; _s = settings ?? new MoveSettings(); }

    public static Vector3 ForwardFlat(float yawDeg)
    {
        float y = yawDeg * MathF.PI / 180f;
        return new Vector3(-MathF.Sin(y), 0, -MathF.Cos(y));
    }

    public static Vector3 RightFlat(float yawDeg)
    {
        float y = yawDeg * MathF.PI / 180f;
        return new Vector3(MathF.Cos(y), 0, -MathF.Sin(y));
    }

    public static Vector3 LookDir(float yawDeg, float pitchDeg)
    {
        float y = yawDeg * MathF.PI / 180f, p = pitchDeg * MathF.PI / 180f;
        return new Vector3(-MathF.Sin(y) * MathF.Cos(p), MathF.Sin(p), -MathF.Cos(y) * MathF.Cos(p));
    }

    public void Tick(in UserCmd cmd, float dt)
    {
        if (NoClip) { NoClipMove(cmd, dt); return; }
        CheckGround();

        // Jump (Q1: no auto-repeat while the key stays down).
        if (cmd.Jump && (AutoHop || !_jumpHeld) && OnGround)
        {
            Velocity.Y = _s.JumpSpeed;
            OnGround = false;
        }
        _jumpHeld = cmd.Jump;

        if (HookAnchor == null) _swinging = false;

        var wishvel = ForwardFlat(cmd.Yaw) * (cmd.Forward * MoveVars.MoveScale)
                    + RightFlat(cmd.Yaw) * (cmd.Side * MoveVars.MoveScale);
        float wishspeed = wishvel.Length();
        var wishdir = wishspeed > 1e-4f ? wishvel / wishspeed : Vector3.Zero;
        float maxSpeed = _s.MaxSpeed * SpeedScale;
        if (wishspeed > maxSpeed) wishspeed = maxSpeed;

        if (HookAnchor is { } anchor) { HookMove(anchor, dt, cmd.Jump, wishdir, wishspeed); return; }

        if (OnGround)
        {
            ApplyFriction(dt);
            Accelerate(wishdir, wishspeed, _s.Accelerate, dt);
            Velocity.Y = 0;
            if (Velocity.X != 0 || Velocity.Z != 0) WalkMove(dt);
        }
        else
        {
            AirAccelerate(wishdir, wishspeed, dt);
            Velocity.Y -= _s.Gravity * dt;
            FlyMove(dt, out _);
        }
        CheckGround();
    }

    /// <summary>While hooked. Default: reel toward the anchor (velocity eases to HookSpeed along the line, gravity overridden;
    /// within a few dozen units the player hangs). Holding jump instead makes the rope a fixed-length line so gravity swings the
    /// player like a pendulum, with air steering to pump the swing. Letting go of jump resumes the reel; releasing the hook keeps
    /// the momentum (slingshot).</summary>
    void HookMove(Vector3 anchor, float dt, bool swing, Vector3 wishdir, float wishspeed)
    {
        if (swing)
        {
            if (!_swinging) { _swinging = true; _ropeLen = MathF.Max(48f, Vector3.Distance(Position, anchor)); }
            SwingSteer(wishdir, wishspeed, dt);
            if (OnGround) ApplyFriction(dt); else Velocity.Y -= _s.Gravity * dt;
            FlyMove(dt, out _);
            ConstrainRope(anchor);
            CheckGround();
            return;
        }

        _swinging = false;
        OnGround = false;
        var to = anchor - Position;
        float dist = to.Length();
        var target = dist > 48f ? to / dist * _s.HookSpeed : Vector3.Zero;
        Velocity += (target - Velocity) * (1f - MathF.Exp(-9f * dt));
        FlyMove(dt, out _);
        var tr = _world.TraceBox(Position, Position - new Vector3(0, 1f, 0), MoveVars.Half);
        if (tr.Hit && tr.Normal.Y > 0.7f && Velocity.Y <= 0) OnGround = true;   // skimming a floor while reeling in
    }

    /// <summary>Stronger air control than normal so strafing can pump a swing (a normal air move is capped near 30 u/s).</summary>
    void SwingSteer(Vector3 wishdir, float wishspeed, float dt)
    {
        if (wishspeed < 1f) return;
        float add = MathF.Min(wishspeed, 260f) - Vector3.Dot(Velocity, wishdir);
        if (add <= 0) return;
        Velocity += wishdir * MathF.Min(_s.AirAccelerate * 0.6f * wishspeed * dt, add);
    }

    /// <summary>A rope only pulls, never pushes: past its length, move back onto the circle and drop the outward velocity.</summary>
    void ConstrainRope(Vector3 anchor)
    {
        var to = anchor - Position;
        float dist = to.Length();
        if (dist <= _ropeLen || dist < 1e-3f) return;
        var n = to / dist;
        var tr = _world.TraceBox(Position, Position + n * (dist - _ropeLen), MoveVars.Half);
        Position = tr.EndPos;
        float outward = -Vector3.Dot(Velocity, n);
        if (outward > 0) Velocity += n * outward;
    }

    void NoClipMove(in UserCmd cmd, float dt)
    {
        // Fly along the view direction; Space rises. No collision, no gravity.
        var look = LookDir(cmd.Yaw, cmd.Pitch);
        var wish = look * cmd.Forward + RightFlat(cmd.Yaw) * cmd.Side + (cmd.Jump ? Vector3.UnitY : Vector3.Zero);
        if (wish.LengthSquared() > 1f) wish = Vector3.Normalize(wish);
        Velocity = wish * (_s.MaxSpeed * 1.5f);
        Position += Velocity * dt;
        OnGround = false;
    }

    void ApplyFriction(float dt)
    {
        float speed = MathF.Sqrt(Velocity.X * Velocity.X + Velocity.Z * Velocity.Z);
        if (speed < 0.1f) { Velocity.X = 0; Velocity.Z = 0; return; }
        float control = speed < _s.StopSpeed ? _s.StopSpeed : speed;
        float newSpeed = MathF.Max(0f, speed - control * _s.Friction * dt);
        float k = newSpeed / speed;
        Velocity.X *= k; Velocity.Z *= k;
    }

    void Accelerate(Vector3 wishdir, float wishspeed, float accel, float dt)
    {
        float current = Vector3.Dot(Velocity, wishdir);
        float add = wishspeed - current;
        if (add <= 0) return;
        float a = MathF.Min(accel * dt * wishspeed, add);
        Velocity += wishdir * a;
    }

    /// <summary>The Quake trick: the cap applies to the *target* speed along wishdir, not total speed,
    /// so strafing at an angle to your velocity keeps adding speed (strafe-jumping).</summary>
    void AirAccelerate(Vector3 wishdir, float wishspeed, float dt)
    {
        float wishspd = MathF.Min(wishspeed, _s.AirWishCap);
        float current = Vector3.Dot(Velocity, wishdir);
        float add = wishspd - current;
        if (add <= 0) return;
        float a = MathF.Min(_s.AirAccelerate * wishspeed * dt, add);
        Velocity += wishdir * a;
    }

    void CheckGround()
    {
        if (Velocity.Y > 180f) { OnGround = false; return; }   // launched upward (jump / rocket)
        var tr = _world.TraceBox(Position, Position - new Vector3(0, 1f, 0), MoveVars.Half);
        OnGround = tr.Hit && tr.Normal.Y > 0.7f;
        if (OnGround) Position = tr.EndPos;
    }

    void WalkMove(float dt)
    {
        var start = Position;
        var startVel = Velocity;
        FlyMove(dt, out bool blocked);
        if (!blocked) return;

        // Blocked: try stepping up, moving, and dropping back down.
        var flatPos = Position; var flatVel = Velocity;
        Position = start; Velocity = startVel;
        var up = _world.TraceBox(Position, Position + new Vector3(0, _s.StepHeight, 0), MoveVars.Half);
        Position = up.EndPos;
        FlyMove(dt, out _);
        var down = _world.TraceBox(Position, Position - new Vector3(0, _s.StepHeight + 0.1f, 0), MoveVars.Half);
        if (down.Hit && down.Normal.Y < 0.7f) { Position = flatPos; Velocity = flatVel; return; }
        Position = down.EndPos;

        float flatDist = Horiz(flatPos - start), stepDist = Horiz(Position - start);
        if (stepDist <= flatDist) { Position = flatPos; Velocity = flatVel; }
        else Velocity.Y = 0;
    }

    static float Horiz(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Z * v.Z);

    /// <summary>Slide the box along surfaces (Quake's SV_FlyMove).</summary>
    void FlyMove(float dt, out bool blocked)
    {
        blocked = false;
        var orig = Velocity;
        float timeLeft = dt;
        Span<Vector3> planes = stackalloc Vector3[5];
        int np = 0;

        for (int bump = 0; bump < 4 && Velocity != Vector3.Zero; bump++)
        {
            var end = Position + Velocity * timeLeft;
            var tr = _world.TraceBox(Position, end, MoveVars.Half);
            if (tr.Fraction > 0) { Position = tr.EndPos; np = 0; }
            if (!tr.Hit) break;

            blocked = true;
            timeLeft -= timeLeft * tr.Fraction;
            if (np >= planes.Length) { Velocity = Vector3.Zero; break; }
            planes[np++] = tr.Normal;

            int i;
            Vector3 nv = Velocity;
            for (i = 0; i < np; i++)
            {
                nv = Clip(Velocity, planes[i]);
                int j;
                for (j = 0; j < np; j++)
                    if (j != i && Vector3.Dot(nv, planes[j]) < 0) break;
                if (j == np) break;
            }
            if (i != np) Velocity = nv;
            else
            {
                if (np != 2) { Velocity = Vector3.Zero; break; }
                var dir = Vector3.Cross(planes[0], planes[1]);
                if (dir.LengthSquared() < 1e-8f) { Velocity = Vector3.Zero; break; }
                dir = Vector3.Normalize(dir);
                Velocity = dir * Vector3.Dot(dir, Velocity);
            }
            if (Vector3.Dot(Velocity, orig) <= 0) { Velocity = Vector3.Zero; break; }
        }
    }

    public static Vector3 Clip(Vector3 v, Vector3 normal, float overbounce = 1f)
    {
        var o = v - normal * (Vector3.Dot(v, normal) * overbounce);
        if (MathF.Abs(o.X) < 0.1f) o.X = 0;
        if (MathF.Abs(o.Y) < 0.1f) o.Y = 0;
        if (MathF.Abs(o.Z) < 0.1f) o.Z = 0;
        return o;
    }
}
