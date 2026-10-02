using System.Numerics;
using Qnova.Core;
using Raylib_cs;

const float S = 1f / 32f;   // Quake units -> render units
static Vector3 R(Vector3 v) => v * S;

// Developer flags (used to capture screenshots headlessly): --start, --paused, --options, --console, --lock-look, --pos "x y z", --yaw, --pitch, --exec "<console line>", --shot <png> [--shot-after <sec>]
string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
bool devLock = args.Contains("--lock-look");   // ignore mouse look (keeps screenshots framed)
bool devStart = args.Contains("--start"), devConsole = args.Contains("--console");
string? devExec = Arg("--exec"), shotPath = Arg("--shot"), devPos = Arg("--pos");
float? DevF(string n) => float.TryParse(Arg(n), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
long frameCount = 0;
double shotAfter = double.TryParse(Arg("--shot-after"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sa) ? sa : 2.0;

Raylib.SetConfigFlags(ConfigFlags.VSyncHint | ConfigFlags.Msaa4xHint);
Raylib.InitWindow(1280, 720, "qnova");
Raylib.InitAudioDevice();
Raylib.SetExitKey(KeyboardKey.Null);   // Esc backs out of menus / closes the console / pauses

// Sound effects are synthesized by Qnova.Core; each id gets a small pool so rapid fire can overlap.
const int PoolSize = 4;
var audioOk = Raylib.IsAudioDeviceReady();
var sounds = new Dictionary<SoundId, (Sound[] Pool, int[] Next)>();
if (audioOk)
    foreach (var id in Enum.GetValues<SoundId>())
    {
        var bytes = SoundSynth.ToWav(SoundSynth.Generate(id));
        var wave = Raylib.LoadWaveFromMemory(".wav", bytes);
        var pool = new Sound[PoolSize];
        for (int i = 0; i < PoolSize; i++) pool[i] = Raylib.LoadSoundFromWave(wave);
        Raylib.UnloadWave(wave);
        sounds[id] = (pool, new int[1]);
    }
bool muted = false;

void Play(SoundId id, Vector3 at, Vector3 listener, float volume = 1f)
{
    if (!audioOk || muted || !sounds.TryGetValue(id, out var e)) return;
    float dist = Vector3.Distance(at, listener);
    float v = volume * Math.Clamp(1f - dist / 2500f, 0f, 1f);
    if (v <= 0.01f) return;
    var snd = e.Pool[e.Next[0]++ % PoolSize];
    Raylib.SetSoundVolume(snd, v);
    Raylib.PlaySound(snd);
}

var game = Arena.Build();
var ui = new ConsoleUi(game.Console);
bool quit = false;

// client-side variables and commands
var popups = new DamagePopups();
var flashUntil = new Dictionary<int, float>();   // victim id -> hit-flash end time (bots 1.., dummies 1000+)
const float FlashTime = 0.16f;
game.MapLoaded += () => { popups.Clear(); flashUntil.Clear(); };   // nothing tied to the old map should linger
float sens = 0.10f, fov = 90f, timescale = 1f;
bool plainBlocks = false, damageNumbers = true, hitFlashOn = true;
float zoomFov = 30f, zoomT = 0f;   // zoomT: 0 = normal view, 1 = fully zoomed (eases in and out)
game.Console.AddCvar("sensitivity", sens, "Mouse sensitivity (degrees per pixel)", v => sens = Math.Max(0f, v));
game.Console.AddCvar("zoom_fov", zoomFov, "Vertical FOV while the zoom key is held (smaller = more magnification)", v => zoomFov = Math.Clamp(v, 5f, 80f));
game.Console.AddCvar("fov", fov, "Vertical field of view in degrees", v => fov = Math.Clamp(v, 30f, 140f));
game.Console.AddCvar("volume", 1f, "Master volume 0-1", v => { if (audioOk) Raylib.SetMasterVolume(Math.Clamp(v, 0f, 1f)); });
game.Console.AddCvar("cl_damagenumbers", 1f, "Floating arcade damage numbers over enemies you hit (0/1)", v => { damageNumbers = v != 0; if (!damageNumbers) popups.Clear(); });
game.Console.AddCvar("cl_hitflash", 1f, "Enemies flash white when you hit them (0/1)", v => hitFlashOn = v != 0);
game.Console.AddCvar("r_plain", 0f, "Render the map as plain flat-shaded blocks (0/1)", v => plainBlocks = v != 0);
game.Console.AddCvar("host_timescale", 1f, "Game speed multiplier (slow-mo / fast-forward)", v => timescale = Math.Clamp(v, 0.05f, 8f), cheat: true);
game.Console.AddCommand("quit", "quit", "Exit the game", _ => quit = true);
game.Console.AddCommand("mute", "mute", "Toggle sound", _ => { muted = !muted; game.Console.Print(muted ? "sound off" : "sound on"); });
game.Console.AddCommand("clear", "clear", "Clear the console", _ => game.Console.Lines.Clear());
game.Console.Print("qnova console - type 'help' or 'cvarlist'. Cheats: 'sv_cheats 1'.");

// Splash / main menu. The game world exists behind it but is frozen until you start.
bool inMenu = true, started = false, skipMouse = false;
long menuEnteredFrame = -1;   // frame on which Esc paused the game (that same Esc must not also resume it)
void PlayUi(SoundId id, float volume = 0.8f) => Play(id, Vector3.Zero, Vector3.Zero, volume);
var splash = new Splash();
splash.Exploded += first => PlayUi(SoundId.Explosion, first ? 0.9f : 0.3f);
var menu = MenuModel.Create(game, () => started, () =>
{
    started = true; inMenu = false; skipMouse = true;
    Raylib.DisableCursor();
}, () => quit = true);

var mapRenderer = new MapRenderer();
var itemSprites = new ItemSprites();
var dynLights = new List<(Vector3 Pos, Vector3 Color, float Radius, float Start, float Duration)>();   // explosions and muzzle flashes
var effects = new List<(Vector3 Pos, float Until, float Radius, Color Color)>();
var tracers = new List<(Vector3 A, Vector3 B, float Until, int Weapon, float Start)>();
float lastLgSound = -1f;   // the lightning gun fires ~18x/s; throttle its crackle so it doesn't machine-gun
float yaw = -90, pitch = 0, acc = 0;
bool fireHeld = false;
float hurtUntil = 0;
long seenLines = game.Console.TotalPrinted;
var feed = new List<(string Text, float Until)>();

// ---- key bindings: codes are upper-case strings ("W", "SPACE", "MOUSE1", "MWHEELUP"); KeyboardKey names double as codes ----
float FlashAmount(int id, float nowF) => hitFlashOn && flashUntil.TryGetValue(id, out var until) && until > nowF ? (until - nowF) / FlashTime : 0f;
Color Mix(Color a, Color b, float t) => new Color((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t), (byte)255);
Color Dim(Color c, float k) => new Color((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k), (byte)255);
var keyCache = new Dictionary<string, KeyboardKey?>();
KeyboardKey? KeyOf(string code)
{
    if (keyCache.TryGetValue(code, out var cached)) return cached;
    KeyboardKey? found = null;
    if (!code.All(char.IsDigit) && Enum.TryParse<KeyboardKey>(code, true, out var k) && Enum.IsDefined(k) && k != KeyboardKey.Null) found = k;
    keyCache[code] = found;
    return found;
}
MouseButton? MouseOf(string code) => code switch
{
    "MOUSE1" => MouseButton.Left, "MOUSE2" => MouseButton.Right, "MOUSE3" => MouseButton.Middle,
    "MOUSE4" => MouseButton.Side, "MOUSE5" => MouseButton.Extra, _ => null,
};
bool IsValidCode(string code) => MouseOf(code) != null || code is "MWHEELUP" or "MWHEELDOWN" || KeyOf(code) != null;
bool CodeDown(string code) => MouseOf(code) is { } mb ? Raylib.IsMouseButtonDown(mb) : KeyOf(code) is { } k && Raylib.IsKeyDown(k);
bool CodePressed(string code, float wheel) =>
    code == "MWHEELUP" ? wheel > 0 : code == "MWHEELDOWN" ? wheel < 0
    : MouseOf(code) is { } mb ? Raylib.IsMouseButtonPressed(mb) : KeyOf(code) is { } k && Raylib.IsKeyPressed(k);
bool ActionDown(InputAction a) => game.Bindings.Get(a) is { } c && CodeDown(c);
bool ActionPressed(InputAction a, float wheel) => game.Bindings.Get(a) is { } c && CodePressed(c, wheel);
string KeyName(InputAction a) => KeyBindings.Display(game.Bindings.Get(a));

game.Bindings.Validator = IsValidCode;
// Bindings persist in <config dir>/qnova/bindings.cfg (~/.config on Linux, %APPDATA% on Windows); --no-config skips it.
// (QNOVA_CONFIG_DIR overrides it.) GetFolderPath can return "" on minimal systems, so fall back rather than writing to the cwd.
string ConfigDir()
{
    var over = Environment.GetEnvironmentVariable("QNOVA_CONFIG_DIR");
    if (!string.IsNullOrEmpty(over)) return over;
    var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    if (!string.IsNullOrEmpty(xdg)) return Path.Combine(xdg, "qnova");
    foreach (var f in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
    {
        var p = Environment.GetFolderPath(f);
        if (!string.IsNullOrEmpty(p)) return Path.Combine(p, "qnova");
    }
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return !string.IsNullOrEmpty(home) ? Path.Combine(home, ".config", "qnova") : Path.Combine(AppContext.BaseDirectory, "config");
}
string bindingsFile = Path.Combine(ConfigDir(), "bindings.cfg");
if (!args.Contains("--no-config"))
{
    try { if (File.Exists(bindingsFile)) game.Bindings.LoadConfig(File.ReadAllLines(bindingsFile)); }
    catch (Exception e) { game.Console.Print($"couldn't read bindings: {e.Message}"); }
    game.Bindings.Changed += () =>
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(bindingsFile)!); File.WriteAllLines(bindingsFile, game.Bindings.ToConfigLines()); }
        catch (Exception e) { game.Console.Print($"couldn't save bindings: {e.Message}"); }
    };
}

if (devStart) { started = true; inMenu = false; Raylib.DisableCursor(); }
if (args.Contains("--paused")) started = true;               // show the pause variant of the menu
if (args.Contains("--options")) { menu.SetSelected(4); menu.Select(); menu.SetSelected(4); }
if (args.Contains("--keybinds"))                       // open Options > Key Bindings (add --capture to wait for a key on JUMP)
{
    menu.SetSelected(4); menu.Select(); menu.SetSelected(9); menu.Select(); menu.SetSelected(4);
    if (args.Contains("--capture")) menu.Select();
}
if (args.Contains("--tf")) game.SetMode(GameMode.TeamFortress);   // team fortress: capture the flag with classes
else if (args.Contains("--ctf")) game.SetMode(GameMode.Ctf);   // capture the flag on whatever map is loaded
if (devPos != null)
{
    var pp = devPos.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    game.Player.Move.Position = new Vector3(pp[0], pp[1], pp[2]);
}
if (DevF("--yaw") is float dy) yaw = dy;
if (DevF("--pitch") is float dp) pitch = dp;
if (devExec != null) game.Console.Execute(devExec, echo: false);
// --map arena | random | random:<seed>   (load a specific map at startup)
if (Arg("--map") is { } mapArg)
{
    if (mapArg.StartsWith("random", StringComparison.OrdinalIgnoreCase))
        game.LoadRandomMap(mapArg.Contains(':') && int.TryParse(mapArg[(mapArg.IndexOf(':') + 1)..], out var mseed) ? mseed : 0);
    else game.LoadClassicArena();
}
if (devConsole) ui.Toggle();

while (!quit && !Raylib.WindowShouldClose())
{
    frameCount++;
    if (inMenu)
    {
        int sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
        splash.Update(Raylib.GetFrameTime(), sw, sh);

        if (menu.Capturing != null)
        {
            // Rebinding: the next key, mouse button or wheel notch becomes the binding. Esc cancels, Backspace/Delete unbinds.
            string? code = null; bool cancel = false, unbind = false;
            int kp;
            while ((kp = (int)Raylib.GetKeyPressed()) != 0)
            {
                var key = (KeyboardKey)kp;
                if (key == KeyboardKey.Escape) cancel = true;
                else if (key == KeyboardKey.Backspace || key == KeyboardKey.Delete) unbind = true;
                else code ??= key.ToString().ToUpperInvariant();
            }
            foreach (var (mb, name) in new[] { (MouseButton.Left, "MOUSE1"), (MouseButton.Right, "MOUSE2"), (MouseButton.Middle, "MOUSE3"), (MouseButton.Side, "MOUSE4"), (MouseButton.Extra, "MOUSE5") })
                if (Raylib.IsMouseButtonPressed(mb)) code ??= name;
            float wh = Raylib.GetMouseWheelMove();
            if (wh > 0) code ??= "MWHEELUP"; else if (wh < 0) code ??= "MWHEELDOWN";

            if (cancel) { menu.CancelCapture(); PlayUi(SoundId.MenuMove); }
            else if (unbind) { menu.UnbindCaptured(); PlayUi(SoundId.MenuSelect); }
            else if (code != null && menu.Capture(code)) PlayUi(SoundId.MenuSelect);
        }
        else
        {
            bool Pressed(KeyboardKey k) => Raylib.IsKeyPressed(k) || Raylib.IsKeyPressedRepeat(k);
            if (Pressed(KeyboardKey.Down) || Pressed(KeyboardKey.S)) { if (menu.Move(1)) PlayUi(SoundId.MenuMove); }
            if (Pressed(KeyboardKey.Up) || Pressed(KeyboardKey.W)) { if (menu.Move(-1)) PlayUi(SoundId.MenuMove); }
            if (Pressed(KeyboardKey.Left) || Pressed(KeyboardKey.A)) { if (menu.Adjust(-1)) PlayUi(SoundId.MenuMove); }
            if (Pressed(KeyboardKey.Right) || Pressed(KeyboardKey.D)) { if (menu.Adjust(1)) PlayUi(SoundId.MenuMove); }
            float scroll = Raylib.GetMouseWheelMove();
            if (scroll != 0 && menu.Move(scroll > 0 ? -1 : 1)) PlayUi(SoundId.MenuMove, 0.5f);
            if (Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.KpEnter) || Raylib.IsKeyPressed(KeyboardKey.Space))
            { PlayUi(SoundId.MenuSelect); menu.Select(); }
            if (Raylib.IsKeyPressed(KeyboardKey.Escape) && menuEnteredFrame != frameCount - 1)
            {
                if (menu.Back()) PlayUi(SoundId.MenuMove);
                else if (started) { inMenu = false; skipMouse = true; Raylib.DisableCursor(); }   // Esc at the pause menu resumes
            }

            // mouse: hover highlights, click activates (left half of a value row decreases, right half increases)
            var mp = Raylib.GetMousePosition();
            var rects = splash.ItemRects;
            bool mouseMoved = Raylib.GetMouseDelta() != Vector2.Zero;
            foreach (var (rect, idx) in rects)
            {
                if (!Raylib.CheckCollisionPointRec(mp, rect)) continue;
                if (mouseMoved && menu.SetSelected(idx)) PlayUi(SoundId.MenuMove, 0.5f);
                if (Raylib.IsMouseButtonPressed(MouseButton.Left) && idx == menu.Selected)
                {
                    var item = menu.SelectedItem;
                    if (item.Value != null && item.Adjustable) menu.Adjust(mp.X < rect.X + rect.Width / 2f ? -1 : 1);
                    else menu.Select();
                    PlayUi(SoundId.MenuSelect);
                }
            }
            // The Enter/Space/click that started a capture is still queued as a key press; drop it so it isn't captured.
            if (menu.Capturing != null) while (Raylib.GetKeyPressed() != 0) { }
        }

        Raylib.BeginDrawing();
        Raylib.ClearBackground(Color.Black);
        splash.Draw(sw, sh, menu, started);
        Raylib.EndDrawing();
        if (shotPath != null && Raylib.GetTime() >= shotAfter) { Raylib.TakeScreenshot(shotPath); Console.WriteLine($"[dev] shot {shotPath}: {frameCount} frames in {Raylib.GetTime():0.0}s"); quit = true; }
        continue;
    }

    if (Raylib.IsKeyPressed(KeyboardKey.Grave)) ui.Toggle();
    if (Raylib.IsKeyPressed(KeyboardKey.Escape))
    {
        if (ui.Open) ui.Close();
        else { inMenu = true; menuEnteredFrame = frameCount; Raylib.EnableCursor(); continue; }   // Esc in game = pause menu
    }
    if (ui.Open) ui.Update();
    bool paused = ui.Open;   // game input and simulation freeze while the console is down

    var md = paused || skipMouse || devLock ? default : Raylib.GetMouseDelta();
    skipMouse = false;
    // Zoom: hold the zoom key to ease toward zoom_fov; look speed shrinks with the view so aiming stays precise.
    bool zoomHeld = !paused && ActionDown(InputAction.Zoom);
    zoomT = Math.Clamp(zoomT + (zoomHeld ? 1f : -1f) * Raylib.GetFrameTime() / 0.12f, 0f, 1f);
    float smooth = zoomT * zoomT * (3f - 2f * zoomT);
    float viewFov = fov + (Math.Min(zoomFov, fov) - fov) * smooth;
    float lookScale = MathF.Tan(viewFov * MathF.PI / 360f) / MathF.Tan(fov * MathF.PI / 360f);
    yaw -= md.X * sens * lookScale;
    pitch = Math.Clamp(pitch - md.Y * sens * lookScale, -89f, 89f);
    fireHeld = !paused && ActionDown(InputAction.Fire);
    WeaponId? sel = null;
    float wheel = paused ? 0 : Raylib.GetMouseWheelMove();
    if (!paused)
        for (int wi = 0; wi < WeaponDef.All.Length; wi++)
            if (ActionPressed(InputAction.Weapon1 + wi, wheel)) sel = (WeaponId)wi;
    int cycle = paused ? 0 : (ActionPressed(InputAction.NextWeapon, wheel) ? 1 : 0) - (ActionPressed(InputAction.PrevWeapon, wheel) ? 1 : 0);
    if (cycle != 0)
    {
        int n = WeaponDef.All.Length, cur = (int)game.Player.Current;
        for (int i = 1; i <= n; i++)   // next (or previous) weapon you actually own
        {
            var cand = (WeaponId)((cur + (cycle > 0 ? i : n - i)) % n);
            if (game.Player.Owned.Contains(cand)) { sel = cand; break; }
        }
    }
    if (!paused && ActionPressed(InputAction.Mute, wheel)) game.Console.Execute("mute", echo: false);
    if (!paused && ActionPressed(InputAction.Respawn, wheel)) game.Respawn();

    var cmd = new UserCmd { Yaw = yaw, Pitch = pitch };
    if (!paused)
    {
        cmd.Forward = (ActionDown(InputAction.Forward) ? 1 : 0) - (ActionDown(InputAction.Back) ? 1 : 0);
        cmd.Side = (ActionDown(InputAction.MoveRight) ? 1 : 0) - (ActionDown(InputAction.MoveLeft) ? 1 : 0);
        cmd.Jump = ActionDown(InputAction.Jump);
        cmd.Grapple = ActionDown(InputAction.Grapple);
    }

    if (!paused) acc += Math.Min(Raylib.GetFrameTime(), 0.1f) * timescale;
    while (!paused && acc >= GameWorld.Dt)
    {
        acc -= GameWorld.Dt;
        game.Tick(cmd, fireHeld, sel); sel = null;
        // dummies: apply knockback velocity with friction so they get thrown by rockets
        foreach (var t in game.Targets)
        {
            var np = t.Origin + t.Velocity * GameWorld.Dt;
            if (game.Map.IsEmpty(np, t.Half)) t.Origin = np; else t.Velocity = default;
            t.Velocity *= 0.95f;
            if (t.Alive == false) t.Velocity = default;
        }
        foreach (var e in game.Events)
        {
            double now = Raylib.GetTime();
            switch (e.Kind)
            {
                case EventKind.Shot:
                {
                    bool lg = e.Arg == (int)WeaponId.LightningGun;
                    if (!lg || (float)now - lastLgSound > 0.11f)
                    {
                        Play(SoundSynth.ForWeapon((WeaponId)e.Arg), e.A, game.Player.Eye, 0.8f);
                        if (lg) lastLgSound = (float)now;
                    }
                    if (e.Arg == (int)WeaponId.Axe) break;
                    if (lg) dynLights.Add((e.A, new Vector3(0.45f, 0.8f, 1.7f), 360f, (float)now, 0.06f));
                    else if (e.Arg == (int)WeaponId.Railgun) dynLights.Add((e.A, new Vector3(0.7f, 0.9f, 2.4f), 800f, (float)now, 0.18f));
                    else dynLights.Add((e.A, new Vector3(1.7f, 1.3f, 0.7f), 520f, (float)now, 0.08f));
                    break;
                }
                case EventKind.DryFire: Play(SoundId.DryFire, e.A, game.Player.Eye, 0.6f); break;
                case EventKind.Bounce: Play(SoundId.Bounce, e.A, game.Player.Eye, 0.7f); break;
                case EventKind.Explosion:
                    Play(SoundId.Explosion, e.A, game.Player.Eye);
                    dynLights.Add((e.A, new Vector3(3.0f, 1.6f, 0.7f), 1200f, (float)now, 0.55f));
                    effects.Add((e.A, (float)now + 0.35f, 120, Color.Orange)); break;
                case EventKind.Pickup: Play(SoundSynth.ForPickup((PickupKind)e.Arg), e.A, game.Player.Eye, e.B.X == 1 ? 1f : 0.5f); break;
                case EventKind.HookFire: Play(SoundId.HookFire, e.A, game.Player.Eye, e.Arg == 1 ? 0.9f : 0.5f); break;
                case EventKind.HookAttach:
                    Play(SoundId.HookHit, e.A, game.Player.Eye, e.Arg == 1 ? 1f : 0.5f);
                    effects.Add((e.A, (float)now + 0.12f, 5, Color.White)); break;
                case EventKind.JumpPad: Play(SoundId.JumpPad, e.A, game.Player.Eye, e.B.X == 1 ? 1f : 0.6f); break;
                case EventKind.FlagTaken: Play(SoundId.FlagTaken, e.A, game.Player.Eye, 0.9f); break;
                case EventKind.FlagDropped: Play(SoundId.FlagReturn, e.A, game.Player.Eye, 0.7f); break;
                case EventKind.FlagReturned: Play(SoundId.FlagReturn, e.A, game.Player.Eye, 0.9f); break;
                case EventKind.FlagCaptured: Play(SoundId.FlagCapture, e.A, game.Player.Eye, 1f); break;
                case EventKind.ItemRespawn: Play(SoundId.ItemRespawn, e.A, game.Player.Eye, 0.4f); break;
                case EventKind.Hurt:
                {
                    var fl = (HurtFlags)(int)e.B.Y;
                    if ((fl & HurtFlags.VictimHuman) != 0) hurtUntil = (float)now + 0.25f;                 // red screen flash when you're hit
                    else if ((fl & HurtFlags.ByHuman) != 0)                                                // you hit an enemy
                    {
                        flashUntil[e.Arg] = (float)now + FlashTime;
                        if (damageNumbers) popups.Add(e.Arg, e.A, (int)e.B.X, (fl & HurtFlags.Killing) != 0, (float)now);
                    }
                    break;
                }
                case EventKind.Impact: effects.Add((e.A, (float)now + 0.1f, 4, Color.Yellow)); break;
                case EventKind.Tracer:
                {
                    float life = e.Arg == (int)WeaponId.Railgun ? 0.9f : e.Arg == (int)WeaponId.LightningGun ? 0.07f : 0.05f;
                    tracers.Add((e.A, e.B, (float)now + life, e.Arg, (float)now));
                    break;
                }
            }
        }
        game.Events.Clear();
    }

    var p = game.Player;
    var cam = new Camera3D
    {
        Position = R(p.Eye), Target = R(p.Eye + p.Look), Up = Vector3.UnitY, FovY = viewFov, Projection = CameraProjection.Perspective,
    };
    float now2 = (float)Raylib.GetTime();
    effects.RemoveAll(e => e.Until < now2); tracers.RemoveAll(t => t.Until < now2);

    Raylib.BeginDrawing();
    Raylib.ClearBackground(new Color(20, 20, 26, 255));
    Raylib.BeginMode3D(cam);
    // --- lit world: gather lights, then draw solids + decor with the map shader ---
    var lightSrcs = new List<LightSrc>(64);
    foreach (var l in game.Lights)
    {
        float fl = l.Flicker ? 0.82f + 0.18f * MathF.Sin(now2 * 13f + l.Position.X) * MathF.Sin(now2 * 5.1f + l.Position.Z) : 1f;
        lightSrcs.Add(new LightSrc(l.Position, l.Color * fl, l.Radius));
    }
    foreach (var k in game.Pickups)
        if (k.Active)
        {
            var (gr, gg, gb) = PickupSprites.Glow(PickupSprites.For(k));
            var pc = new Vector3(gr, gg, gb);
            lightSrcs.Add(new LightSrc(k.Position + new Vector3(0, 20, 0), pc, k.Kind == PickupKind.Weapon ? 260f : 190f));
        }
    foreach (var fl in game.Flags)
        lightSrcs.Add(new LightSrc(fl.Pos + new Vector3(0, 30, 0), fl.Team == Team.Red ? new Vector3(1.5f, 0.25f, 0.2f) : new Vector3(0.25f, 0.5f, 1.6f), 420f));
    foreach (var pr in game.Projectiles)
        if (pr.Kind != ProjectileKind.Nail) lightSrcs.Add(new LightSrc(pr.Pos, pr.Kind == ProjectileKind.Rocket ? new Vector3(1.6f, 0.8f, 0.3f) : new Vector3(0.4f, 1.0f, 0.3f), 380f));
    dynLights.RemoveAll(d => now2 - d.Start > d.Duration);
    foreach (var d in dynLights)
    {
        float t01 = (now2 - d.Start) / d.Duration;
        lightSrcs.Add(new LightSrc(d.Pos, d.Color * (1f - t01) * (1f - t01), d.Radius * (0.6f + 0.4f * t01)));
    }
    mapRenderer.Frame(p.Eye, lightSrcs, now2, plainBlocks);

    mapRenderer.Begin();
    foreach (var sol in game.Map.Solids)
    {
        var mat = SurfaceRules.For(sol, game.CeilingY);
        MapRenderer.Box(sol, mat, MapRenderer.Palette(mat, sol));
    }
    foreach (var dc in game.Decor)
    {
        var col = dc.Surface == Surface.Emissive ? new Color((byte)dc.Color.X, (byte)dc.Color.Y, (byte)dc.Color.Z, (byte)255) : MapRenderer.Palette(dc.Surface, dc.Box);
        MapRenderer.Box(dc.Box, dc.Surface, col);
    }
    for (int ti = 0; ti < game.Targets.Count; ti++)
    {
        var t = game.Targets[ti];
        if (!t.Alive) continue;
        var tcol = new Color(190, 70 + Math.Clamp(t.Health, 0, 185), 60, 255);
        float tf = FlashAmount(1000 + ti, now2);
        // a fresh hit lights the body up bright white (drawn unlit), easing back to normal
        if (tf > 0) MapRenderer.Box(Aabb.FromCenter(t.Origin, t.Half), Surface.Emissive, Mix(Dim(tcol, 0.55f), new Color(235, 235, 235, 255), tf));
        else MapRenderer.Box(Aabb.FromCenter(t.Origin, t.Half), Surface.Flat, tcol);
    }
    mapRenderer.End();
    float tnow = (float)Raylib.GetTime();
    // launch pads: glowing chevrons rise off each plate
    foreach (var pad in game.JumpPads)
        for (int ci = 0; ci < 3; ci++)
        {
            float ph = (tnow * 0.9f + ci / 3f) % 1f;
            var cpos = new Vector3(pad.Center.X, pad.Trigger.Min.Y + 8f + ph * 80f, pad.Center.Z);
            float csz = (1.5f - ph) * 1.0f;
            Raylib.DrawCubeV(R(cpos), new Vector3(csz, 0.08f, csz), Raylib.Fade(new Color(120, 225, 255, 255), 1f - ph));
        }
    for (int i = 0; i < game.Pickups.Count; i++)
    {
        var k = game.Pickups[i];
        var floor = k.Position - new Vector3(0, 15f, 0);
        Raylib.DrawCubeV(R(floor), new Vector3(1.1f, 0.06f, 1.1f), k.Active ? new Color(60, 60, 70, 255) : new Color(35, 35, 40, 255));   // pad
        if (!k.Active) continue;
        var sid = PickupSprites.For(k);
        bool wpn = PickupSprites.IsWeapon(sid);
        var at = R(k.Position + new Vector3(0, (plainBlocks ? 8 : (wpn ? 2.2f : 1.4f) * 16f - 6f) + MathF.Sin(tnow * 2.5f + i) * 4f, 0));
        if (plainBlocks)
        {
            var (pr_, pg_, pb_) = PickupSprites.Glow(sid);
            var col = new Color((byte)(pr_ * 255), (byte)(pg_ * 255), (byte)(pb_ * 255), (byte)255);
            float sz = wpn ? 1.0f : 0.7f;
            Raylib.DrawCubeV(at, new Vector3(sz, sz, sz), col);
            Raylib.DrawCubeWiresV(at, new Vector3(sz, sz, sz), new Color(20, 20, 20, 255));
            continue;
        }
        var (gr2, gg2, gb2) = PickupSprites.Glow(sid);
        Raylib.BeginBlendMode(BlendMode.Additive);
        Raylib.DrawBillboard(cam, itemSprites.Glow, at, wpn ? 3.4f : 2.4f, new Color((byte)(gr2 * 150), (byte)(gg2 * 150), (byte)(gb2 * 150), (byte)255));
        Raylib.EndBlendMode();
        Raylib.DrawBillboard(cam, itemSprites.Get(sid), at, wpn ? 2.2f : 1.4f, Color.White);
    }
    foreach (var fl in game.Flags)
    {
        var tc = fl.Team == Team.Red ? new Color(225, 45, 40, 255) : new Color(50, 100, 235, 255);
        var basePos = fl.Home - new Vector3(0, Flag.Half.Y - 2f, 0);
        Raylib.DrawCubeV(R(basePos), new Vector3(2.4f, 0.1f, 2.4f), Raylib.Fade(tc, fl.State == FlagState.Home ? 0.9f : 0.35f));   // base plate stays put
        if (fl.State == FlagState.Dropped && ((int)(tnow * 4f) & 1) == 0) continue;                                            // a dropped flag blinks
        bool carried = fl.State == FlagState.Carried;
        var foot = fl.Pos - new Vector3(0, Flag.Half.Y - 2f, 0);
        var top = foot + new Vector3(0, carried ? 80f : 170f, 0);
        Raylib.DrawCylinderEx(R(foot), R(top), 0.1f, 0.1f, 6, new Color(200, 200, 205, 255));
        // banner: a few slabs that ripple in the wind
        for (int bi = 0; bi < 5; bi++)
        {
            float wave = MathF.Sin(tnow * 5f + bi * 0.9f) * 3f * (bi / 4f);
            var bp2 = top + new Vector3(14f + bi * 14f, -22f + wave * 0.3f, wave);
            Raylib.DrawCubeV(R(bp2), new Vector3(0.5f, 1.3f - bi * 0.08f, 0.06f), tc);
        }
        Raylib.BeginBlendMode(BlendMode.Additive);
        Raylib.DrawCylinderEx(R(foot), R(foot + new Vector3(0, 400f, 0)), 0.5f, 0.08f, 8, Raylib.Fade(tc, 0.5f));                // beacon beam
        Raylib.EndBlendMode();
    }
    foreach (var b in game.Bots)
    {
        var bp = b.Body;
        if (!bp.Alive) continue;
        var body = bp.Team == Team.Red ? new Color(200, 60, 50, 255) : new Color(70, 120, 210, 255);
        float bf = FlashAmount(bp.Id, now2);
        mapRenderer.Begin();
        if (bf > 0) MapRenderer.Box(Aabb.FromCenter(bp.Move.Position, MoveVars.Half), Surface.Emissive, Mix(Dim(body, 0.55f), new Color(235, 235, 235, 255), bf));
        else MapRenderer.Box(Aabb.FromCenter(bp.Move.Position, MoveVars.Half), Surface.Flat, body);
        MapRenderer.Box(Aabb.FromCenter(bp.Move.Position + new Vector3(0, 40, 0), new Vector3(8, 8, 8)), Surface.Flat, new Color(225, 195, 165, 255));
        mapRenderer.End();
        Raylib.DrawSphere(R(bp.Move.Position + new Vector3(0, 34, 0)), 0.32f, Mix(new Color(230, 200, 170, 255), new Color(255, 255, 255, 255), bf));
        var look = bp.Look;
        Raylib.DrawLine3D(R(bp.Eye), R(bp.Eye + look * 40f), Color.Red);   // gun barrel: shows where it is aiming
        Raylib.DrawCubeV(R(bp.Eye + look * 22f), new Vector3(0.12f, 0.12f, 0.12f) + Vector3.Abs(look) * 0.5f, new Color(40, 40, 40, 255));
    }
    foreach (var hc in game.Combatants)   // grappling hooks: a rope from the hand to the tip
    {
        var hk = hc.Hook;
        if (hk.State == HookState.None) continue;
        var hand = hc.Eye + PlayerMove.RightFlat(hc.Yaw) * 10f + new Vector3(0, -9f, 0) + hc.Look * 12f;
        Raylib.DrawCylinderEx(R(hand), R(hk.Pos), 0.014f, 0.014f, 4, new Color(205, 175, 120, 255));
        Raylib.DrawCubeV(R(hk.Pos), new Vector3(0.16f, 0.16f, 0.16f), hk.State == HookState.Attached ? new Color(255, 200, 90, 255) : new Color(225, 225, 235, 255));
    }
    foreach (var pr in game.Projectiles)
        Raylib.DrawSphere(R(pr.Pos), pr.Kind == ProjectileKind.Nail ? 0.05f : 0.15f, pr.Kind == ProjectileKind.Nail ? Color.Yellow : pr.Kind == ProjectileKind.Rocket ? Color.Red : Color.DarkGreen);
    foreach (var (pos, until, radius, color) in effects) Raylib.DrawSphere(R(pos), radius * S * (1 - (until - now2)), Raylib.Fade(color, 0.6f));
    var flashRng = new Random((int)(now2 * 90f));
    float Thick(Vector3 worldPos, float perUnit) => 0.004f + perUnit * Vector3.Distance(R(worldPos), cam.Position);   // ~constant screen width
    foreach (var (a, b, until, wpn, start) in tracers)
    {
        var dir = b - a;
        float len = dir.Length();
        if (len < 1f) continue;
        dir /= len;
        // your own beams leave from the lower-right 'hand' (like the hook rope); other shooters' from just below their eye
        bool mine = Vector3.DistanceSquared(a, p.Eye) < 4f;
        var from = mine ? a + PlayerMove.RightFlat(p.Yaw) * 9f + new Vector3(0, -9f, 0) + dir * 14f : a + dir * 10f + new Vector3(0, -6f, 0);
        if (wpn == (int)WeaponId.LightningGun)
        {
            // jagged bolt: re-rolled every frame so it crackles
            var side = Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitY) + new Vector3(0, 0.001f, 0));
            var upv = Vector3.Cross(side, dir);
            var pts = new List<Vector3> { from };
            int n = Math.Max(4, (int)(len / 60f));
            for (int i = 1; i < n; i++)
            {
                float t = i / (float)n, amp = 9f * MathF.Sin(t * MathF.PI);
                pts.Add(from + (b - from) * t + side * ((float)flashRng.NextDouble() * 2 - 1) * amp + upv * ((float)flashRng.NextDouble() * 2 - 1) * amp);
            }
            pts.Add(b);
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                Raylib.DrawCylinderEx(R(pts[i]), R(pts[i + 1]), Thick(pts[i], 0.0022f), Thick(pts[i + 1], 0.0022f), 4, new Color(90, 150, 255, 255));   // blue body
                Raylib.DrawLine3D(R(pts[i]), R(pts[i + 1]), Color.White);                                        // hot core
            }
        }
        else if (wpn == (int)WeaponId.Railgun)
        {
            // straight core plus a corkscrew that fades over ~1s
            float age = (now2 - start) / 0.9f, fade = MathF.Max(0f, 1f - age);
            var rightv = MathF.Abs(dir.Y) > 0.95f ? Vector3.UnitX : Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitY));
            var upv = Vector3.Cross(rightv, dir);
            float spin = (now2 - start) * 9f;
            var prev = from + (rightv * MathF.Cos(spin) + upv * MathF.Sin(spin)) * 7f;
            int steps = Math.Min(900, (int)(len / 7f));
            for (int i = 1; i <= steps; i++)
            {
                float d = i * 7f, ang = d * 0.05f + spin;
                var cur = from + dir * d + (rightv * MathF.Cos(ang) + upv * MathF.Sin(ang)) * (7f * (0.6f + 0.4f * fade));
                Raylib.DrawCylinderEx(R(prev), R(cur), Thick(prev, 0.0016f), Thick(cur, 0.0016f), 3, Raylib.Fade(new Color(90, 190, 255, 255), fade));
                prev = cur;
            }
            Raylib.DrawCylinderEx(R(from), R(b), Thick(from, 0.0026f) * (0.6f + 0.4f * fade), Thick(b, 0.0026f) * (0.6f + 0.4f * fade), 4, Raylib.Fade(new Color(240, 252, 255, 255), fade));
        }
        else Raylib.DrawLine3D(R(from), R(b), Color.Yellow);
    }
    Raylib.EndMode3D();
    if (damageNumbers) popups.Draw(cam, R, now2);

    foreach (var b in game.Bots)
    {
        var bp = b.Body;
        if (!bp.Alive) continue;
        var sp = Raylib.GetWorldToScreen(R(bp.Move.Position + new Vector3(0, 52, 0)), cam);
        var toBot = bp.Move.Position - p.Eye;
        if (Vector3.Dot(toBot, p.Look) <= 0) continue;   // behind the camera
        int bw = 60;
        Raylib.DrawRectangle((int)sp.X - bw / 2, (int)sp.Y, bw, 6, new Color(30, 30, 30, 200));
        Raylib.DrawRectangle((int)sp.X - bw / 2, (int)sp.Y, bw * Math.Clamp(bp.Health, 0, 100) / 100, 6, new Color(220, 60, 60, 255));
        Raylib.DrawText(bp.Name, (int)sp.X - bw / 2, (int)sp.Y - 18, 16, Color.White);
    }
    if (hurtUntil > now2) Raylib.DrawRectangle(0, 0, Raylib.GetScreenWidth(), Raylib.GetScreenHeight(), new Color(200, 0, 0, (int)(110 * Math.Min(1f, (hurtUntil - now2) / 0.25f))));

    var w = WeaponDef.Get(p.Current);
    float speed = MathF.Sqrt(p.Move.Velocity.X * p.Move.Velocity.X + p.Move.Velocity.Z * p.Move.Velocity.Z);
    // crosshair: turns red and thickens when the current weapon would hit an enemy right now
    {
        int ccx = Raylib.GetScreenWidth() / 2, ccy = Raylib.GetScreenHeight() / 2;
        bool onEnemy = !paused && game.AimingAtEnemy(p);
        var cc = onEnemy ? new Color(255, 50, 40, 255) : Color.White;
        float th = onEnemy ? 3f : 1f, arm = onEnemy ? 11f : 8f;
        Raylib.DrawLineEx(new Vector2(ccx - arm, ccy), new Vector2(ccx + arm, ccy), th, cc);
        Raylib.DrawLineEx(new Vector2(ccx, ccy - arm), new Vector2(ccx, ccy + arm), th, cc);
    }
    Raylib.DrawText($"HP {Math.Max(0, p.Health)}   {w.Name}   shells {p.Shells}  nails {p.Nails}  rockets {p.Rockets}  cells {p.Cells}  slugs {p.Slugs}   frags {p.Frags}", 16, 680, 22, Color.White);
    Raylib.DrawText($"speed {speed:0}  {(p.Move.OnGround ? "ground" : "air")}", 16, 16, 22, Color.White);
    if (game.IsTf)
    {
        string cl = p.Class == PlayerClass.None ? "" : ClassDef.Get(p.Class).Name.ToUpperInvariant();
        if (p.NextClass != p.Class) cl += $"  (next: {ClassDef.Get(p.NextClass).Name})";
        Raylib.DrawText(cl, 16, 660 - 60, 22, new Color(255, 220, 120, 255));
    }
    Raylib.DrawText(game.MapName, Raylib.GetScreenWidth() - 16 - Raylib.MeasureText(game.MapName, 16), Raylib.GetScreenHeight() - 26, 16, new Color(150, 150, 150, 255));
    Raylib.DrawText($"{KeyName(InputAction.Forward)}/{KeyName(InputAction.MoveLeft)}/{KeyName(InputAction.Back)}/{KeyName(InputAction.MoveRight)} move  {KeyName(InputAction.Jump)} jump  MOUSE look  {KeyName(InputAction.Fire)} fire  {KeyName(InputAction.Zoom)} zoom  {KeyName(InputAction.Grapple)} hook  {KeyName(InputAction.PrevWeapon)}/{KeyName(InputAction.NextWeapon)} weapon  {KeyName(InputAction.Mute)} mute  {KeyName(InputAction.Respawn)} reset  ~ console  ESC menu", 16, 44, 16, Color.Gray);
    if (!p.Alive)
    {
        float left = Math.Max(0f, p.RespawnAt - game.Time);
        Raylib.DrawText($"YOU DIED - respawning in {left:0.0}s", 400, 340, 30, Color.Red);
    }

    // weapon bar: owned guns bright, current one boxed
    string[] short_ = { "Axe", "SG", "SSG", "NG", "SNG", "GL", "RL", "LG", "RG" };
    for (int i = 0; i < short_.Length; i++)
    {
        int x = 16 + i * 74, y = 640;
        bool owned = p.Owned.Contains((WeaponId)i), cur = p.Current == (WeaponId)i;
        if (cur) Raylib.DrawRectangleLines(x - 4, y - 3, 68, 26, Color.Yellow);
        var kn = KeyName(InputAction.Weapon1 + i);
        if (kn.Length > 4) kn = kn[..4];   // keep the bar compact for long key names
        Raylib.DrawText($"{kn} {short_[i]}", x, y, 20, owned ? (cur ? Color.Yellow : Color.White) : new Color(90, 90, 90, 255));
    }

    if (game.IsCtf)
    {
        int cw = Raylib.GetScreenWidth();
        string sc = $"RED {game.TeamScore[1]}  -  {game.TeamScore[2]} BLUE";
        Raylib.DrawText(sc, cw / 2 - Raylib.MeasureText(sc, 30) / 2, 12, 30, Color.White);
        Raylib.DrawText($"first to {game.CaptureLimit}", cw / 2 - Raylib.MeasureText($"first to {game.CaptureLimit}", 16) / 2, 44, 16, Color.Gray);
        string FlagText(Team t)
        {
            var f = game.FlagOf(t)!;
            return f.State switch { FlagState.Home => "HOME", FlagState.Dropped => $"DROPPED {Math.Max(0, (int)(f.ReturnAt - game.Time))}s", _ => f.Carrier == p ? "YOU HAVE IT" : $"TAKEN by {f.Carrier?.Name}" };
        }
        Raylib.DrawText($"YOUR FLAG: {FlagText(Team.Red)}", 16, 108 + 8 * 22 + 8, 20, new Color(255, 110, 100, 255));
        Raylib.DrawText($"ENEMY FLAG: {FlagText(Team.Blue)}", 16, 108 + 8 * 22 + 32, 20, new Color(110, 160, 255, 255));
        if (game.Winner != Team.None)
        {
            string wt = $"{game.Winner.Label()} TEAM WINS";
            Raylib.DrawText(wt, cw / 2 - Raylib.MeasureText(wt, 60) / 2, 250, 60, game.Winner == Team.Red ? new Color(255, 90, 80, 255) : new Color(100, 150, 255, 255));
        }
        else if (game.Carrying(p) != null)
        {
            string ct = "YOU HAVE THE FLAG - GET HOME";
            Raylib.DrawText(ct, cw / 2 - Raylib.MeasureText(ct, 24) / 2, 70, 24, Color.Yellow);
        }
    }

    // scoreboard (top right)
    int sy = 72, sx = Raylib.GetScreenWidth() - (game.IsTf ? 310 : 260);   // below the controls hint line, which spans the top
    foreach (var c in game.Combatants.OrderByDescending(c => c.Frags))
    {
        var scol = c == p ? Color.Yellow : c.Team == Team.Red ? new Color(255, 130, 120, 255) : c.Team == Team.Blue ? new Color(130, 170, 255, 255) : Color.White;
        string cls = game.IsTf && c.Class != PlayerClass.None ? $" {ClassDef.Get(c.Class).Name[..3].ToUpperInvariant()}" : "";
        Raylib.DrawText($"{c.Name,-6} {c.Frags,3} / {c.Deaths,-3}{cls}", sx, sy, 20, scol);
        sy += 22;
    }

    // recent console output (kills, command results) fades in the corner while the console is closed
    long fresh = game.Console.TotalPrinted - seenLines;
    seenLines = game.Console.TotalPrinted;
    for (long i = Math.Min(fresh, game.Console.Lines.Count); i > 0; i--)
        feed.Add((game.Console.Lines[(int)(game.Console.Lines.Count - i)], now2 + 5f));
    while (feed.Count > 8) feed.RemoveAt(0);
    feed.RemoveAll(f => f.Until < now2);
    if (!ui.Open)
        for (int i = 0; i < feed.Count; i++)
            Raylib.DrawText(feed[i].Text, 16, 90 + i * 22, 20, Raylib.Fade(Color.White, Math.Min(1f, feed[i].Until - now2)));
    if (ui.Open) ui.Draw(Raylib.GetScreenWidth(), Raylib.GetScreenHeight());
    Raylib.EndDrawing();
    if (shotPath != null && Raylib.GetTime() >= shotAfter) { Raylib.TakeScreenshot(shotPath); Console.WriteLine($"[dev] shot {shotPath}: {frameCount} frames in {Raylib.GetTime():0.0}s"); quit = true; }
}
foreach (var (pool, _) in sounds.Values) foreach (var snd in pool) Raylib.UnloadSound(snd);
if (audioOk) Raylib.CloseAudioDevice();
popups.Unload();
mapRenderer.Unload();
splash.Unload();
ui.Unload();
itemSprites.Dispose();
Raylib.CloseWindow();
