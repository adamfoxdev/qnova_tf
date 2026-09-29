using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Qnova.Core;

public sealed class MapFormatException : Exception
{
    public MapFormatException(string message) : base(message) { }
}

/// <summary>The on-disk map format (JSON, version 1) shared by the game and the HTML map editor.
/// Vectors are [x,y,z]; boxes are [minX,minY,minZ,maxX,maxY,maxZ]; units are Quake units, Y up.</summary>
public static class MapJson
{
    public const int Version = 1;

    static readonly string[] SurfaceNames = { "floor", "wall", "metal", "flat", "ceiling", "emissive" };

    public static int DefaultAmount(PickupKind k) => k switch
    {
        PickupKind.Health => 25, PickupKind.Shells => 20, PickupKind.Nails => 50, PickupKind.Rockets => 5,
        PickupKind.Cells => 60, PickupKind.Slugs => 10, _ => 0,
    };

    // ------------------------------------------------------------------ write

    public static string ToJson(MapData m)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("format", Version);
            w.WriteString("name", m.Name);
            w.WriteNumber("seed", m.Seed);
            w.WriteNumber("half", m.Half);
            w.WriteNumber("height", m.Height);
            Vec(w, "playerSpawn", m.PlayerSpawn);
            if (m.RedFlag is { } rf) Vec(w, "redFlag", rf);
            if (m.BlueFlag is { } bf) Vec(w, "blueFlag", bf);

            w.WriteStartArray("solids");
            foreach (var s in m.Solids) BoxArr(w, s);
            w.WriteEndArray();

            w.WriteStartArray("decor");
            foreach (var d in m.Decor)
            {
                w.WriteStartObject();
                w.WritePropertyName("box"); BoxValue(w, d.Box);
                w.WriteString("surface", SurfaceNames[(int)d.Surface]);
                if (d.Surface == Surface.Emissive) Vec(w, "color", d.Color);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("lights");
            foreach (var l in m.Lights)
            {
                w.WriteStartObject();
                Vec(w, "pos", l.Position); Vec(w, "color", l.Color);
                w.WriteNumber("radius", l.Radius);
                if (l.Flicker) w.WriteBoolean("flicker", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("pickups");
            foreach (var k in m.Pickups)
            {
                w.WriteStartObject();
                w.WriteString("kind", k.Kind.ToString().ToLowerInvariant());
                if (k.Kind == PickupKind.Weapon) w.WriteString("weapon", k.Weapon.ToString());
                w.WriteNumber("amount", k.Amount);
                Vec(w, "pos", k.Position);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("pads");
            foreach (var p in m.JumpPads)
            {
                w.WriteStartObject();
                w.WritePropertyName("trigger"); BoxValue(w, p.Trigger);
                Vec(w, "target", p.Target);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("spawns");
            foreach (var s in m.Spawns) VecValue(w, s);
            w.WriteEndArray();
            w.WriteStartArray("dummies");
            foreach (var d in m.Dummies) VecValue(w, d);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    static void Vec(Utf8JsonWriter w, string name, Vector3 v) { w.WritePropertyName(name); VecValue(w, v); }
    static void VecValue(Utf8JsonWriter w, Vector3 v)
    {
        w.WriteStartArray(); w.WriteNumberValue(Round(v.X)); w.WriteNumberValue(Round(v.Y)); w.WriteNumberValue(Round(v.Z)); w.WriteEndArray();
    }
    static void BoxArr(Utf8JsonWriter w, Aabb b) => BoxValue(w, b);
    static void BoxValue(Utf8JsonWriter w, Aabb b)
    {
        w.WriteStartArray();
        foreach (var f in new[] { b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z }) w.WriteNumberValue(Round(f));
        w.WriteEndArray();
    }
    static float Round(float f) => f;   // shortest text that reads back to the same float

    // ------------------------------------------------------------------ read

    public static MapData Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException e) { throw new MapFormatException($"not valid JSON: {e.Message}"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new MapFormatException("top level must be an object");
            int ver = root.TryGetProperty("format", out var fv) && fv.ValueKind == JsonValueKind.Number ? fv.GetInt32() : 0;
            if (ver != Version) throw new MapFormatException($"unsupported map format {ver} (expected {Version})");

            var m = new MapData
            {
                Name = Str(root, "name", "Custom map"),
                Seed = (int)Num(root, "seed", 0),
                Half = Num(root, "half", 2048f),
                Height = Num(root, "height", 768f),
            };
            if (m.Half < 256f || m.Half > 16384f) throw new MapFormatException($"half {m.Half} out of range (256-16384)");
            if (m.Height < 128f || m.Height > 4096f) throw new MapFormatException($"height {m.Height} out of range (128-4096)");

            foreach (var (e, i) in Items(root, "solids"))
            {
                var b = ReadBox(e, $"solids[{i}]");
                m.Solids.Add(b);
            }
            foreach (var (e, i) in Items(root, "decor"))
            {
                string at = $"decor[{i}]";
                var box = ReadBox(Req(e, "box", at), at + ".box");
                var surf = ParseSurface(Str(e, "surface", "flat"), at);
                var col = e.TryGetProperty("color", out var cv) ? ReadVec(cv, at + ".color") : Vector3.Zero;
                m.Decor.Add(new DecorBox(box, surf, col));
            }
            foreach (var (e, i) in Items(root, "lights"))
            {
                string at = $"lights[{i}]";
                m.Lights.Add(new MapLight
                {
                    Position = ReadVec(Req(e, "pos", at), at + ".pos"),
                    Color = e.TryGetProperty("color", out var cv) ? ReadVec(cv, at + ".color") : Vector3.One,
                    Radius = Num(e, "radius", 600f),
                    Flicker = e.TryGetProperty("flicker", out var fl) && fl.ValueKind == JsonValueKind.True,
                });
            }
            foreach (var (e, i) in Items(root, "pickups"))
            {
                string at = $"pickups[{i}]";
                string kindName = Str(e, "kind", "");
                if (!Enum.TryParse<PickupKind>(kindName, ignoreCase: true, out var kind)) throw new MapFormatException($"{at}: unknown kind \"{kindName}\"");
                var k = new Pickup { Kind = kind, Position = ReadVec(Req(e, "pos", at), at + ".pos") };
                if (kind == PickupKind.Weapon)
                {
                    string wn = Str(e, "weapon", "");
                    if (!Enum.TryParse<WeaponId>(wn, ignoreCase: true, out var wid)) throw new MapFormatException($"{at}: unknown weapon \"{wn}\"");
                    k.Weapon = wid;
                }
                int amt = (int)Num(e, "amount", 0);
                k.Amount = amt > 0 ? amt : DefaultAmount(kind);
                m.Pickups.Add(k);
            }
            foreach (var (e, i) in Items(root, "pads"))
            {
                string at = $"pads[{i}]";
                m.JumpPads.Add(new JumpPad { Trigger = ReadBox(Req(e, "trigger", at), at + ".trigger"), Target = ReadVec(Req(e, "target", at), at + ".target") });
            }
            foreach (var (e, i) in Items(root, "spawns")) m.Spawns.Add(ReadVec(e, $"spawns[{i}]"));
            foreach (var (e, i) in Items(root, "dummies")) m.Dummies.Add(ReadVec(e, $"dummies[{i}]"));

            if (m.Solids.Count == 0) throw new MapFormatException("map has no solids");
            if (m.Spawns.Count == 0) throw new MapFormatException("map has no spawn points");
            m.PlayerSpawn = root.TryGetProperty("playerSpawn", out var ps) ? ReadVec(ps, "playerSpawn") : m.Spawns[0];
            if (root.TryGetProperty("redFlag", out var rf)) m.RedFlag = ReadVec(rf, "redFlag");
            if (root.TryGetProperty("blueFlag", out var bf)) m.BlueFlag = ReadVec(bf, "blueFlag");
            if ((m.RedFlag == null) != (m.BlueFlag == null)) throw new MapFormatException("redFlag and blueFlag must both be set (or neither)");
            return m;
        }
    }

    static IEnumerable<(JsonElement, int)> Items(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var arr)) yield break;
        if (arr.ValueKind != JsonValueKind.Array) throw new MapFormatException($"\"{name}\" must be an array");
        int i = 0;
        foreach (var e in arr.EnumerateArray()) yield return (e, i++);
    }

    static JsonElement Req(JsonElement e, string name, string at)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) throw new MapFormatException($"{at}: missing \"{name}\"");
        return v;
    }

    static string Str(JsonElement e, string name, string def) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? def : def;

    static float Num(JsonElement e, string name, float def) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : def;

    static float[] Floats(JsonElement v, int n, string at)
    {
        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() != n) throw new MapFormatException($"{at}: expected {n} numbers");
        var r = new float[n]; int i = 0;
        foreach (var x in v.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Number) throw new MapFormatException($"{at}: expected {n} numbers");
            r[i++] = x.GetSingle();
        }
        return r;
    }

    static Vector3 ReadVec(JsonElement v, string at) { var f = Floats(v, 3, at); return new Vector3(f[0], f[1], f[2]); }

    static Aabb ReadBox(JsonElement v, string at)
    {
        var f = Floats(v, 6, at);
        var min = new Vector3(Math.Min(f[0], f[3]), Math.Min(f[1], f[4]), Math.Min(f[2], f[5]));
        var max = new Vector3(Math.Max(f[0], f[3]), Math.Max(f[1], f[4]), Math.Max(f[2], f[5]));
        if (max.X - min.X < 0.01f || max.Y - min.Y < 0.01f || max.Z - min.Z < 0.01f) throw new MapFormatException($"{at}: box has no volume");
        return new Aabb(min, max);
    }

    static Surface ParseSurface(string s, string at)
    {
        int i = Array.IndexOf(SurfaceNames, s.ToLowerInvariant());
        if (i < 0) throw new MapFormatException($"{at}: unknown surface \"{s}\" (use {string.Join(", ", SurfaceNames)})");
        return (Surface)i;
    }

    public static MapData Load(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new MapFormatException($"can't read {path}: {e.Message}"); }
        return Parse(text);
    }
}
