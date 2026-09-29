using System.Numerics;

namespace Qnova.Core;

public sealed class Player
{
    public PlayerMove Move;
    public float Yaw, Pitch;
    public int Health = 100, MaxHealth = 100;
    public int Shells = 25, Nails = 100, Rockets = 10, Cells = 50, Slugs = 5;
    public HashSet<WeaponId> Owned = new() { WeaponId.Axe, WeaponId.Shotgun };
    public const int MaxShells = 100, MaxNails = 200, MaxRockets = 100, MaxCells = 200, MaxSlugs = 50;
    public WeaponId Current = WeaponId.Shotgun;
    public float NextFire;
    public int Frags, Deaths;
    public bool God;
    public string Name = "Player";
    public int Id;                // 0 = the human, 1.. = bots (used to tell victims apart in events)
    public bool IsBot;
    public Team Team;             // None outside capture the flag
    public PlayerClass Class;     // current Team Fortress class (None outside that mode)
    public PlayerClass NextClass = PlayerClass.Soldier;   // class you will spawn as next
    public float HealBuffer;      // fractional healing carried between ticks
    public readonly Hook Hook = new();
    public bool GrappleHeld;      // previous-tick state of the grapple key (for press detection)
    public float PadCooldownUntil;
    public float RespawnAt;   // when dead: time at which to respawn

    public Player(World w, Vector3 spawn, MoveSettings? settings = null) { Move = new PlayerMove(w, settings) { Position = spawn }; }

    public Vector3 Eye => Move.Position + new Vector3(0, MoveVars.EyeHeight, 0);
    public Vector3 Look => PlayerMove.LookDir(Yaw, Pitch);
    public bool Alive => Health > 0;

    public int Ammo(AmmoType t) => t switch { AmmoType.Shells => Shells, AmmoType.Nails => Nails, AmmoType.Rockets => Rockets, AmmoType.Cells => Cells, AmmoType.Slugs => Slugs, _ => int.MaxValue };

    public int MaxAmmo(AmmoType t) => t switch { AmmoType.Shells => MaxShells, AmmoType.Nails => MaxNails, AmmoType.Rockets => MaxRockets, AmmoType.Cells => MaxCells, AmmoType.Slugs => MaxSlugs, _ => 0 };

    /// <summary>Add ammo up to the cap; returns how much was actually accepted.</summary>
    public int AddAmmo(AmmoType t, int n)
    {
        int room = MaxAmmo(t) - Ammo(t);
        int take = Math.Clamp(n, 0, Math.Max(0, room));
        switch (t)
        {
            case AmmoType.Shells: Shells += take; break;
            case AmmoType.Nails: Nails += take; break;
            case AmmoType.Rockets: Rockets += take; break;
            case AmmoType.Cells: Cells += take; break;
            case AmmoType.Slugs: Slugs += take; break;
        }
        return take;
    }

    public void Spend(AmmoType t, int n)
    {
        switch (t)
        {
            case AmmoType.Shells: Shells -= n; break;
            case AmmoType.Nails: Nails -= n; break;
            case AmmoType.Rockets: Rockets -= n; break;
            case AmmoType.Cells: Cells -= n; break;
            case AmmoType.Slugs: Slugs -= n; break;
        }
    }
}

/// <summary>A stationary damageable dummy that respawns.</summary>
public sealed class Target
{
    public Vector3 Origin;
    public Vector3 Velocity;
    public int Health = 100;
    public float RespawnAt;
    public Vector3 Half = new(16, 28, 16);
    public Aabb Bounds => Aabb.FromCenter(Origin, Half);
    public bool Alive => Health > 0;
}

public enum ProjectileKind { Nail, Grenade, Rocket }

public sealed class Projectile
{
    public ProjectileKind Kind;
    public Vector3 Pos, Vel;
    public Player? Owner;
    public WeaponId Weapon;     // which gun fired it (for kill messages)
    public float Expires;
    public int Damage;
    public float Splash;
}

public enum EventKind { Explosion, Impact, Tracer, Hurt, Kill, Shot, Bounce, DryFire, Pickup, ItemRespawn, HookFire, HookAttach, JumpPad, FlagTaken, FlagDropped, FlagReturned, FlagCaptured }

/// <summary>Hurt events: A = victim centre, B.X = damage dealt, B.Y = <see cref="HurtFlags"/>, Arg = victim id (players 0.., dummies 1000 + index).
/// Arg carries the (int)WeaponId for Shot/DryFire and the (int)PickupKind for Pickup/ItemRespawn (B.X = 1 when the human collected it).</summary>
public readonly record struct GameEvent(EventKind Kind, Vector3 A, Vector3 B = default, int Arg = 0);

public enum PickupKind { Health, Shells, Nails, Rockets, Weapon, Cells, Slugs }

/// <summary>A floor item. After being collected it is inactive until <see cref="RespawnAt"/>.</summary>
public sealed class Pickup
{
    public static readonly Vector3 Half = new(16, 16, 16);
    public PickupKind Kind;
    public WeaponId Weapon;       // when Kind == Weapon
    public int Amount;            // health / ammo quantity (weapons: ammo that comes with the gun)
    public Vector3 Position;      // box centre
    public bool Active = true;
    public float RespawnAt;
    public Aabb Bounds => Aabb.FromCenter(Position, Half);

    public string Name => Kind switch
    {
        PickupKind.Health => $"{Amount} health",
        PickupKind.Shells => $"{Amount} shells",
        PickupKind.Nails => $"{Amount} nails",
        PickupKind.Rockets => $"{Amount} rockets",
        PickupKind.Cells => $"{Amount} cells",
        PickupKind.Slugs => $"{Amount} slugs",
        _ => WeaponDef.Get(Weapon).Name,
    };

    /// <summary>Ammo type a weapon pickup comes with (and how much).</summary>
    public static (AmmoType Type, int Amount) WeaponAmmo(WeaponId w) => w switch
    {
        WeaponId.Shotgun or WeaponId.SuperShotgun => (AmmoType.Shells, 10),
        WeaponId.Nailgun or WeaponId.SuperNailgun => (AmmoType.Nails, 30),
        WeaponId.GrenadeLauncher or WeaponId.RocketLauncher => (AmmoType.Rockets, 5),
        WeaponId.LightningGun => (AmmoType.Cells, 50),
        WeaponId.Railgun => (AmmoType.Slugs, 10),
        _ => (AmmoType.None, 0),
    };
}

public enum HookState { None, Flying, Attached }

/// <summary>A grappling hook: flies straight until it strikes a surface, then reels its owner in while the key is held.</summary>
public sealed class Hook
{
    public HookState State;
    public Vector3 Pos;          // tip (flying) or anchor (attached)
    public Vector3 Dir;
    public float Traveled;
    public float NextAt;         // earliest time it can be fired again
    public float CheckAt;        // next progress check while attached
    public float LastDist;
    public Vector3 Anchor => Pos;
}

/// <summary>Quake 3 style launch pad: touching the trigger flings you along a ballistic arc whose apex is <see cref="Target"/>.</summary>
public sealed class JumpPad
{
    public Aabb Trigger;
    public Vector3 Target;       // the apex of the flight
    public Vector3 Center => Trigger.Center;

    /// <summary>Q3's AimAtTarget: rise to the target height in time t = sqrt(2h/g), covering the horizontal distance in that time.</summary>
    public Vector3 LaunchVelocity(Vector3 from, float gravity)
    {
        var d = Target - from;
        float h = MathF.Max(32f, d.Y);
        float t = MathF.Sqrt(2f * h / MathF.Max(1f, gravity));
        return new Vector3(d.X / t, gravity * t, d.Z / t);
    }
}

[Flags]
public enum HurtFlags { None = 0, ByHuman = 1, VictimHuman = 2, Killing = 4 }
