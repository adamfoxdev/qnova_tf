using System.Globalization;

namespace Qnova.Core;

public sealed class MenuItem
{
    public required Func<string> Label;
    public Func<string>? Value;          // shown on the right (options)
    public Action? OnSelect;             // Enter / click
    public Action<int>? OnAdjust;        // Left/Right: -1 / +1
    public bool Adjustable => OnAdjust != null;
}

public sealed class MenuScreen
{
    public required string Title;
    public readonly List<MenuItem> Items = new();
}

/// <summary>Keyboard-driven menu state machine for the splash screen (main menu + options).
/// It holds no drawing code so it can be tested; option values are read and written through console cvars.</summary>
public sealed class MenuModel
{
    readonly Stack<(MenuScreen Screen, int Selected)> _stack = new();
    public MenuScreen Current { get; private set; }
    public int Selected { get; private set; }
    public bool AtRoot => _stack.Count == 0;

    MenuModel(MenuScreen root, KeyBindings bindings) { Current = root; _bindings = bindings; }

    public MenuItem SelectedItem => Current.Items[Selected];

    public void Push(MenuScreen s) { _stack.Push((Current, Selected)); Current = s; Selected = 0; Notice = null; }

    /// <summary>Move the highlight; wraps around. Returns true if it moved.</summary>
    public bool Move(int dir)
    {
        int n = Current.Items.Count;
        if (n < 2 || Capturing != null) return false;
        Selected = ((Selected + dir) % n + n) % n;
        Notice = null;
        return true;
    }

    public bool SetSelected(int i)
    {
        if (Capturing != null || i < 0 || i >= Current.Items.Count || i == Selected) return false;
        Selected = i; Notice = null; return true;
    }

    /// <summary>Left/Right on an adjustable item. Returns true if the item handles it.</summary>
    public bool Adjust(int dir)
    {
        var item = SelectedItem;
        if (Capturing != null || item.OnAdjust == null) return false;
        item.OnAdjust(dir);
        return true;
    }

    /// <summary>Enter / click. Adjustable items without a select action step forward (toggles).</summary>
    public void Select()
    {
        if (Capturing != null) return;
        var item = SelectedItem;
        if (item.OnSelect != null) item.OnSelect();
        else item.OnAdjust?.Invoke(1);
    }

    /// <summary>Escape: cancel a pending key capture, else pop back one screen.
    /// Returns false at the root (caller decides: resume or ignore).</summary>
    public bool Back()
    {
        if (Capturing != null) { CancelCapture(); return true; }
        if (_stack.Count == 0) return false;
        (Current, Selected) = _stack.Pop();
        return true;
    }

    // ---- key capture (rebinding) ----

    readonly KeyBindings _bindings;

    /// <summary>The action waiting for a key press, or null. While set, the frontend feeds the next key to <see cref="Capture"/>.</summary>
    public InputAction? Capturing { get; private set; }

    /// <summary>Short feedback for the bindings screen ("W taken from MOVE FORWARD"); cleared when navigating.</summary>
    public string? Notice { get; private set; }

    public void BeginCapture(InputAction action) { Capturing = action; Notice = null; }
    public void CancelCapture() => Capturing = null;

    /// <summary>Bind the captured code to the waiting action. Returns false if the code is unusable (reserved or invalid);
    /// capture then stays active so the player can try another key.</summary>
    public bool Capture(string code)
    {
        if (Capturing is not { } action) return false;
        if (!_bindings.Bind(action, code, out var lost))
        {
            Notice = $"CAN'T USE {KeyBindings.Display(code)}";
            return false;
        }
        Capturing = null;
        Notice = lost is { } l ? $"{KeyBindings.Display(code)} TAKEN FROM {KeyBindings.Label(l)}" : null;
        return true;
    }

    /// <summary>Backspace / Delete while capturing: leave the action unbound.</summary>
    public void UnbindCaptured()
    {
        if (Capturing is { } a) { _bindings.Unbind(a); Capturing = null; Notice = null; }
    }

    public const int MaxBots = 4;

    /// <summary>Build the main menu and options screen for a game.
    /// <paramref name="hasStarted"/> switches "START GAME" to "RESUME GAME" once the player has entered the arena.</summary>
    public static MenuModel Create(GameWorld g, Func<bool> hasStarted, Action start, Action quit)
    {
        var options = new MenuScreen { Title = "OPTIONS" };
        var root = new MenuScreen { Title = "QNOVA" };
        var keys = new MenuScreen { Title = "KEY BINDINGS" };
        var m = new MenuModel(root, g.Bindings);

        options.Items.Add(Slider(g, "MOUSE SENSITIVITY", "sensitivity", 0.02f, 0.5f, 0.01f, "0.00"));
        options.Items.Add(Slider(g, "FIELD OF VIEW", "fov", 60f, 120f, 5f, "0"));
        options.Items.Add(Slider(g, "ZOOM FOV", "zoom_fov", 10f, 60f, 5f, "0"));
        options.Items.Add(Slider(g, "VOLUME", "volume", 0f, 1f, 0.1f, "0%"));
        options.Items.Add(Slider(g, "BOT SKILL", "bot_skill", 1f, 5f, 1f, "0"));
        options.Items.Add(new MenuItem
        {
            Label = () => "BOTS",
            Value = () => g.Bots.Count.ToString(CultureInfo.InvariantCulture),
            OnAdjust = dir =>
            {
                if (dir > 0 && g.Bots.Count < MaxBots) g.AddBot();
                else if (dir < 0 && g.Bots.Count > 0) g.Bots.RemoveAt(g.Bots.Count - 1);
            },
        });
        options.Items.Add(new MenuItem
        {
            Label = () => "AUTO BUNNY-HOP",
            Value = () => g.Console.Get("sv_autohop") != 0 ? "ON" : "OFF",
            OnAdjust = _ => g.Console.Execute($"sv_autohop {(g.Console.Get("sv_autohop") != 0 ? 0 : 1)}", echo: false),
        });
        options.Items.Add(new MenuItem
        {
            Label = () => "PLAIN BLOCKS",
            Value = () => g.Console.TryGet("r_plain", out var v) ? (v != 0 ? "ON" : "OFF") : "n/a",
            OnAdjust = _ =>
            {
                if (g.Console.TryGet("r_plain", out var v)) g.Console.Execute($"r_plain {(v != 0 ? 0 : 1)}", echo: false);
            },
        });
        options.Items.Add(new MenuItem
        {
            Label = () => "DAMAGE NUMBERS",
            Value = () => g.Console.TryGet("cl_damagenumbers", out var v) ? (v != 0 ? "ON" : "OFF") : "n/a",
            OnAdjust = _ =>
            {
                if (g.Console.TryGet("cl_damagenumbers", out var v)) g.Console.Execute($"cl_damagenumbers {(v != 0 ? 0 : 1)}", echo: false);
            },
        });
        options.Items.Add(new MenuItem
        {
            Label = () => "CLASS",
            Value = () => ClassDef.Get(g.Player.NextClass).Name.ToUpperInvariant(),
            OnAdjust = dir =>
            {
                int n = ClassDef.All.Length;
                var next = ClassDef.All[((int)g.Player.NextClass - 1 + dir + n) % n].Id;
                g.Player.NextClass = next;
                if (g.IsTf && !hasStarted()) g.RespawnPlayer(g.Player);      // before the game starts the change is immediate
            },
        });
        options.Items.Add(new MenuItem { Label = () => "KEY BINDINGS", OnSelect = () => m.Push(keys) });
        options.Items.Add(new MenuItem { Label = () => "BACK", OnSelect = () => m.Back() });

        // one row per action: Enter, then press the new key (Esc cancels, Backspace unbinds)
        foreach (var action in KeyBindings.All)
        {
            var a = action;
            keys.Items.Add(new MenuItem
            {
                Label = () => KeyBindings.Label(a),
                Value = () => m.Capturing == a ? "PRESS A KEY..." : KeyBindings.Display(g.Bindings.Get(a)),
                OnSelect = () => m.BeginCapture(a),
            });
        }
        keys.Items.Add(new MenuItem { Label = () => "RESET TO DEFAULTS", OnSelect = () => { g.Bindings.ResetDefaults(); m.Notice = "BINDINGS RESET TO DEFAULTS"; } });
        keys.Items.Add(new MenuItem { Label = () => "BACK", OnSelect = () => m.Back() });

        root.Items.Add(new MenuItem { Label = () => hasStarted() ? "RESUME GAME" : "START GAME", OnSelect = start });
        // Fresh maps: a new random seed each time (console: "map random <seed>" replays one), or back to the hand-built arena.
        root.Items.Add(new MenuItem { Label = () => "RANDOM MAP", OnSelect = () => { g.LoadRandomMap(); start(); } });
        root.Items.Add(new MenuItem { Label = () => "CLASSIC ARENA", OnSelect = () => { g.LoadClassicArena(); start(); } });
        root.Items.Add(new MenuItem
        {
            Label = () => "GAME MODE",
            Value = () => g.IsTf ? "TEAM FORTRESS" : g.IsCtf ? "CAPTURE THE FLAG" : "DEATHMATCH",
            OnSelect = () => { g.SetMode(NextMode(g.Mode, 1)); start(); },
            OnAdjust = dir => g.SetMode(NextMode(g.Mode, dir)),
        });
        root.Items.Add(new MenuItem { Label = () => "OPTIONS", OnSelect = () => m.Push(options) });
        root.Items.Add(new MenuItem { Label = () => "QUIT", OnSelect = quit });
        return m;
    }

    static GameMode NextMode(GameMode m, int dir)
    {
        int n = Enum.GetValues<GameMode>().Length;
        return (GameMode)(((int)m + (dir < 0 ? -1 : 1) + n) % n);
    }

    static MenuItem Slider(GameWorld g, string label, string cvar, float min, float max, float step, string fmt)
    {
        return new MenuItem
        {
            Label = () => label,
            Value = () => g.Console.TryGet(cvar, out var v) ? Format(v, fmt) : "n/a",
            OnAdjust = dir =>
            {
                if (!g.Console.TryGet(cvar, out var v)) return;
                float next = Math.Clamp(MathF.Round((v + dir * step) / step) * step, min, max);
                g.Console.Execute($"{cvar} {next.ToString("0.####", CultureInfo.InvariantCulture)}", echo: false);
            },
        };
    }

    static string Format(float v, string fmt) =>
        fmt == "0%" ? $"{MathF.Round(v * 100):0}%" : v.ToString(fmt, CultureInfo.InvariantCulture);
}
