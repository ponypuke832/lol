using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Shush
{
    public class OptSpec
    {
        public string Type, Key, Label, Unit = "", DefStr = "";
        public int Min, Max, DefInt;
        public string[] Choices;
    }

    public class FeatureSpec
    {
        public string Id, Name, Kind, DelayLabel, Sub, Cat, Help = "";
        public int Delay, DelayMax = 250;
        public bool Auto;
        public List<OptSpec> Opts = new List<OptSpec>();
    }

    public class FeatureCfg
    {
        public bool Enabled { get; set; }
        public string Bind { get; set; } = "";
        public int Delay { get; set; }
        public Dictionary<string, int> Num { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, string> Str { get; set; } = new Dictionary<string, string>();
    }

    public class Settings
    {
        public int Version { get; set; } = 0;
        public Dictionary<string, FeatureCfg> Features { get; set; } = new Dictionary<string, FeatureCfg>();
        public Dictionary<string, string> Keys { get; set; } = new Dictionary<string, string>();
        public string ResetMethod { get; set; } = "Right Click";
        public int ScrollMs { get; set; } = 50;
        public int TapMs { get; set; } = 25;
        public bool RobloxOnly { get; set; } = true;
        public bool Debug { get; set; } = false;
        public string Theme { get; set; } = "Midnight";
        public bool HideTaskbar { get; set; } = true;
        public string UiKey { get; set; } = "INSERT";
        public string PanicKey { get; set; } = "F12";
        public int Fps { get; set; } = 144;
        public bool FpsFloor { get; set; } = true;
        public int Opacity { get; set; } = 100;
        public int Scale { get; set; } = 100;

        public static readonly string[] ThemeNames = { "Light", "Midnight", "Crimson", "Slate" };

        public static readonly Dictionary<string, string> DefaultKeys = new Dictionary<string, string>
        {
            { "edit", "G" }, { "pickup", "E" }, { "slot1", "F" }, { "slot2", "1" }, { "slot3", "2" }, { "slot4", "3" },
            { "slot5", "4" }, { "slot6", "5" }, { "slot7", "6" }, { "wall", "Z" }, { "floor", "X" }, { "ramp", "C" }, { "pyramid", "V" },
        };

        /// <summary>Fill anything missing, migrate old configs, so a partial/old file never causes errors.</summary>
        public void Fix()
        {
            if (Features == null) Features = new Dictionary<string, FeatureCfg>();
            if (Keys == null) Keys = new Dictionary<string, string>();
            foreach (var kv in DefaultKeys) if (!Keys.ContainsKey(kv.Key) || string.IsNullOrEmpty(Keys[kv.Key])) Keys[kv.Key] = kv.Value;
            foreach (var spec in Specs.All)
            {
                FeatureCfg f;
                if (!Features.TryGetValue(spec.Id, out f) || f == null)
                {
                    f = new FeatureCfg { Delay = spec.Delay };
                    Features[spec.Id] = f;
                }
                if (f.Num == null) f.Num = new Dictionary<string, int>();
                if (f.Str == null) f.Str = new Dictionary<string, string>();
                if (f.Bind == null) f.Bind = "";
                foreach (var o in spec.Opts)
                {
                    if (o.Type == "choice" || o.Type == "bind" || o.Type == "tiles") { if (!f.Str.ContainsKey(o.Key)) f.Str[o.Key] = o.DefStr; }
                    else if (!f.Num.ContainsKey(o.Key)) f.Num[o.Key] = o.DefInt;
                }
            }

            // old separate Drag Wall/Floor/Ramp/Pyramid macros -> one Auto Build macro
            var ab = Features["auto_build"];
            foreach (var p in Specs.Pieces)
            {
                FeatureCfg old;
                if (Features.TryGetValue("drag_" + p, out old) && old != null)
                {
                    if (!string.IsNullOrEmpty(old.Bind) && string.IsNullOrEmpty(ab.Str["bind_" + p])) ab.Str["bind_" + p] = old.Bind;
                    if (old.Enabled) ab.Enabled = true;
                    if (old.Str != null && old.Str.ContainsKey("mode")) ab.Str["mode"] = old.Str["mode"];
                    Features.Remove("drag_" + p);
                }
            }

            if (Array.IndexOf(ThemeNames, Theme) < 0) Theme = "Midnight";
            if (string.IsNullOrEmpty(UiKey)) UiKey = "INSERT";
            if (string.IsNullOrEmpty(PanicKey)) PanicKey = "F12";
            if (UiKey == PanicKey) PanicKey = UiKey == "F12" ? "F11" : "F12";
            if (Fps < 30) Fps = 144;

            // older versions had delays that were too fast -> apply the slower defaults once
            if (Version < 2)
            {
                TapMs = 25;
                foreach (var spec in Specs.All)
                {
                    var f = Features[spec.Id];
                    f.Delay = spec.Delay;
                    foreach (var o in spec.Opts) if (o.Type == "slider") f.Num[o.Key] = o.DefInt;
                }
                Version = 2;
            }

            // a macro is active when it has a key (the on/off switch can still turn it off later)
            if (Version < 3)
            {
                foreach (var spec in Specs.All)
                {
                    if (spec.Auto) continue;
                    var f = Features[spec.Id];
                    if (Specs.IsPieceKind(spec.Kind)) f.Enabled = Specs.Pieces.Any(pc => !string.IsNullOrEmpty(f.Str["bind_" + pc]));
                    else f.Enabled = !string.IsNullOrEmpty(f.Bind);
                }
                Version = 3;
            }
        }
    }

    public static class Specs
    {
        public static readonly string[] Cells = { "Center", "Top Left", "Top", "Top Right", "Left", "Right", "Bottom Left", "Bottom", "Bottom Right" };
        public static readonly Dictionary<string, int[]> CellXY = new Dictionary<string, int[]>
        {
            { "Center", new[] { 0, 0 } }, { "Top Left", new[] { -1, -1 } }, { "Top", new[] { 0, -1 } }, { "Top Right", new[] { 1, -1 } },
            { "Left", new[] { -1, 0 } }, { "Right", new[] { 1, 0 } }, { "Bottom Left", new[] { -1, 1 } }, { "Bottom", new[] { 0, 1 } },
            { "Bottom Right", new[] { 1, 1 } },
        };
        public static readonly string[] Slots = { "Slot 1", "Slot 2", "Slot 3", "Slot 4", "Slot 5", "Slot 6", "Slot 7" };
        public static readonly string[] Pieces = { "wall", "floor", "ramp", "pyramid" };
        public static readonly List<FeatureSpec> All = new List<FeatureSpec>();
        public static readonly Dictionary<string, FeatureSpec> ById = new Dictionary<string, FeatureSpec>();

        /// <summary>Macros that have one trigger key per build piece instead of a single key.</summary>
        public static bool IsPieceKind(string kind) { return kind == "autobuild" || kind == "instabuild"; }

        /// <summary>Text for the "?" next to a setting (by option key). "delay" = the main delay slider of every macro.</summary>
        public static readonly Dictionary<string, string> OptHelp = new Dictionary<string, string>
        {
            { "delay", "Main wait of this macro (opening edit / equipping before the next step). Raised automatically to the FPS minimum." },
            { "mode", "Once = one press runs it once. Hold = keeps running while you hold the key. Toggle = press to start, press again to stop." },
            { "repeat_gap", "Small pause between repeats in Hold / Toggle mode." },
            { "fps", "Set this to your in-game FPS. Every step waits just enough frames for the game to see it. If inputs get missed, slide it LOWER for more consistency." },
            { "click_hold", "How long the click is held down." },
            { "confirm", "How the edit is confirmed: by releasing the click, or by tapping the Edit key again." },
            { "confirm_edit", "Tap the Edit key after clicking to confirm the edit." },
            { "auto_reset", "After the edit, opens edit again, resets the structure and closes edit (uses Settings > Reset method)." },
            { "reset_delay", "Wait after the edit before the reset starts." },
            { "reset_step", "Wait between each step of the reset (open edit, reset, close edit)." },
            { "open_edit", "Press your Edit key first when the macro starts." },
            { "release_delay", "Wait before confirming / releasing at the end." },
            { "gap", "Wait between the first and second edit." },
            { "place", "Which mouse button places a build in game." },
            { "place_gap", "Hold / Toggle mode: wait between each placed build." },
            { "swap_back", "After placing, switch back to the weapon slot chosen in Weapon Pullout." },
            { "direction", "Which way the scroll wheel turns. Use the same direction you reset with by hand." },
            { "tick_gap", "Wait between scroll ticks. 0 = one tick every frame (fastest)." },
            { "start", "Which tile your crosshair is on when edit opens." },
            { "dist", "How far the mouse moves from one tile to the next." },
            { "steps", "How many small moves each tile-to-tile movement is split into." },
            { "step_delay", "Wait between each small move." },
            { "tile_dwell", "How long the crosshair stays on each tile." },
            { "return_aim", "Move your aim back to where it started after the edit." },
            { "repress", "Lets go of the click and presses it again right after, for games that miss the first click." },
            { "slot", "Which weapon slot to pull out." },
            { "fire", "Click once after pulling out the weapon." },
            { "trigger", "What counts as finishing an edit." },
            { "window", "How long after opening edit a confirm still counts." },
            { "equip_delay", "Wait after the edit before switching to the weapon." },
            { "hold", "How long the pickup key is held each press." },
        };

        static OptSpec OSlider(string key, string label, int def, int min, int max, string unit = " ms")
        { return new OptSpec { Type = "slider", Key = key, Label = label, DefInt = def, Min = min, Max = max, Unit = unit }; }
        static OptSpec OSwitch(string key, string label, bool def)
        { return new OptSpec { Type = "switch", Key = key, Label = label, DefInt = def ? 1 : 0 }; }
        static OptSpec OChoice(string key, string label, string def, params string[] ch)
        { return new OptSpec { Type = "choice", Key = key, Label = label, DefStr = def, Choices = ch }; }
        static OptSpec OBind(string key, string label)
        { return new OptSpec { Type = "bind", Key = key, Label = label, DefStr = "" }; }
        static OptSpec OTiles(string key, string label, string def)
        { return new OptSpec { Type = "tiles", Key = key, Label = label, DefStr = def }; }
        static OptSpec OFps() { return OSlider("fps", "In-game FPS", 144, 60, 240, " fps"); }

        static List<OptSpec> Repeat()
        {
            return new List<OptSpec>
            {
                OChoice("mode", "Repeat mode", "Hold", "Once", "Hold", "Toggle"),
                OSlider("repeat_gap", "Repeat gap", 1, 0, 100),
            };
        }

        static List<OptSpec> AutoReset()
        {
            return new List<OptSpec>
            {
                OSwitch("auto_reset", "Auto Reset after edit", false),
                OSlider("reset_delay", "Reset wait after edit", 120, 0, 500),
                OSlider("reset_step", "Reset step delay", 45, 0, 250),
            };
        }

        static List<OptSpec> PieceBinds()
        {
            return new List<OptSpec>
            {
                OBind("bind_wall", "Wall trigger"),
                OBind("bind_floor", "Floor trigger"),
                OBind("bind_ramp", "Ramp trigger"),
                OBind("bind_pyramid", "Pyramid trigger"),
            };
        }

        static List<OptSpec> TileOpts(string defTiles, string defConfirm)
        {
            return new List<OptSpec>
            {
                OChoice("start", "Crosshair starts on", "Center", Cells),
                OTiles("tiles", "Tiles to select", defTiles),
                OChoice("confirm", "Confirm by", defConfirm, "Release click", "Tap Edit", "Hold Edit"),
                OSlider("dist", "Move distance", 40, 5, 150, " px"),
                OSlider("steps", "Sweep steps", 5, 1, 12, ""),
                OSlider("step_delay", "Step delay", 6, 0, 100),
                OSlider("tile_dwell", "Time on each tile", 20, 0, 250),
                OSlider("release_delay", "Release delay", 40, 0, 250),
                OSwitch("return_aim", "Return aim after edit", true),
            };
        }

        static FeatureSpec Add(string id, string name, string kind, string cat, int delay, string dlabel, string sub,
                               List<OptSpec> opts, bool auto = false, int delayMax = 250)
        {
            var s = new FeatureSpec { Id = id, Name = name, Kind = kind, Cat = cat, Delay = delay, DelayLabel = dlabel, Sub = sub, Auto = auto, Opts = opts, DelayMax = delayMax };
            All.Add(s);
            ById[id] = s;
            return s;
        }

        static void Help(string id, string text) { ById[id].Help = text; }

        static Specs()
        {
            // Editing
            var drag = Repeat();
            drag.AddRange(TileOpts("111000000", "Release click"));
            Add("drag_edit", "Drag Edit", "dragedit", "Editing", 40, "Grid delay", "Hold click, sweep over tiles", drag);

            var single = Repeat();
            single.Add(OSlider("click_hold", "Click hold", 45, 1, 250));
            single.AddRange(TileOpts("010010010", "Tap Edit"));
            Add("single_edit", "Single Edit", "single", "Editing", 40, "Grid delay", "Pick tiles, one press", single);

            var dbl = Repeat();
            dbl.Add(OSlider("click_hold", "Click hold", 45, 1, 250));
            dbl.Add(OSlider("gap", "Inter-structure delay", 110, 0, 400));
            dbl.AddRange(AutoReset());
            Add("double_edit", "Double Edit", "double", "Editing", 40, "Grid delay", "Edit two structures", dbl);

            var simple = Repeat();
            simple.Add(OSlider("click_hold", "Click hold", 45, 1, 250));
            simple.Add(OSwitch("confirm_edit", "Confirm with Edit key", false));
            simple.AddRange(AutoReset());
            Add("simple_edit", "Simple Edit", "edit", "Editing", 40, "Grid delay", "Edit + click center tile", simple);

            // Building
            var abOpts = new List<OptSpec>
            {
                OChoice("mode", "Mode", "Hold", "Hold", "Toggle"),
                OSwitch("repress", "Re-press click (reliability)", false),
            };
            abOpts.AddRange(PieceBinds());
            Add("auto_build", "Auto Build", "autobuild", "Building", 10, "Equip to hold delay", "Wall, floor, ramp, pyramid", abOpts);

            // Utility
            Add("weapon_pullout", "Weapon Pullout", "auto", "Utility", 0, "Fire delay", "Always on after an edit",
                new List<OptSpec>
                {
                    OChoice("slot", "Weapon slot", "Slot 1", Slots),
                    OSwitch("fire", "Fire on pullout", false),
                    OChoice("trigger", "Edit confirmed by", "Both", "Both", "Click release", "Edit key"),
                    OSlider("window", "Edit window", 3000, 500, 10000),
                    OSlider("equip_delay", "Equip delay", 15, 0, 250),
                }, true, 100);

            Add("auto_pickup", "Auto Pickup", "pickup", "Utility", 1, "Spam delay", "Spam the pickup key",
                new List<OptSpec>
                {
                    OChoice("mode", "Mode", "Hold", "Hold", "Toggle"),
                    OSlider("hold", "Press length", 2, 1, 50),
                }, false, 100);

            // Games > Framebreak
            var fbDrag = new List<OptSpec>
            {
                OFps(),
                OSwitch("open_edit", "Open edit when pressed", true),
                OChoice("confirm", "When you let go", "Release click", "Release click", "Tap Edit"),
                OSlider("release_delay", "Confirm delay", 20, 0, 250),
            };
            fbDrag.AddRange(AutoReset());
            Add("fb_drag", "Drag", "fbdrag", "Framebreak", 30, "Edit open delay", "Hold key = hold click, drag over tiles", fbDrag);

            var fbd = Repeat();
            fbd.Add(OSlider("click_hold", "Click hold", 30, 1, 250));
            fbd.Add(OSlider("gap", "Delay between edits", 60, 0, 400));
            fbd.Add(OChoice("confirm", "Confirm by", "Release click", "Release click", "Tap Edit"));
            Add("fb_double", "Double Edit", "fbdouble", "Framebreak", 30, "Edit open delay", "Hold = keeps double editing", fbd);

            var rapid = Repeat();
            rapid.Insert(0, OFps());
            rapid.Add(OSlider("click_hold", "Click hold", 1, 1, 100));
            rapid.Add(OChoice("confirm", "Confirm by", "Release click", "Release click", "Tap Edit"));
            Add("fb_rapid", "Rapid Edit", "rapid", "Framebreak", 0, "Step delay", "Spams one tile edit super fast", rapid, false, 100);

            var inst = new List<OptSpec>
            {
                OChoice("mode", "Mode", "Hold", "Once", "Hold", "Toggle"),
                OFps(),
                OChoice("place", "Place with", "Right Click", "Right Click", "Left Click"),
                OSlider("click_hold", "Place click hold", 1, 1, 100),
                OSlider("place_gap", "Delay between places", 0, 0, 250),
                OSwitch("swap_back", "Swap back to weapon after", false),
            };
            inst.AddRange(PieceBinds());
            Add("fb_instant", "Instant Build", "instabuild", "Framebreak", 0, "Equip to place delay", "Key = equip + place, no clicking", inst, false, 100);

            Add("fb_editreset", "Edit Reset", "editreset", "Framebreak", 0, "Edit open delay", "Hold key = scroll-wheel reset",
                new List<OptSpec>
                {
                    OChoice("mode", "Mode", "Hold", "Hold", "Toggle"),
                    OFps(),
                    OChoice("direction", "Scroll direction", "Scroll Down", "Scroll Down", "Scroll Up"),
                    OSlider("tick_gap", "Time between scroll ticks", 0, 0, 100),
                    OSwitch("open_edit", "Open edit first", false),
                }, false, 100);

            // "?" help text for every macro
            Help("drag_edit", "One press opens edit, holds left click and sweeps over the tiles you picked in the 3x3 grid, then confirms. Pick tiles and confirm style in the dots menu.");
            Help("single_edit", "One press opens edit and clicks each tile you picked, one by one, then confirms.");
            Help("double_edit", "Edits two structures back to back: edit + click, short wait, edit + click again. Optional Auto Reset afterwards.");
            Help("simple_edit", "Opens edit and clicks the tile under your crosshair. Optional confirm and Auto Reset.");
            Help("auto_build", "Hold a build key: equips that piece and holds left click so it keeps placing. Set a key for each piece in the dots menu.");
            Help("weapon_pullout", "Always on (no key needed): after you finish an edit it switches to your weapon, and can fire once.");
            Help("auto_pickup", "Spams your pickup key while you hold (or toggle) the trigger.");
            Help("fb_drag", "Hold the key = opens edit and holds left click, you drag the mouse over the tiles. Let go = release (and confirm). FPS slider: set to your in-game FPS, lower it if drags get missed. Auto Reset resets the edit after.");
            Help("fb_double", "Edit + select tile, twice. In Hold mode it keeps double editing while you hold the key.");
            Help("fb_rapid", "Edits the tile under your crosshair over and over, as fast as the game can read it, while you hold the key. Each step waits 1 frame of the FPS slider. Slide it lower if some edits get missed.");
            Help("fb_instant", "Press a piece key: equips that piece and places it instantly, you never click. Hold to keep placing. REQUIRES Turbo Building OFF in game settings. Set a key for each piece in the dots menu.");
            Help("fb_editreset", "While you hold the trigger it sends scroll-wheel ticks, giving the same effect as scroll-wheel resetting without touching the wheel.");
        }
    }

    public static class Store
    {
        public static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "config");
        static readonly JsonSerializerOptions Opt = new JsonSerializerOptions { WriteIndented = true };
        static string PathOf(string name) { return Path.Combine(Dir, name + ".json"); }

        public static List<string> Profiles()
        {
            var l = new List<string>();
            try
            {
                if (Directory.Exists(Dir))
                    foreach (var f in Directory.GetFiles(Dir, "*.json"))
                    {
                        var n = Path.GetFileNameWithoutExtension(f);
                        if (n != "_state") l.Add(n);
                    }
            }
            catch { }
            if (!l.Contains("default")) l.Add("default");
            l.Sort(StringComparer.OrdinalIgnoreCase);
            return l;
        }

        public static string Active()
        {
            try
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Dir, "_state.json")));
                string a;
                if (d != null && d.TryGetValue("active", out a) && File.Exists(PathOf(a))) return a;
            }
            catch { }
            return "default";
        }

        public static Settings Load(string name)
        {
            try
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathOf(name)));
                if (s != null) { s.Fix(); return s; }
            }
            catch (Exception e) { Log.E("load " + name + ": " + e.Message); }
            var n = new Settings();
            n.Fix();
            return n;
        }

        public static void Save(string name, Settings s)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(PathOf(name), JsonSerializer.Serialize(s, Opt));
                File.WriteAllText(Path.Combine(Dir, "_state.json"), JsonSerializer.Serialize(new Dictionary<string, string> { { "active", name } }));
            }
            catch (Exception e) { Log.E("save " + name + ": " + e.Message); }
        }

        public static Settings Clone(Settings s)
        {
            var c = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(s));
            c.Fix();
            return c;
        }

        public static void Delete(string name) { try { File.Delete(PathOf(name)); } catch { } }
    }
}
