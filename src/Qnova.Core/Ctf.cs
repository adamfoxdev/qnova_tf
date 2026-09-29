using System.Numerics;

namespace Qnova.Core;

public enum GameMode { Deathmatch, Ctf, TeamFortress }
public enum Team { None, Red, Blue }
public enum FlagState { Home, Carried, Dropped }

public static class TeamExtensions
{
    public static Team Other(this Team t) => t == Team.Red ? Team.Blue : t == Team.Blue ? Team.Red : Team.None;
    public static string Label(this Team t) => t == Team.Red ? "RED" : t == Team.Blue ? "BLUE" : "";
}

/// <summary>A team's banner. It sits at <see cref="Home"/>, is carried by an enemy, or lies where its carrier died.</summary>
public sealed class Flag
{
    public static readonly Vector3 Half = new(24, 40, 24);
    public Team Team;
    public Vector3 Home;
    public Vector3 Pos;
    public FlagState State;
    public Player? Carrier;
    public float ReturnAt;          // when dropped: time it returns by itself
    public Aabb Bounds => Aabb.FromCenter(Pos, Half);
}

public sealed partial class GameWorld
{
    public const float FlagReturnTime = 30f;
    public const int CaptureFrags = 5;
    const float MatchOverTime = 6f;

    public GameMode Mode { get; private set; } = GameMode.Deathmatch;
    public readonly List<Flag> Flags = new();
    public readonly int[] TeamScore = new int[3];          // indexed by (int)Team
    public int CaptureLimit = 5;
    public Team Winner { get; private set; }
    public float MatchOverUntil { get; private set; }
    readonly Dictionary<Team, List<Vector3>> _teamSpawns = new();
    MapData? _current;

    public bool IsCtf => Mode == GameMode.Ctf;

    /// <summary>The loaded map as data (for saving and checking). A game built directly by <see cref="Arena.Build"/> never went through LoadMap.</summary>
    public MapData SnapshotMap() => _current ?? (MapName == "Classic Arena" ? Arena.Data() : MapData.From(this, MapName, MapSeed, MapHalf, CeilingY));

    /// <summary>Load a map file made with the editor, then print anything <see cref="MapCheck"/> finds wrong with it.</summary>
    public MapData LoadMapFile(string path)
    {
        var m = MapJson.Load(path);
        LoadMap(m);
        Console.Print($"{m.Summary}");
        PrintCheck(MapCheck.Check(m));
        return m;
    }

    public void PrintCheck(MapCheck.Result r)
    {
        foreach (var e in r.Errors.Take(10)) Console.Print($"ERROR: {e}");
        if (r.Errors.Count > 10) Console.Print($"...and {r.Errors.Count - 10} more errors");
        foreach (var w in r.Warnings.Take(5)) Console.Print($"warning: {w}");
        Console.Print(!r.Ok ? $"map check: {r.Errors.Count} error(s), {r.Warnings.Count} warning(s)" : r.Warnings.Count == 0 ? "map check: OK" : $"map check: playable, {r.Warnings.Count} warning(s)");
    }
    public Flag? FlagOf(Team t) => Flags.FirstOrDefault(f => f.Team == t);
    public bool Friendly(Player a, Player b) => IsCtf && a.Team != Team.None && a.Team == b.Team;
    public Flag? Carrying(Player p) => Flags.FirstOrDefault(f => f.Carrier == p);

    /// <summary>Switch mode and reload the current map so flags and teams are set up (or torn down) cleanly.</summary>
    public void SetMode(GameMode mode)
    {
        Mode = mode;
        if (mode != GameMode.Deathmatch) while (Bots.Count < 3) AddBot();      // 2v2 with you and three bots
        if (_current == null && MapName == "Classic Arena" && Pickups.Count > 0) _current = Arena.Data();   // built directly, never loaded: adopt its authored bases
        if (_current != null) LoadMap(_current); else { SetupMode(); RespawnAll(); }
    }

    /// <summary>Teams, flags and team spawns for the current mode. Called at the end of <see cref="LoadMap"/>.</summary>
    void SetupMode()
    {
        Flags.Clear(); _teamSpawns.Clear();
        Array.Clear(TeamScore); Winner = Team.None; MatchOverUntil = 0;
        AssignTeams();
        if (!IsCtf) return;

        var (red, blue) = FlagBases();
        Flags.Add(new Flag { Team = Team.Red, Home = red, Pos = red });
        Flags.Add(new Flag { Team = Team.Blue, Home = blue, Pos = blue });
        var pool = SpawnPoints.Count > 0 ? SpawnPoints : new List<Vector3> { SpawnPoint };
        foreach (var t in new[] { Team.Red, Team.Blue })
        {
            var home = FlagOf(t)!.Home;
            _teamSpawns[t] = pool.OrderBy(s => Vector3.Distance(s, home)).Take(3).ToList();
        }
    }

    (Vector3 Red, Vector3 Blue) FlagBases()
    {
        if (_current?.RedFlag is { } r && _current.BlueFlag is { } b) return (r, b);
        // no authored bases: use the two spawn points that are farthest apart, resting the flag on the floor
        var pool = SpawnPoints.Count >= 2 ? SpawnPoints : new List<Vector3> { SpawnPoint, SpawnPoint + new Vector3(400, 0, 0) };
        Vector3 a = pool[0], c = pool[1]; float best = -1;
        for (int i = 0; i < pool.Count; i++)
            for (int j = i + 1; j < pool.Count; j++)
            {
                float d = Vector3.Distance(pool[i], pool[j]);
                if (d > best) { best = d; a = pool[i]; c = pool[j]; }
            }
        var lift = new Vector3(0, Flag.Half.Y - MoveVars.Half.Y, 0);
        return (a + lift, c + lift);
    }

    /// <summary>You are Red; bots alternate Blue, Red, Blue... In deathmatch nobody has a team.</summary>
    public void AssignTeams()
    {
        if (!IsCtf) { foreach (var c in Combatants) c.Team = Team.None; return; }
        Player.Team = Team.Red;
        int i = 0;
        foreach (var b in Bots) b.Body.Team = (i++ % 2 == 0) ? Team.Blue : Team.Red;
        int k = 0;                                   // bots take a spread of classes (you choose your own)
        foreach (var b in Bots) b.Body.NextClass = ClassDef.All[(k++ * 5 + 1) % ClassDef.All.Length].Id;
        int reds = 0, blues = 0;
        foreach (var b in Bots) b.Role = b.Body.Team == Team.Red ? (reds++ == 0 ? BotRole.Attack : BotRole.Defend) : (blues++ == 0 ? BotRole.Attack : BotRole.Defend);
    }

    void RespawnAll() { foreach (var c in Combatants) RespawnPlayer(c); }

    void ResetFlag(Flag f) { f.State = FlagState.Home; f.Carrier = null; f.Pos = f.Home; }

    void UpdateFlags()
    {
        if (!IsCtf || Flags.Count < 2) return;
        if (MatchOverUntil > 0)
        {
            if (Time < MatchOverUntil) return;
            NewMatch();
        }
        foreach (var f in Flags)
        {
            if (f.State == FlagState.Carried)
            {
                if (f.Carrier is not { Alive: true } c || !Combatants.Contains(c)) { ResetFlag(f); continue; }
                f.Pos = c.Move.Position + new Vector3(0, Flag.Half.Y - MoveVars.Half.Y, 0);
                continue;
            }
            if (f.State == FlagState.Dropped && Time >= f.ReturnAt)
            {
                ResetFlag(f);
                Announce(EventKind.FlagReturned, f, $"The {f.Team.Label()} flag returned");
                continue;
            }
            var box = f.Bounds;
            foreach (var c in Combatants)
            {
                if (!c.Alive || c.Team == Team.None || !Box(c).Overlaps(box)) continue;
                if (c.Team != f.Team)
                {
                    if (Carrying(c) != null) continue;                         // one flag at a time
                    f.State = FlagState.Carried; f.Carrier = c;
                    Announce(EventKind.FlagTaken, f, $"{c.Name} took the {f.Team.Label()} flag", c == Player);
                    break;
                }
                if (f.State == FlagState.Dropped)                              // touch your own dropped flag to send it home
                {
                    ResetFlag(f); c.Frags += 1;
                    Announce(EventKind.FlagReturned, f, $"{c.Name} returned the {f.Team.Label()} flag", c == Player);
                    break;
                }
                if (Carrying(c) is { } enemyFlag)                              // own flag is home: capture
                {
                    ResetFlag(enemyFlag);
                    TeamScore[(int)c.Team]++; c.Frags += CaptureFrags;
                    Announce(EventKind.FlagCaptured, enemyFlag, $"{c.Name} captured the {enemyFlag.Team.Label()} flag!  RED {TeamScore[1]} - {TeamScore[2]} BLUE", c == Player);
                    if (TeamScore[(int)c.Team] >= CaptureLimit) EndMatch(c.Team);
                    break;
                }
            }
        }
    }

    void Announce(EventKind kind, Flag f, string text, bool human = false)
    {
        Console.Print(text);
        Events.Add(new GameEvent(kind, f.Pos, human ? Vector3.UnitX : default, (int)f.Team));
    }

    void EndMatch(Team winner)
    {
        Winner = winner; MatchOverUntil = Time + MatchOverTime;
        Console.Print($"{winner.Label()} TEAM WINS  RED {TeamScore[1]} - {TeamScore[2]} BLUE");
    }

    /// <summary>Scores, flags and everyone's positions reset after a decided match.</summary>
    void NewMatch()
    {
        Array.Clear(TeamScore); Winner = Team.None; MatchOverUntil = 0;
        foreach (var f in Flags) ResetFlag(f);
        foreach (var c in Combatants) { c.Frags = 0; c.Deaths = 0; RespawnPlayer(c); }
        Console.Print("New match");
    }

    /// <summary>The carrier's flag falls where they died.</summary>
    void DropFlag(Player p)
    {
        if (Carrying(p) is not { } f) return;
        f.State = FlagState.Dropped; f.Carrier = null; f.ReturnAt = Time + FlagReturnTime;
        f.Pos = p.Move.Position + new Vector3(0, Flag.Half.Y - MoveVars.Half.Y, 0);
        Announce(EventKind.FlagDropped, f, $"{p.Name} dropped the {f.Team.Label()} flag");
    }
}

public enum BotRole { Attack, Defend }
