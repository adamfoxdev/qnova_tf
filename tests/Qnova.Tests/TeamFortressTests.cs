using System.Numerics;
using Qnova.Core;

namespace Qnova.Tests;

public class TeamFortressTests
{
    static GameWorld Tf(PlayerClass mine = PlayerClass.Soldier)
    {
        var g = Arena.Build(bots: 0);
        g.LoadClassicArena();
        g.BotAi = false;
        g.Player.NextClass = mine;
        g.SetMode(GameMode.TeamFortress);
        return g;
    }

    static void Step(GameWorld g, int n = 1) { for (int i = 0; i < n; i++) g.Tick(default, false); }

    [Fact]
    public void Team_fortress_is_ctf_with_classes()
    {
        var g = Tf();
        Assert.True(g.IsTf); Assert.True(g.IsCtf);
        Assert.Equal(2, g.Flags.Count);
        Assert.All(g.Combatants, c => Assert.NotEqual(PlayerClass.None, c.Class));
    }

    [Theory]
    [InlineData(PlayerClass.Scout, 75, WeaponId.SuperShotgun)]
    [InlineData(PlayerClass.Soldier, 125, WeaponId.RocketLauncher)]
    [InlineData(PlayerClass.Demoman, 100, WeaponId.GrenadeLauncher)]
    [InlineData(PlayerClass.Medic, 90, WeaponId.Nailgun)]
    [InlineData(PlayerClass.Heavy, 200, WeaponId.SuperNailgun)]
    [InlineData(PlayerClass.Sniper, 80, WeaponId.Railgun)]
    public void Each_class_spawns_with_its_health_and_loadout(PlayerClass c, int hp, WeaponId primary)
    {
        var g = Tf(c);
        var p = g.Player;
        Assert.Equal(c, p.Class);
        Assert.Equal(hp, p.Health); Assert.Equal(hp, p.MaxHealth);
        Assert.Equal(primary, p.Current);
        Assert.Contains(primary, p.Owned);
        Assert.DoesNotContain(WeaponId.LightningGun, p.Owned);
    }

    [Fact]
    public void Class_change_applies_on_respawn_not_immediately()
    {
        var g = Tf(PlayerClass.Scout);
        g.ChooseClass(PlayerClass.Heavy);
        Assert.Equal(PlayerClass.Scout, g.Player.Class);
        g.Die(g.Player, g.Player);
        Step(g, (int)(4 * GameWorld.TickRate));
        Assert.Equal(PlayerClass.Heavy, g.Player.Class);
        Assert.Equal(200, g.Player.Health);
    }

    [Fact]
    public void Scout_runs_faster_than_heavy()
    {
        float Speed(PlayerClass c)
        {
            var w = new World();
            w.Add(new(-20000, -64, -20000), new(20000, 0, 20000));
            var g = new GameWorld(w, new Vector3(0, 28, 0), seed: 11);
            g.Player.NextClass = c; g.BotAi = false;
            g.SetMode(GameMode.TeamFortress);
            g.Player.Move.Position = new Vector3(0, 28, 0);
            var cmd = new UserCmd { Forward = 1 };
            for (int i = 0; i < 200; i++) g.Tick(cmd, false);
            var v = g.Player.Move.Velocity;
            return MathF.Sqrt(v.X * v.X + v.Z * v.Z);
        }
        float scout = Speed(PlayerClass.Scout), heavy = Speed(PlayerClass.Heavy);
        Assert.True(scout > heavy * 1.5f, $"scout {scout} heavy {heavy}");
        Assert.InRange(heavy, 0.75f * 320f - 5, 0.75f * 320f + 5);
    }

    [Fact]
    public void Medic_heals_wounded_teammates_but_not_enemies()
    {
        var g = Tf(PlayerClass.Medic);
        var med = g.Player;
        var ally = g.Bots.First(b => b.Body.Team == Team.Red).Body;
        var foe = g.Bots.First(b => b.Body.Team == Team.Blue).Body;
        med.Move.Position = new Vector3(0, 28, 0);
        ally.Move.Position = new Vector3(100, 28, 0); foe.Move.Position = new Vector3(-100, 28, 0);
        ally.Health = 10; foe.Health = 10;
        Step(g, (int)(2 * GameWorld.TickRate));
        Assert.True(ally.Health >= 20, $"ally {ally.Health}");
        Assert.Equal(10, foe.Health);
    }

    [Fact]
    public void Medic_heal_is_out_of_range_ignored_and_capped_at_max()
    {
        var g = Tf(PlayerClass.Medic);
        var ally = g.Bots.First(b => b.Body.Team == Team.Red).Body;
        g.Player.Move.Position = new Vector3(0, 28, 0);
        ally.Move.Position = new Vector3(1500, 28, 0); ally.Health = 10;
        Step(g, 144);
        Assert.Equal(10, ally.Health);
        ally.Move.Position = new Vector3(60, 28, 0);
        Step(g, 72 * 30);
        Assert.Equal(ally.MaxHealth, ally.Health);
    }

    [Fact]
    public void Leaving_team_fortress_restores_normal_health_speed_and_bot_arsenal()
    {
        var g = Tf(PlayerClass.Heavy);
        g.SetMode(GameMode.Deathmatch);
        Assert.Equal(100, g.Player.MaxHealth);
        Assert.Equal(1f, g.Player.Move.SpeedScale);
        Assert.Equal(PlayerClass.None, g.Player.Class);
        Assert.All(g.Bots, b => Assert.Equal(Enum.GetValues<WeaponId>().Length, b.Body.Owned.Count));
    }

    [Fact]
    public void Bots_fight_with_weapons_they_own()
    {
        var g = Tf(PlayerClass.Soldier);
        g.BotAi = true;
        Step(g, 72 * 20);
        Assert.All(g.Bots, b => Assert.Contains(b.Body.Current, b.Body.Owned));
    }

    [Fact]
    public void Class_command_and_gamemode_tf_work_from_the_console()
    {
        var g = Arena.Build(bots: 0);
        g.LoadClassicArena();
        g.Console.Execute("gamemode tf", echo: false);
        Assert.True(g.IsTf);
        g.Console.Execute("class sniper", echo: false);
        Assert.Equal(PlayerClass.Sniper, g.Player.NextClass);
        g.Console.Execute("class 5", echo: false);
        Assert.Equal(PlayerClass.Heavy, g.Player.NextClass);
    }

    [Fact]
    public void Menu_game_mode_cycles_through_team_fortress()
    {
        var g = Arena.Build(bots: 0);
        g.LoadClassicArena();
        var m = MenuModel.Create(g, () => false, () => { }, () => { });
        int idx = m.Current.Items.FindIndex(i => i.Label() == "GAME MODE");
        m.SetSelected(idx);
        m.Adjust(1); Assert.Equal(GameMode.Ctf, g.Mode);
        m.Adjust(1); Assert.Equal(GameMode.TeamFortress, g.Mode);
        m.Adjust(1); Assert.Equal(GameMode.Deathmatch, g.Mode);
    }
}
