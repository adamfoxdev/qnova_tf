using System.Numerics;
using Qnova.Core;

namespace Qnova.Tests;

public class KeyBindingTests
{
    static GameWorld Game()
    {
        var w = new World();
        w.Add(new(-3000, -64, -3000), new(3000, 0, 3000));
        var g = new GameWorld(w, new Vector3(0, 28, 0));
        g.Console.AddCvar("sensitivity", 0.10f);
        g.Console.AddCvar("fov", 90f);
        g.Console.AddCvar("volume", 1f);
        g.Console.AddCvar("r_plain", 0f);
        return g;
    }

    [Fact]
    public void Defaults_bind_every_action_to_a_unique_key()
    {
        var kb = new KeyBindings();
        foreach (var a in KeyBindings.All) Assert.NotNull(kb.Get(a));
        Assert.Equal(KeyBindings.All.Count, KeyBindings.All.Select(kb.Get).Distinct().Count());
        Assert.Equal("W", kb.Get(InputAction.Forward));
        Assert.Equal("SPACE", kb.Get(InputAction.Jump));
        Assert.Equal("MOUSE1", kb.Get(InputAction.Fire));
        Assert.Equal("MOUSE3", kb.Get(InputAction.Zoom));
        Assert.Equal(InputAction.Jump, kb.ActionFor("space"));   // case-insensitive
        Assert.Null(kb.ActionFor("Q"));
    }

    [Fact]
    public void Rebinding_replaces_the_old_key()
    {
        var kb = new KeyBindings();
        Assert.True(kb.Bind(InputAction.Jump, "LEFTSHIFT", out var lost));
        Assert.Null(lost);
        Assert.Equal("LEFTSHIFT", kb.Get(InputAction.Jump));
        Assert.Null(kb.ActionFor("SPACE"));          // old key is free again
    }

    [Fact]
    public void Binding_a_used_key_takes_it_from_the_other_action()
    {
        var kb = new KeyBindings();
        Assert.True(kb.Bind(InputAction.Jump, "W", out var lost));   // W was MOVE FORWARD
        Assert.Equal(InputAction.Forward, lost);
        Assert.Null(kb.Get(InputAction.Forward));
        Assert.Equal(InputAction.Jump, kb.ActionFor("W"));
        // no key is ever driving two actions
        var keys = KeyBindings.All.Select(kb.Get).Where(k => k != null).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Rebinding_to_the_same_key_is_harmless()
    {
        var kb = new KeyBindings();
        Assert.True(kb.Bind(InputAction.Forward, "W", out var lost));
        Assert.Null(lost);
        Assert.Equal("W", kb.Get(InputAction.Forward));
    }

    [Fact]
    public void Reserved_empty_and_invalid_keys_are_rejected()
    {
        var kb = new KeyBindings();
        Assert.False(kb.Bind(InputAction.Jump, "ESCAPE", out _));
        Assert.False(kb.Bind(InputAction.Jump, "grave", out _));
        Assert.False(kb.Bind(InputAction.Jump, "  ", out _));
        Assert.False(kb.Bind(InputAction.Jump, "two words", out _));
        Assert.Equal("SPACE", kb.Get(InputAction.Jump));      // unchanged

        kb.Validator = code => code != "NOPE";
        Assert.False(kb.Bind(InputAction.Jump, "nope", out _));
        Assert.True(kb.Bind(InputAction.Jump, "X", out _));
    }

    [Fact]
    public void Unbind_unbindall_and_reset()
    {
        var kb = new KeyBindings();
        kb.Unbind(InputAction.Jump);
        Assert.Null(kb.Get(InputAction.Jump));
        Assert.Null(kb.ActionFor("SPACE"));
        Assert.True(kb.UnbindKey("W"));
        Assert.False(kb.UnbindKey("W"));
        kb.UnbindAll();
        Assert.All(KeyBindings.All, a => Assert.Null(kb.Get(a)));
        kb.ResetDefaults();
        Assert.Equal("W", kb.Get(InputAction.Forward));
        Assert.Equal("SPACE", kb.Get(InputAction.Jump));
    }

    [Fact]
    public void Changed_fires_on_real_changes_only()
    {
        var kb = new KeyBindings();
        int n = 0; kb.Changed += () => n++;
        kb.Bind(InputAction.Jump, "X", out _);
        Assert.Equal(1, n);
        kb.Bind(InputAction.Jump, "ESCAPE", out _);   // rejected
        kb.Unbind(InputAction.Forward);
        Assert.Equal(2, n);
        kb.Unbind(InputAction.Forward);               // already unbound
        Assert.Equal(2, n);
    }

    [Fact]
    public void Config_round_trips_including_unbound_actions()
    {
        var a = new KeyBindings();
        a.Bind(InputAction.Jump, "LEFTSHIFT", out _);
        a.Bind(InputAction.Fire, "MOUSE2", out _);
        a.Unbind(InputAction.Mute);
        a.Bind(InputAction.Forward, "UP", out _);
        var lines = a.ToConfigLines().ToList();
        Assert.Contains("unbind mute", lines);
        Assert.Contains("bind LEFTSHIFT jump", lines);

        var b = new KeyBindings();
        Assert.True(b.LoadConfig(lines) > 0);
        foreach (var act in KeyBindings.All) Assert.Equal(a.Get(act), b.Get(act));
    }

    [Fact]
    public void Loading_config_keeps_defaults_for_actions_missing_from_the_file()
    {
        var kb = new KeyBindings();
        Assert.Equal(1, kb.LoadConfig(new[] { "bind LEFTSHIFT jump", "garbage line", "bind Z notanaction" }));
        Assert.Equal("LEFTSHIFT", kb.Get(InputAction.Jump));
        Assert.Equal("W", kb.Get(InputAction.Forward));          // untouched default
        Assert.Equal("R", kb.Get(InputAction.Respawn));
    }

    [Fact]
    public void Empty_or_garbled_config_changes_nothing()
    {
        var kb = new KeyBindings();
        kb.Bind(InputAction.Jump, "X", out _);
        Assert.Equal(0, kb.LoadConfig(Array.Empty<string>()));
        Assert.Equal(0, kb.LoadConfig(new[] { "lorem ipsum", "" }));
        Assert.Equal("X", kb.Get(InputAction.Jump));
    }

    [Fact]
    public void Config_with_a_key_stolen_from_a_default_resolves_cleanly()
    {
        var kb = new KeyBindings();
        // JUMP takes W (the default forward key); FORWARD moves to UP
        kb.LoadConfig(new[] { "bind W jump", "bind UP forward" });
        Assert.Equal("W", kb.Get(InputAction.Jump));
        Assert.Equal("UP", kb.Get(InputAction.Forward));
        Assert.Null(kb.ActionFor("SPACE"));
    }

    [Fact]
    public void Display_names_are_readable()
    {
        Assert.Equal("MOUSE LEFT", KeyBindings.Display("MOUSE1"));
        Assert.Equal("WHEEL UP", KeyBindings.Display("mwheelup"));
        Assert.Equal("1", KeyBindings.Display("ONE"));
        Assert.Equal("L SHIFT", KeyBindings.Display("LEFTSHIFT"));
        Assert.Equal("W", KeyBindings.Display("w"));
        Assert.Equal("UNBOUND", KeyBindings.Display(null));
    }

    [Fact]
    public void Console_bind_unbind_and_bindlist()
    {
        var g = Game();
        g.Console.Execute("bind LEFTSHIFT jump");
        Assert.Equal("LEFTSHIFT", g.Bindings.Get(InputAction.Jump));
        g.Console.Execute("bind leftshift");
        Assert.Contains(g.Console.Lines, l => l.Contains("\"LEFTSHIFT\" = jump"));

        g.Console.Execute("bind W jump");
        Assert.Contains(g.Console.Lines, l => l.Contains("forward is now unbound"));

        g.Console.Execute("unbind W");
        Assert.Null(g.Bindings.Get(InputAction.Jump));
        g.Console.Execute("unbind forward");                 // already unbound
        Assert.Contains(g.Console.Lines, l => l.Contains("nothing bound"));
        g.Console.Execute("unbind attack");                  // by action name
        Assert.Null(g.Bindings.Get(InputAction.Fire));

        g.Console.Execute("bindlist");
        Assert.Contains(g.Console.Lines, l => l.StartsWith("attack") && l.Contains("UNBOUND"));

        g.Console.Execute("bind_reset");
        Assert.Equal("MOUSE1", g.Bindings.Get(InputAction.Fire));
    }

    [Fact]
    public void Console_bind_reports_bad_input()
    {
        var g = Game();
        g.Console.Execute("bind X flying");
        Assert.Contains(g.Console.Lines, l => l.Contains("unknown action"));
        g.Console.Execute("bind ESCAPE jump");
        Assert.Contains(g.Console.Lines, l => l.Contains("reserved"));
        g.Bindings.Validator = c => c != "BOGUS";
        g.Console.Execute("bind bogus jump");
        Assert.Contains(g.Console.Lines, l => l.Contains("not a key I know"));
        Assert.Equal("SPACE", g.Bindings.Get(InputAction.Jump));
        g.Console.Execute("unbindall");
        Assert.All(KeyBindings.All, a => Assert.Null(g.Bindings.Get(a)));
    }

    // ---- menu integration ----

    static (MenuModel M, GameWorld G) Menu()
    {
        var g = Game();
        var m = MenuModel.Create(g, () => false, () => { }, () => { });
        m.SetSelected(4); m.Select();                        // OPTIONS
        m.SetSelected(10);                                   // KEY BINDINGS
        Assert.Equal("KEY BINDINGS", m.SelectedItem.Label());
        m.Select();
        Assert.Equal("KEY BINDINGS", m.Current.Title);
        return (m, g);
    }

    [Fact]
    public void Bindings_screen_lists_every_action_plus_reset_and_back()
    {
        var (m, g) = Menu();
        Assert.Equal(KeyBindings.All.Count + 2, m.Current.Items.Count);
        Assert.Equal("MOVE FORWARD", m.Current.Items[0].Label());
        Assert.Equal("W", m.Current.Items[0].Value!());
        Assert.Equal("MOUSE LEFT", m.Current.Items[5].Value!());
        Assert.Equal("ZOOM (HOLD)", m.Current.Items[6].Label());
        Assert.Equal("MOUSE MIDDLE", m.Current.Items[6].Value!());
        Assert.Equal("RESET TO DEFAULTS", m.Current.Items[^2].Label());
        Assert.Equal("BACK", m.Current.Items[^1].Label());
    }

    [Fact]
    public void Enter_then_a_key_rebinds_the_action()
    {
        var (m, g) = Menu();
        m.SetSelected(4);                                    // JUMP
        m.Select();
        Assert.Equal(InputAction.Jump, m.Capturing);
        Assert.Equal("PRESS A KEY...", m.SelectedItem.Value!());
        Assert.True(m.Capture("LEFTSHIFT"));
        Assert.Null(m.Capturing);
        Assert.Equal("LEFTSHIFT", g.Bindings.Get(InputAction.Jump));
        Assert.Equal("L SHIFT", m.SelectedItem.Value!());
        Assert.Null(m.Notice);
    }

    [Fact]
    public void Capturing_a_used_key_reports_who_lost_it()
    {
        var (m, g) = Menu();
        m.SetSelected(4); m.Select();
        Assert.True(m.Capture("W"));
        Assert.Equal("W", g.Bindings.Get(InputAction.Jump));
        Assert.Equal("UNBOUND", m.Current.Items[0].Value!());
        Assert.Contains("TAKEN FROM MOVE FORWARD", m.Notice);
    }

    [Fact]
    public void Escape_cancels_capture_without_leaving_the_screen()
    {
        var (m, g) = Menu();
        m.SetSelected(4); m.Select();
        Assert.True(m.Back());
        Assert.Null(m.Capturing);
        Assert.Equal("KEY BINDINGS", m.Current.Title);       // still here
        Assert.Equal("SPACE", g.Bindings.Get(InputAction.Jump));
        Assert.True(m.Back());                               // now Esc leaves
        Assert.Equal("OPTIONS", m.Current.Title);
    }

    [Fact]
    public void Backspace_unbinds_and_reserved_keys_keep_capture_open()
    {
        var (m, g) = Menu();
        m.SetSelected(4); m.Select();
        Assert.False(m.Capture("GRAVE"));                    // console key
        Assert.Equal(InputAction.Jump, m.Capturing);
        Assert.Contains("CAN'T USE", m.Notice);
        m.UnbindCaptured();
        Assert.Null(m.Capturing);
        Assert.Null(g.Bindings.Get(InputAction.Jump));
        Assert.Equal("UNBOUND", m.SelectedItem.Value!());
    }

    [Fact]
    public void Navigation_is_frozen_while_capturing()
    {
        var (m, _) = Menu();
        m.SetSelected(2); m.Select();
        int at = m.Selected;
        Assert.False(m.Move(1));
        Assert.False(m.SetSelected(0));
        Assert.False(m.Adjust(1));
        m.Select();                                          // must not restart or trigger anything
        Assert.Equal(at, m.Selected);
        Assert.Equal(InputAction.MoveLeft, m.Capturing);
    }

    [Fact]
    public void Reset_row_restores_defaults_and_back_row_leaves()
    {
        var (m, g) = Menu();
        g.Bindings.Bind(InputAction.Jump, "X", out _);
        g.Bindings.Unbind(InputAction.Fire);
        m.SetSelected(m.Current.Items.Count - 2);
        m.Select();
        Assert.Equal("SPACE", g.Bindings.Get(InputAction.Jump));
        Assert.Equal("MOUSE1", g.Bindings.Get(InputAction.Fire));
        Assert.Contains("RESET", m.Notice);
        m.SetSelected(m.Current.Items.Count - 1);
        m.Select();
        Assert.Equal("OPTIONS", m.Current.Title);
    }
}
