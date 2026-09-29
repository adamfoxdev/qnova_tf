using System.Numerics;
using Qnova.Core;

namespace Qnova.Tests;

public class MenuTests
{
    static GameWorld Game()
    {
        var w = new World();
        w.Add(new(-3000, -64, -3000), new(3000, 0, 3000));
        var g = new GameWorld(w, new Vector3(0, 28, 0));
        // client cvars are normally registered by the frontend
        g.Console.AddCvar("sensitivity", 0.10f);
        g.Console.AddCvar("fov", 90f);
        g.Console.AddCvar("zoom_fov", 30f);
        g.Console.AddCvar("volume", 1f);
        g.Console.AddCvar("r_plain", 0f);
        g.Console.AddCvar("cl_damagenumbers", 1f);
        return g;
    }

    static (MenuModel M, Func<(bool Started, bool Quit)> State) Make(GameWorld g)
    {
        bool started = false, quit = false;
        var m = MenuModel.Create(g, () => started, () => started = true, () => quit = true);
        return (m, () => (started, quit));
    }

    [Fact]
    public void Root_menu_has_start_options_quit_and_wraps()
    {
        var (m, _) = Make(Game());
        Assert.Equal(new[] { "START GAME", "RANDOM MAP", "CLASSIC ARENA", "GAME MODE", "OPTIONS", "QUIT" }, m.Current.Items.Select(i => i.Label()));
        Assert.Equal(0, m.Selected);
        m.Move(-1);
        Assert.Equal(5, m.Selected);       // wrapped up to QUIT
        m.Move(1);
        Assert.Equal(0, m.Selected);       // wrapped down
        Assert.False(m.SetSelected(0));    // no change
        Assert.True(m.SetSelected(1));
    }

    [Fact]
    public void Start_and_quit_invoke_callbacks_and_start_becomes_resume()
    {
        var (m, state) = Make(Game());
        m.Select();
        Assert.True(state().Started);
        Assert.Equal("RESUME GAME", m.Current.Items[0].Label());
        m.SetSelected(5);
        Assert.False(state().Quit);
        m.Select();
        Assert.True(state().Quit);
    }

    [Fact]
    public void Options_opens_and_back_returns_to_the_same_selection()
    {
        var (m, _) = Make(Game());
        m.SetSelected(4); m.Select();
        Assert.Equal("OPTIONS", m.Current.Title);
        Assert.False(m.AtRoot);
        Assert.Equal(0, m.Selected);
        Assert.True(m.Back());
        Assert.True(m.AtRoot);
        Assert.Equal(4, m.Selected);
        Assert.False(m.Back());            // nothing above the root
    }

    [Fact]
    public void Back_item_at_bottom_of_options_leaves_options()
    {
        var (m, _) = Make(Game());
        m.SetSelected(4); m.Select();
        m.Move(-1);                        // wraps to BACK
        Assert.Equal("BACK", m.SelectedItem.Label());
        m.Select();
        Assert.True(m.AtRoot);
    }

    [Fact]
    public void Sliders_adjust_cvars_within_limits()
    {
        var g = Game();
        var (m, _) = Make(g);
        m.SetSelected(4); m.Select();                   // options; item 0 = sensitivity
        Assert.Equal("0.10", m.SelectedItem.Value!());
        m.Adjust(1); m.Adjust(1);
        Assert.Equal(0.12f, g.Console.Get("sensitivity"), 0.001f);
        for (int i = 0; i < 100; i++) m.Adjust(-1);
        Assert.Equal(0.02f, g.Console.Get("sensitivity"), 0.001f);   // clamped at min
        for (int i = 0; i < 100; i++) m.Adjust(1);
        Assert.Equal(0.5f, g.Console.Get("sensitivity"), 0.001f);    // clamped at max

        m.Move(1);                                      // fov
        Assert.Equal("90", m.SelectedItem.Value!());
        m.Adjust(1);
        Assert.Equal(95f, g.Console.Get("fov"));
        for (int i = 0; i < 50; i++) m.Adjust(1);
        Assert.Equal(120f, g.Console.Get("fov"));

        m.Move(1);                                      // zoom fov
        Assert.Equal("ZOOM FOV", m.SelectedItem.Label());
        Assert.Equal("30", m.SelectedItem.Value!());
        m.Adjust(1); m.Adjust(1);
        Assert.Equal(40f, g.Console.Get("zoom_fov"));
        for (int i = 0; i < 50; i++) m.Adjust(-1);
        Assert.Equal(10f, g.Console.Get("zoom_fov"));   // clamped at min

        m.Move(1);                                      // volume
        Assert.Equal("100%", m.SelectedItem.Value!());
        m.Adjust(-1);
        Assert.Equal("90%", m.SelectedItem.Value!());
    }

    [Fact]
    public void Bot_skill_bot_count_and_autohop_options()
    {
        var g = Game();
        var (m, _) = Make(g);
        m.SetSelected(4); m.Select();
        m.SetSelected(4);                               // BOT SKILL
        Assert.Equal("BOT SKILL", m.SelectedItem.Label());
        m.Adjust(1); m.Adjust(1); m.Adjust(1); m.Adjust(1); m.Adjust(1);
        Assert.Equal(5, g.BotSkill);

        m.SetSelected(5);                               // BOTS
        Assert.Equal("0", m.SelectedItem.Value!());
        for (int i = 0; i < 10; i++) m.Adjust(1);
        Assert.Equal(MenuModel.MaxBots, g.Bots.Count);
        m.Adjust(-1);
        Assert.Equal(MenuModel.MaxBots - 1, g.Bots.Count);
        Assert.Equal((MenuModel.MaxBots - 1).ToString(), m.SelectedItem.Value!());

        m.SetSelected(6);                               // AUTO BUNNY-HOP
        Assert.Equal("OFF", m.SelectedItem.Value!());
        m.Select();                                     // Enter toggles
        Assert.Equal("ON", m.SelectedItem.Value!());
        Assert.True(g.Player.Move.AutoHop);
        m.Adjust(-1);
        Assert.Equal("OFF", m.SelectedItem.Value!());
    }

    [Fact]
    public void Plain_blocks_option_toggles_the_render_cvar()
    {
        var g = Game();
        var (m, _) = Make(g);
        m.SetSelected(4); m.Select();
        m.SetSelected(7);
        Assert.Equal("PLAIN BLOCKS", m.SelectedItem.Label());
        Assert.Equal("OFF", m.SelectedItem.Value!());
        m.Select();
        Assert.Equal(1f, g.Console.Get("r_plain"));
        Assert.Equal("ON", m.SelectedItem.Value!());
        m.Adjust(-1);                                   // Left/Right toggle too
        Assert.Equal(0f, g.Console.Get("r_plain"));
        m.Move(1);
        Assert.Equal("DAMAGE NUMBERS", m.SelectedItem.Label());
        Assert.Equal("ON", m.SelectedItem.Value!());
        m.Select();                                     // toggles the floating damage numbers off
        Assert.Equal(0f, g.Console.Get("cl_damagenumbers"));
        Assert.Equal("OFF", m.SelectedItem.Value!());
        m.Adjust(1);
        Assert.Equal("ON", m.SelectedItem.Value!());
        m.Move(1);
        Assert.Equal("CLASS", m.SelectedItem.Label());
        m.Move(1);
        Assert.Equal("KEY BINDINGS", m.SelectedItem.Label());
        m.Move(1);
        Assert.Equal("BACK", m.SelectedItem.Label());
    }

    [Fact]
    public void Missing_cvar_shows_na_and_adjust_is_harmless()
    {
        var w = new World(); w.Add(new(-100, -64, -100), new(100, 0, 100));
        var g = new GameWorld(w, new Vector3(0, 28, 0));   // frontend cvars not registered
        var (m, _) = Make(g);
        m.SetSelected(4); m.Select();
        Assert.Equal("n/a", m.SelectedItem.Value!());
        m.Adjust(1);                                       // must not throw
    }

    [Fact]
    public void Adjust_on_non_adjustable_items_returns_false()
    {
        var (m, _) = Make(Game());
        Assert.False(m.Adjust(1));
    }
}
