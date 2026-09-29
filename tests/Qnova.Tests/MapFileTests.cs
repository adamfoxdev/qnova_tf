using System.Numerics;
using Qnova.Core;

namespace Qnova.Tests;

public class MapFileTests
{
    static string Tmp(string name) => Path.Combine(Path.GetTempPath(), $"qnova-{Guid.NewGuid():N}-{name}");

    [Fact]
    public void Arena_round_trips_through_json()
    {
        var a = Arena.Data();
        var b = MapJson.Parse(MapJson.ToJson(a));
        Assert.Equal(a.Name, b.Name);
        Assert.Equal(a.Half, b.Half); Assert.Equal(a.Height, b.Height);
        Assert.Equal(a.Solids, b.Solids);
        Assert.Equal(a.Decor.Count, b.Decor.Count);
        Assert.Equal(a.Decor.Select(d => d.Surface), b.Decor.Select(d => d.Surface));
        Assert.Equal(a.Lights.Count, b.Lights.Count);
        Assert.Equal(a.Lights.Select(l => l.Position), b.Lights.Select(l => l.Position));
        Assert.Equal(a.Pickups.Select(k => (k.Kind, k.Weapon, k.Amount, k.Position)), b.Pickups.Select(k => (k.Kind, k.Weapon, k.Amount, k.Position)));
        Assert.Equal(a.JumpPads.Select(p => (p.Trigger, p.Target)), b.JumpPads.Select(p => (p.Trigger, p.Target)));
        Assert.Equal(a.Spawns, b.Spawns);
        Assert.Equal(a.RedFlag, b.RedFlag); Assert.Equal(a.BlueFlag, b.BlueFlag);
        Assert.Equal(a.PlayerSpawn, b.PlayerSpawn);
    }

    [Fact]
    public void Random_map_round_trips_and_still_validates()
    {
        var a = MapGenerator.Generate(42);
        var b = MapJson.Parse(MapJson.ToJson(a));
        Assert.Equal(a.Solids.Count, b.Solids.Count);
        Assert.Equal(a.Pickups.Count, b.Pickups.Count);
        Assert.True(MapCheck.Check(b).Ok, string.Join("; ", MapCheck.Check(b).Errors));
    }

    [Fact]
    public void Classic_arena_passes_the_map_check()
    {
        var r = MapCheck.Check(Arena.Data());
        Assert.True(r.Ok, string.Join("; ", r.Errors));
    }

    [Fact]
    public void Defaults_fill_in_and_boxes_are_normalised()
    {
        var m = MapJson.Parse("""
        { "format": 1, "half": 1024,
          "solids": [[1024,0,1024,-1024,-64,-1024]],
          "spawns": [[0,28,0]],
          "pickups": [{"kind":"health","pos":[100,16,0]}, {"kind":"weapon","weapon":"railgun","pos":[200,16,0]}] }
        """);
        Assert.Equal(new Vector3(-1024, -64, -1024), m.Solids[0].Min);
        Assert.Equal(new Vector3(0, 28, 0), m.PlayerSpawn);
        Assert.Equal(25, m.Pickups[0].Amount);
        Assert.Equal(WeaponId.Railgun, m.Pickups[1].Weapon);
        Assert.Equal(768f, m.Height);
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("[]", "top level")]
    [InlineData("{\"format\":2}", "unsupported map format")]
    [InlineData("{\"format\":1,\"solids\":[],\"spawns\":[[0,28,0]]}", "no solids")]
    [InlineData("{\"format\":1,\"solids\":[[0,0,0,1,1,1]],\"spawns\":[]}", "no spawn")]
    [InlineData("{\"format\":1,\"solids\":[[0,0,0,1,1]],\"spawns\":[[0,28,0]]}", "solids[0]: expected 6 numbers")]
    [InlineData("{\"format\":1,\"solids\":[[0,0,0,0,1,1]],\"spawns\":[[0,28,0]]}", "no volume")]
    [InlineData("{\"format\":1,\"solids\":[[0,0,0,1,1,1]],\"spawns\":[[0,28,0]],\"pickups\":[{\"kind\":\"pizza\",\"pos\":[0,0,0]}]}", "unknown kind")]
    [InlineData("{\"format\":1,\"solids\":[[0,0,0,1,1,1]],\"spawns\":[[0,28,0]],\"decor\":[{\"box\":[0,0,0,1,1,1],\"surface\":\"lava\"}]}", "unknown surface")]
    [InlineData("{\"format\":1,\"solids\":[[0,0,0,1,1,1]],\"spawns\":[[0,28,0]],\"redFlag\":[0,0,0]}", "both be set")]
    public void Bad_files_are_rejected_with_a_helpful_message(string json, string expected)
    {
        var e = Assert.Throws<MapFormatException>(() => MapJson.Parse(json));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void Map_check_finds_stuck_spawns_floating_items_and_bad_pads()
    {
        var m = new MapData { Half = 1024, Height = 512, PlayerSpawn = new(0, 28, 0) };
        m.Solids.Add(new Aabb(new(-1024, -64, -1024), new(1024, 0, 1024)));
        m.Solids.Add(new Aabb(new(300, 0, -50), new(400, 200, 50)));
        m.Spawns.Add(new(0, 28, 0));
        m.Spawns.Add(new(350, 28, 0));                                   // inside the block
        m.Spawns.Add(new(-200, 200, 0));                                 // in the air
        m.Pickups.Add(new Pickup { Kind = PickupKind.Health, Amount = 25, Position = new(-300, 150, 0) });   // floats
        m.JumpPads.Add(new JumpPad { Trigger = new Aabb(new(-50, 0, 300), new(50, 40, 400)), Target = new(350, 100, 0) });   // arcs into the block
        var r = MapCheck.Check(m);
        Assert.Contains(r.Errors, e => e.Contains("inside geometry"));
        Assert.Contains(r.Errors, e => e.Contains("not on the ground"));
        Assert.Contains(r.Errors, e => e.Contains("floats"));
        Assert.Contains(r.Errors, e => e.Contains("pad at"));
        Assert.Contains(r.Warnings, w => w.Contains("spawn point"));
        Assert.False(r.Ok);
    }

    [Fact]
    public void Console_can_save_and_load_a_map_file_and_play_ctf_on_it()
    {
        var g = Arena.Build(bots: 0);
        string path = Tmp("arena.json");
        try
        {
            g.Console.Execute($"map save {path}", echo: false);
            Assert.True(File.Exists(path));
            g.Console.Execute("map random 7", echo: false);
            Assert.NotEqual("Classic Arena", g.MapName);
            g.Console.Execute($"map file {path}", echo: false);
            Assert.Equal("Classic Arena", g.MapName);
            Assert.Contains(g.Console.Lines, l => l.Contains("map check: OK"));
            g.SetMode(GameMode.Ctf);
            Assert.Equal(new Vector3(0, 40, -1850), g.FlagOf(Team.Red)!.Home);   // bases survive the file
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Console_reports_a_missing_or_broken_file_without_throwing()
    {
        var g = Arena.Build(bots: 0);
        g.Console.Execute($"map file {Tmp("nope.json")}", echo: false);
        Assert.Contains(g.Console.Lines, l => l.Contains("can't load map"));
        string path = Tmp("bad.json");
        File.WriteAllText(path, "{ oops");
        try { g.Console.Execute($"map file {path}", echo: false); } finally { File.Delete(path); }
        Assert.Contains(g.Console.Lines, l => l.Contains("not valid JSON"));
        Assert.Equal("Classic Arena", g.MapName);                       // the old map stays loaded
        g.Console.Execute("mapcheck", echo: false);
        Assert.Contains(g.Console.Lines, l => l.Contains("map check"));
    }
}

public class ShippedMapTests
{
    static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "maps", "classic_arena.json"))) d = d.Parent;
        return d?.FullName ?? throw new FileNotFoundException("maps/classic_arena.json not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Shipped_classic_arena_file_matches_the_built_in_arena()
    {
        var file = MapJson.Load(Path.Combine(Repo(), "maps", "classic_arena.json"));
        var built = Arena.Data();
        Assert.Equal(built.Solids, file.Solids);
        Assert.Equal(built.Pickups.Count, file.Pickups.Count);
        Assert.Equal(built.JumpPads.Count, file.JumpPads.Count);
        Assert.Equal(built.RedFlag, file.RedFlag);
    }

    [Fact]
    public void Every_shipped_map_loads_and_passes_the_check()
    {
        foreach (var f in Directory.GetFiles(Path.Combine(Repo(), "maps"), "*.json"))
        {
            var r = MapCheck.Check(MapJson.Load(f));
            Assert.True(r.Ok, $"{Path.GetFileName(f)}: {string.Join("; ", r.Errors)}");
        }
    }

    [Fact]
    public void Editor_template_arena_is_the_shipped_arena()
    {
        // the editor embeds the classic arena as its template; keep it in step with the shipped file
        string html = File.ReadAllText(Path.Combine(Repo(), "editor", "index.html"));
        var arena = MapJson.Load(Path.Combine(Repo(), "maps", "classic_arena.json"));
        Assert.Contains("const ARENA = {", html);
        Assert.Contains(((int)arena.Solids[0].Max.X).ToString(), html);
        Assert.Contains("\"redFlag\":[0,40,-1850]", html);
    }
}
