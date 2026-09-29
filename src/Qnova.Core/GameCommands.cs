using System.Globalization;
using System.Numerics;

namespace Qnova.Core;

/// <summary>Registers the simulation's cvars and commands on <see cref="GameWorld.Console"/>.</summary>
public static class GameCommands
{
    static readonly (string Name, WeaponId Id)[] Aliases =
    {
        ("axe", WeaponId.Axe), ("shotgun", WeaponId.Shotgun), ("sg", WeaponId.Shotgun),
        ("supershotgun", WeaponId.SuperShotgun), ("doubleshotgun", WeaponId.SuperShotgun), ("ssg", WeaponId.SuperShotgun),
        ("nailgun", WeaponId.Nailgun), ("ng", WeaponId.Nailgun),
        ("supernailgun", WeaponId.SuperNailgun), ("sng", WeaponId.SuperNailgun),
        ("grenadelauncher", WeaponId.GrenadeLauncher), ("gl", WeaponId.GrenadeLauncher),
        ("rocketlauncher", WeaponId.RocketLauncher), ("rl", WeaponId.RocketLauncher),
        ("lightninggun", WeaponId.LightningGun), ("lightning", WeaponId.LightningGun), ("lg", WeaponId.LightningGun),
        ("railgun", WeaponId.Railgun), ("rail", WeaponId.Railgun), ("rg", WeaponId.Railgun),
    };

    public static bool TryWeapon(string s, out WeaponId id)
    {
        id = default;
        if (int.TryParse(s, out int n) && n >= 1 && n <= WeaponDef.All.Length) { id = (WeaponId)(n - 1); return true; }
        foreach (var (name, w) in Aliases) if (name.Equals(s, StringComparison.OrdinalIgnoreCase)) { id = w; return true; }
        return false;
    }

    static bool F(string s, out float v) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    public static void Install(GameWorld g)
    {
        var c = g.Console; var p = g.Player; var s = g.Settings;

        // ---- movement variables (free to change, like Quake's sv_*) ----
        c.AddCvar("sv_gravity", s.Gravity, "Gravity in units/s^2", v => s.Gravity = v);
        c.AddCvar("sv_maxspeed", s.MaxSpeed, "Ground speed cap", v => s.MaxSpeed = v);
        c.AddCvar("sv_accelerate", s.Accelerate, "Ground acceleration", v => s.Accelerate = v);
        c.AddCvar("sv_airaccelerate", s.AirAccelerate, "Air acceleration", v => s.AirAccelerate = v);
        c.AddCvar("sv_aircap", s.AirWishCap, "Air wish-speed cap (strafe-jump limit)", v => s.AirWishCap = v);
        c.AddCvar("sv_friction", s.Friction, "Ground friction", v => s.Friction = v);
        c.AddCvar("sv_stopspeed", s.StopSpeed, "Speed below which friction is constant", v => s.StopSpeed = v);
        c.AddCvar("sv_jumpspeed", s.JumpSpeed, "Vertical speed of a jump", v => s.JumpSpeed = v);
        c.AddCvar("sv_stepheight", s.StepHeight, "Max ledge height walked up", v => s.StepHeight = v);
        c.AddCvar("sv_hookspeed", s.HookSpeed, "Grappling hook reel-in speed", v => s.HookSpeed = Math.Max(0f, v));
        c.AddCvar("sv_hookrange", g.HookRange, "Grappling hook maximum range", v => g.HookRange = Math.Max(0f, v));
        c.AddCvar("sv_autohop", 0, "Hold jump to keep bunny-hopping (0/1)", v => p.Move.AutoHop = v != 0);

        // ---- bots ----
        c.AddCvar("sv_pickup_respawn", g.PickupRespawn, "Seconds before a collected pickup returns", v => g.PickupRespawn = Math.Max(0f, v));
        c.AddCvar("bot_ai", 1, "Bots think and act (0 freezes them)", v => g.BotAi = v != 0);
        c.AddCvar("bot_skill", g.BotSkill, "Bot skill 1 (easy) to 5 (hard)", v => g.BotSkill = (int)Math.Clamp(v, 1, 5));

        // ---- cheat variables ----
        c.AddCvar("sv_infiniteammo", 0, "Weapons use no ammo (0/1)", v => g.InfiniteAmmo = v != 0, cheat: true);
        c.AddCvar("sv_god", 0, "No self damage (0/1)", v => p.God = v != 0, cheat: true);
        c.AddCvar("sv_noclip", 0, "Fly through walls (0/1)", v => p.Move.NoClip = v != 0, cheat: true);

        // ---- commands ----
        void ToggleCheat(string name, string cvar, string label)
        {
            c.AddCommand(name, $"{name} [0|1]", $"Toggle {label}", a =>
            {
                float next = a.Length > 0 && F(a[0], out var v) ? (v != 0 ? 1 : 0) : (c.Get(cvar) == 0 ? 1 : 0);
                c.Execute($"{cvar} {next}", echo: false);
            }, cheat: true);
        }
        ToggleCheat("god", "sv_god", "god mode (no self damage)");
        ToggleCheat("noclip", "sv_noclip", "noclip (fly through walls)");
        c.AddCommand("weapon", "weapon <1-9|name>", "Select a weapon", a =>
        {
            if (a.Length < 1 || !TryWeapon(a[0], out var w)) { c.Print("usage: weapon <1-9|axe|sg|ssg|ng|sng|gl|rl|lg|rail>"); return; }
            if (!p.Owned.Contains(w)) { c.Print($"you don't have the {WeaponDef.Get(w).Name}"); return; }
            p.Current = w; c.Print($"weapon: {WeaponDef.Get(w).Name}");
        });
        c.AddCommand("give", "give <all|health|ammo|shells|nails|rockets|cells|slugs|weapon> [amount]", "Give items", a =>
        {
            if (a.Length < 1) { c.Print("usage: give <all|health|ammo|shells|nails|rockets|cells|slugs|weapon> [amount]"); return; }
            int amt = a.Length > 1 && int.TryParse(a[1], out var n) ? n : -1;
            switch (a[0].ToLowerInvariant())
            {
                case "all":
                    p.Owned = new HashSet<WeaponId>(Enum.GetValues<WeaponId>());
                    p.Shells = Player.MaxShells; p.Nails = Player.MaxNails; p.Rockets = Player.MaxRockets; p.Cells = Player.MaxCells; p.Slugs = Player.MaxSlugs; p.Health = p.MaxHealth;
                    c.Print("gave everything"); break;
                case "health": p.Health = amt >= 0 ? amt : p.MaxHealth; c.Print($"health {p.Health}"); break;
                case "ammo": { int k = amt >= 0 ? amt : 100; p.Shells += k; p.Nails += k; p.Rockets += k; p.Cells += k; p.Slugs += k; c.Print($"+{k} of each ammo"); break; }
                case "shells": p.Shells += amt >= 0 ? amt : 25; c.Print($"shells {p.Shells}"); break;
                case "nails": p.Nails += amt >= 0 ? amt : 100; c.Print($"nails {p.Nails}"); break;
                case "rockets": p.Rockets += amt >= 0 ? amt : 10; c.Print($"rockets {p.Rockets}"); break;
                case "cells": p.Cells += amt >= 0 ? amt : 50; c.Print($"cells {p.Cells}"); break;
                case "slugs": p.Slugs += amt >= 0 ? amt : 10; c.Print($"slugs {p.Slugs}"); break;
                default:
                    if (TryWeapon(a[0], out var w)) { p.Owned.Add(w); p.Current = w; c.Print($"gave {WeaponDef.Get(w).Name}"); }
                    else c.Print($"don't know how to give \"{a[0]}\"");
                    break;
            }
        }, cheat: true);
        c.AddCommand("setpos", "setpos <x> <y> <z>", "Teleport (Y is up)", a =>
        {
            if (a.Length < 3 || !F(a[0], out var x) || !F(a[1], out var y) || !F(a[2], out var z)) { c.Print("usage: setpos <x> <y> <z>"); return; }
            p.Move.Position = new Vector3(x, y, z); p.Move.Velocity = default;
        }, cheat: true);
        c.AddCommand("getpos", "getpos", "Print position and velocity", a =>
        {
            var m = p.Move;
            c.Print($"pos {GameConsole.Fmt(m.Position.X)} {GameConsole.Fmt(m.Position.Y)} {GameConsole.Fmt(m.Position.Z)}  vel {GameConsole.Fmt(m.Velocity.X)} {GameConsole.Fmt(m.Velocity.Y)} {GameConsole.Fmt(m.Velocity.Z)}");
        });
        void GiveArsenal()
        {
            p.Owned = new HashSet<WeaponId>(Enum.GetValues<WeaponId>());
            p.Shells = Player.MaxShells; p.Nails = Player.MaxNails; p.Rockets = Player.MaxRockets; p.Cells = Player.MaxCells; p.Slugs = Player.MaxSlugs;
            c.Print($"all weapons and full ammo ({Player.MaxShells} shells, {Player.MaxNails} nails, {Player.MaxRockets} rockets, {Player.MaxCells} cells, {Player.MaxSlugs} slugs)");
        }
        c.AddCommand("giveall", "giveall", "Give every weapon and full ammo (health untouched; 'give all' also heals)", a => GiveArsenal(), cheat: true);
        c.AddCommand("impulse", "impulse 9", "Quake-style: impulse 9 gives every weapon and full ammo", a =>
        {
            if (a.Length > 0 && a[0] == "9") GiveArsenal(); else c.Print("only impulse 9 is supported");
        }, cheat: true);
        c.AddCommand("kill", "kill", "Suicide", a => g.Die(p, p));
        c.AddCommand("bot_add", "bot_add [count]", "Add bots", a =>
        {
            int n = a.Length > 0 && int.TryParse(a[0], out var k) ? Math.Clamp(k, 1, 16) : 1;
            for (int i = 0; i < n; i++) g.AddBot();
            c.Print($"{g.Bots.Count} bot(s) in game");
        });
        c.AddCommand("bot_removeall", "bot_removeall", "Remove every bot", a => { c.Print($"removed {g.Bots.Count} bot(s)"); g.Bots.Clear(); });
        c.AddCommand("bots", "bots", "List bots and the scoreboard", a =>
        {
            foreach (var pl in g.Combatants)
                c.Print($"{pl.Name,-8} frags {pl.Frags,3}  deaths {pl.Deaths,3}  health {Math.Max(0, pl.Health),3}{(pl.Alive ? "" : "  (dead)")}");
        });
        c.AddCommand("respawn", "respawn", "Reset player, ammo and dummies", a => { g.Respawn(); c.Print("respawned"); });
        c.AddCommand("spawn", "spawn [count]", "Spawn target dummies where you're looking", a =>
        {
            int n = a.Length > 0 && int.TryParse(a[0], out var k) ? Math.Clamp(k, 1, 20) : 1;
            var tr = g.Map.TraceBox(p.Eye, p.Eye + p.Look * 4096f, MoveVars.Half);
            for (int i = 0; i < n; i++)
                g.Targets.Add(new Target { Origin = tr.EndPos + new Vector3((i % 5) * 40f, 0, (i / 5) * 40f) });
            c.Print($"spawned {n} dummy(s)");
        }, cheat: true);
        c.AddCommand("killtargets", "killtargets", "Remove all dummies", a => { c.Print($"removed {g.Targets.Count} dummies"); g.Targets.Clear(); }, cheat: true);
        // ---- key bindings ----
        var kb = g.Bindings;
        string ActionNames() => string.Join(" ", KeyBindings.All.Select(KeyBindings.Name));
        c.AddCommand("bind", "bind <key> <action>", "Bind a key or mouse button to an action (bind <key> shows it)", a =>
        {
            if (a.Length == 0) { c.Print($"usage: bind <key> <action>   actions: {ActionNames()}"); return; }
            var key = KeyBindings.Normalize(a[0]);
            if (a.Length == 1)
            {
                var cur = kb.ActionFor(key);
                c.Print(cur == null ? $"\"{key}\" is not bound" : $"\"{key}\" = {KeyBindings.Name(cur.Value)}");
                return;
            }
            if (!KeyBindings.TryParseAction(a[1], out var action)) { c.Print($"unknown action \"{a[1]}\"; actions: {ActionNames()}"); return; }
            if (KeyBindings.IsReserved(key)) { c.Print($"\"{key}\" is reserved and cannot be rebound"); return; }
            if (!kb.Bind(action, key, out var lost)) { c.Print($"\"{key}\" is not a key I know"); return; }
            c.Print($"{key} -> {KeyBindings.Name(action)}" + (lost != null ? $"  ({KeyBindings.Name(lost.Value)} is now unbound)" : ""));
        });
        c.AddCommand("unbind", "unbind <key|action>", "Remove a binding by key or action name", a =>
        {
            if (a.Length < 1) { c.Print("usage: unbind <key|action>"); return; }
            if (kb.UnbindKey(a[0])) c.Print($"\"{KeyBindings.Normalize(a[0])}\" unbound");
            else if (KeyBindings.TryParseAction(a[0], out var act) && kb.Get(act) != null) { kb.Unbind(act); c.Print($"{KeyBindings.Name(act)} unbound"); }
            else c.Print($"nothing bound to \"{a[0]}\"");
        });
        c.AddCommand("unbindall", "unbindall", "Remove every binding (use bind_reset to restore defaults)", a => { kb.UnbindAll(); c.Print("all bindings removed"); });
        c.AddCommand("bind_reset", "bind_reset", "Restore the default key bindings", a => { kb.ResetDefaults(); c.Print("key bindings reset to defaults"); });
        c.AddCommand("bindlist", "bindlist", "List key bindings", a =>
        {
            foreach (var act in KeyBindings.All) c.Print($"{KeyBindings.Name(act),-11} {KeyBindings.Display(kb.Get(act))}");
        });

        c.AddCommand("map", "map [arena|random [seed]]", "Show the current map, load the classic arena, or generate a random one (same seed = same map)", a =>
        {
            if (a.Length == 0) { c.Print($"current map: {g.MapName}" + (g.MapSeed > 0 ? $" (seed {g.MapSeed})" : "")); return; }
            if (a[0].Equals("arena", StringComparison.OrdinalIgnoreCase)) { g.LoadClassicArena(); return; }
            if (a[0].Equals("random", StringComparison.OrdinalIgnoreCase))
            {
                int seed = 0;
                if (a.Length > 1 && !int.TryParse(a[1], out seed)) { c.Print($"\"{a[1]}\" is not a seed number"); return; }
                g.LoadRandomMap(seed);
                return;
            }
            c.Print("usage: map [arena|random [seed]]");
        });
        c.AddCvar("capturelimit", g.CaptureLimit, "Captures needed to win a capture-the-flag match", v => g.CaptureLimit = Math.Max(1, (int)v));
        c.AddCommand("gamemode", "gamemode [dm|ctf|tf]", "Show or switch the game mode (reloads the current map)", a =>
        {
            if (a.Length == 0) { c.Print($"game mode: {(g.IsTf ? "team fortress" : g.IsCtf ? "capture the flag" : "deathmatch")}"); return; }
            if (a[0].Equals("tf", StringComparison.OrdinalIgnoreCase)) { g.SetMode(GameMode.TeamFortress); c.Print("team fortress"); return; }
            if (a[0].Equals("ctf", StringComparison.OrdinalIgnoreCase)) { g.SetMode(GameMode.Ctf); c.Print("capture the flag"); }
            else if (a[0].Equals("dm", StringComparison.OrdinalIgnoreCase)) { g.SetMode(GameMode.Deathmatch); c.Print("deathmatch"); }
            else c.Print("usage: gamemode [dm|ctf|tf]");
        });
        c.AddCommand("class", "class [scout|soldier|demoman|medic|heavy|sniper|1-6]", "Team Fortress: show classes or pick your next one (applies when you respawn)", a =>
        {
            if (a.Length == 0)
            {
                c.Print($"you are: {(p.Class == PlayerClass.None ? "no class" : ClassDef.Get(p.Class).Name)}  next: {ClassDef.Get(p.NextClass).Name}");
                for (int i = 0; i < ClassDef.All.Length; i++) { var d = ClassDef.All[i]; c.Print($"  {i + 1} {d.Name,-8} {d.Health,3} hp  speed {d.Speed:0.00}  {d.Blurb}"); }
                return;
            }
            if (!ClassDef.TryParse(a[0], out var cls)) { c.Print($"unknown class \"{a[0]}\""); return; }
            g.ChooseClass(cls);
            if (!g.IsTf) c.Print("(classes only apply in team fortress: gamemode tf)");
        });
        c.AddCommand("flags", "flags", "Show the score and where each flag is", a =>
        {
            if (!g.IsCtf) { c.Print("not in capture the flag (gamemode ctf|tf)"); return; }
            c.Print($"RED {g.TeamScore[1]} - {g.TeamScore[2]} BLUE  (first to {g.CaptureLimit})");
            foreach (var f in g.Flags)
                c.Print($"{f.Team.Label()} flag: {f.State}" + (f.Carrier != null ? $" by {f.Carrier.Name}" : ""));
        });
        c.AddCommand("pickups", "pickups", "List pickups and seconds until each returns", a =>
        {
            foreach (var k in g.Pickups)
                c.Print($"{k.Name,-18} at {GameConsole.Fmt(k.Position.X)} {GameConsole.Fmt(k.Position.Y)} {GameConsole.Fmt(k.Position.Z)}  " +
                        (k.Active ? "ready" : $"back in {Math.Max(0f, k.RespawnAt - g.Time):0.0}s"));
        });
        c.AddCommand("stats", "stats", "Print player status", a =>
            c.Print($"health {p.Health}  shells {p.Shells}  nails {p.Nails}  rockets {p.Rockets}  cells {p.Cells}  slugs {p.Slugs}  frags {p.Frags}  weapon {WeaponDef.Get(p.Current).Name}"));
    }
}
