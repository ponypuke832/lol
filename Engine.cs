using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Shush
{
    internal sealed class DragState
    {
        public volatile bool Held;
        public volatile bool Down;
        public long T;
        public readonly object Lock = new object();
    }

    /// <summary>Hooks + macros.</summary>
    internal sealed class Engine
    {
        const int MinClick = 30;   // ms a click is held at minimum

        public Settings S;
        public volatile bool Running;
        public volatile Action<string> Capture;
        public event Action ToggleUi;
        public event Action PanicEvt;

        volatile Dictionary<string, string> bindMap = new Dictionary<string, string>();
        readonly ConcurrentDictionary<string, string> held = new ConcurrentDictionary<string, string>();
        readonly ConcurrentDictionary<string, CancellationTokenSource> loops = new ConcurrentDictionary<string, CancellationTokenSource>();
        readonly ConcurrentDictionary<string, byte> busy = new ConcurrentDictionary<string, byte>();
        readonly ConcurrentDictionary<string, DragState> drags = new ConcurrentDictionary<string, DragState>();
        readonly HashSet<string> swallowUp = new HashSet<string>();
        readonly Stopwatch clock = Stopwatch.StartNew();

        volatile bool focus = true, stop;
        bool inEdit, clickArmed, editDown;
        double editT;
        uint hookTid;
        Native.HookProc kbProc, msProc;
        long hookBusy;

        public Engine(Settings s) { S = s; RebuildBinds(); }

        public void Start()
        {
            new Thread(FocusLoop) { IsBackground = true }.Start();
            new Thread(HookThread) { IsBackground = true }.Start();
            new Thread(Watchdog) { IsBackground = true }.Start();
            new Thread(EmergencyHotkey) { IsBackground = true }.Start();
        }

        // ------------------------------------------------------------- safety nets
        static void Kill(string why)
        {
            try
            {
                Directory.CreateDirectory(Store.Dir);
                File.AppendAllText(Path.Combine(Store.Dir, "shush.log"), DateTime.Now.ToString("HH:mm:ss  ") + "HARD EXIT: " + why + Environment.NewLine);
            }
            catch { }
            Process.GetCurrentProcess().Kill();
        }

        void EmergencyHotkey()
        {
            if (!Native.RegisterHotKey(IntPtr.Zero, 0x5348, 0x0001 | 0x0002 | 0x0004 | 0x4000, 0x7B)) { Log.E("emergency hotkey could not be registered"); return; }
            Native.MSG m;
            while (Native.GetMessage(out m, IntPtr.Zero, 0, 0) > 0)
                if (m.message == 0x0312) Kill("emergency hotkey (Ctrl+Alt+Shift+F12)");
        }

        static bool PhysicallyDown(string name)
        {
            int vk;
            switch (name)
            {
                case "MOUSE1": vk = 0x01; break;
                case "MOUSE2": vk = 0x02; break;
                case "MOUSE3": vk = 0x04; break;
                case "MOUSE4": vk = 0x05; break;
                case "MOUSE5": vk = 0x06; break;
                default: if (!Input.VK.TryGetValue(name, out vk)) return true; break;
            }
            return (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        void Watchdog()
        {
            var miss = new Dictionary<string, int>();
            while (!stop)
            {
                try
                {
                    long now = Stopwatch.GetTimestamp();
                    long hb = Interlocked.Read(ref hookBusy);
                    if (hb != 0 && (now - hb) * 1000 / Stopwatch.Frequency > 2000) Kill("hook callback stuck for 2s");
                    foreach (var kv in Input.Sending)
                        if ((now - kv.Value) * 1000 / Stopwatch.Frequency > 2000) Kill("SendInput stuck for 2s");

                    var k = S.Keys;
                    var injected = new HashSet<string> { "MOUSE1", "MOUSE2", k["edit"], k["pickup"], k["wall"], k["floor"], k["ramp"], k["pyramid"],
                                                         k["slot1"], k["slot2"], k["slot3"], k["slot4"], k["slot5"], k["slot6"], k["slot7"] };
                    foreach (var kv in held)
                    {
                        if (injected.Contains(kv.Key) || PhysicallyDown(kv.Key)) { miss.Remove(kv.Key); continue; }
                        int c;
                        miss.TryGetValue(kv.Key, out c);
                        miss[kv.Key] = ++c;
                        if (c >= 2)
                        {
                            miss.Remove(kv.Key);
                            string target;
                            if (held.TryRemove(kv.Key, out target)) { Log.E("missed key-up for " + kv.Key + ", releasing " + target); Release(target); }
                        }
                    }
                }
                catch (Exception e) { Log.E("watchdog: " + e.Message); }
                Thread.Sleep(250);
            }
        }

        public void Stop()
        {
            stop = true;
            StopAll();
            if (hookTid != 0) Native.PostThreadMessage(hookTid, 0x12, IntPtr.Zero, IntPtr.Zero);
        }

        public void RebuildBinds()
        {
            var m = new Dictionary<string, string>();
            foreach (var kv in S.Features)
            {
                FeatureSpec spec;
                if (!kv.Value.Enabled || !Specs.ById.TryGetValue(kv.Key, out spec) || spec.Auto) continue;
                if (Specs.IsPieceKind(spec.Kind))
                {
                    foreach (var p in Specs.Pieces)
                    {
                        string bind;
                        if (kv.Value.Str.TryGetValue("bind_" + p, out bind) && !string.IsNullOrEmpty(bind)) m[bind] = kv.Key + ":" + p;
                    }
                }
                else if (!string.IsNullOrEmpty(kv.Value.Bind)) m[kv.Value.Bind] = kv.Key;
            }
            bindMap = m;
        }

        // ---------------------------------------------------------------- hooks
        void HookThread()
        {
            hookTid = Native.GetCurrentThreadId();
            Input.HookThreadId = Environment.CurrentManagedThreadId;
            kbProc = KbProc;
            msProc = MsProc;
            var mod = Native.GetModuleHandle(null);
            var kh = Native.SetWindowsHookEx(13, kbProc, mod, 0);
            var mh = Native.SetWindowsHookEx(14, msProc, mod, 0);
            if (kh == IntPtr.Zero || mh == IntPtr.Zero) Log.E("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
            Native.MSG msg;
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            if (kh != IntPtr.Zero) Native.UnhookWindowsHookEx(kh);
            if (mh != IntPtr.Zero) Native.UnhookWindowsHookEx(mh);
        }

        IntPtr KbProc(int n, IntPtr w, IntPtr l)
        {
            Interlocked.Exchange(ref hookBusy, Stopwatch.GetTimestamp());
            try { return KbProcCore(n, w, l); }
            finally { Interlocked.Exchange(ref hookBusy, 0); }
        }

        IntPtr KbProcCore(int n, IntPtr w, IntPtr l)
        {
            try
            {
                if (n == 0)
                {
                    int m = (int)w;
                    if (m == 0x100 || m == 0x101 || m == 0x104 || m == 0x105)
                    {
                        var k = Marshal.PtrToStructure<Native.KBDLL>(l);
                        string name;
                        if ((k.flags & 0x10) == 0 && Input.VkName.TryGetValue((int)k.vk, out name))
                            if (OnInput(name, m == 0x100 || m == 0x104)) return (IntPtr)1;
                    }
                }
            }
            catch (Exception e) { Log.E("hook error: " + e); }
            return Native.CallNextHookEx(IntPtr.Zero, n, w, l);
        }

        IntPtr MsProc(int n, IntPtr w, IntPtr l)
        {
            Interlocked.Exchange(ref hookBusy, Stopwatch.GetTimestamp());
            try { return MsProcCore(n, w, l); }
            finally { Interlocked.Exchange(ref hookBusy, 0); }
        }

        IntPtr MsProcCore(int n, IntPtr w, IntPtr l)
        {
            try
            {
                if (n == 0)
                {
                    int m = (int)w;
                    string name = null; bool down = false;
                    switch (m)
                    {
                        case 0x201: name = "MOUSE1"; down = true; break;
                        case 0x202: name = "MOUSE1"; break;
                        case 0x204: name = "MOUSE2"; down = true; break;
                        case 0x205: name = "MOUSE2"; break;
                        case 0x207: name = "MOUSE3"; down = true; break;
                        case 0x208: name = "MOUSE3"; break;
                        case 0x20B: name = "X"; down = true; break;
                        case 0x20C: name = "X"; break;
                    }
                    if (name != null)
                    {
                        var s = Marshal.PtrToStructure<Native.MSLL>(l);
                        if ((s.flags & 1) == 0)
                        {
                            if (name == "X") name = ((s.data >> 16) & 0xFFFF) == 1 ? "MOUSE4" : "MOUSE5";
                            if (OnInput(name, down)) return (IntPtr)1;
                        }
                    }
                }
            }
            catch (Exception e) { Log.E("hook error: " + e); }
            return Native.CallNextHookEx(IntPtr.Zero, n, w, l);
        }

        void FocusLoop()
        {
            var sb = new StringBuilder(256);
            while (!stop)
            {
                try
                {
                    sb.Clear();
                    Native.GetWindowText(Native.GetForegroundWindow(), sb, 256);
                    bool ok = sb.ToString().StartsWith("Roblox");
                    if (!ok && focus && S.RobloxOnly)
                        foreach (var k in drags.Keys.ToList()) DragUp(k, true);
                    focus = ok;
                }
                catch { }
                Thread.Sleep(100);
            }
        }

        bool FocusOk() { return !S.RobloxOnly || focus; }

        bool OnInput(string name, bool down)
        {
            if (down && name == S.PanicKey) { Panic(); return false; }
            if (!down && swallowUp.Remove(name)) return true;
            var cap = Capture;
            if (down && cap != null)
            {
                Capture = null;
                swallowUp.Add(name);
                cap(name);
                return true;
            }
            if (name == S.UiKey) { if (down && ToggleUi != null) ToggleUi(); return true; }
            Watch(name, down);
            if (!down)
            {
                string f;
                if (held.TryRemove(name, out f)) { Release(f); return true; }
                return false;
            }
            if (held.ContainsKey(name)) return true;
            string target;
            if (!bindMap.TryGetValue(name, out target)) return false;
            if (!Running || !FocusOk())
            {
                Log.E(name + " pressed but ignored (running=" + Running + ", roblox focused=" + focus + ")");
                return false;
            }
            held[name] = target;
            Log.E("fire " + target + " (" + name + ")");
            try { Press(target); }
            catch (Exception e)
            {
                string dummy; held.TryRemove(name, out dummy);
                Log.E("press error: " + e);
                return false;
            }
            return true;
        }

        bool IsSlot(string name)
        {
            for (int i = 1; i <= 7; i++) if (S.Keys["slot" + i] == name) return true;
            return false;
        }

        void Watch(string name, bool down)
        {
            var g = S.Keys;
            if (down && (name == g["edit"] || IsSlot(name)))
                foreach (var kv in drags)
                    if (kv.Value.Held || kv.Value.Down) DragUp(kv.Key, true);
            var pf = S.Features["weapon_pullout"];
            if (!pf.Enabled || !Running) return;
            double now = clock.Elapsed.TotalSeconds, win = pf.Num["window"] / 1000.0;
            if (down && (name == g["wall"] || name == g["floor"] || name == g["ramp"] || name == g["pyramid"]))
            {
                inEdit = clickArmed = false;
                return;
            }
            if (name == g["edit"])
            {
                if (!down) { editDown = false; return; }
                if (editDown) return;
                editDown = true;
                if (!FocusOk()) return;
                if (inEdit && now - editT < win)
                {
                    inEdit = clickArmed = false;
                    if (pf.Str["trigger"] != "Click release") Run(Pullout);
                }
                else { inEdit = true; editT = now; }
            }
            else if (name == "MOUSE1")
            {
                if (down) clickArmed = inEdit && now - editT < win;
                else if (clickArmed)
                {
                    clickArmed = false;
                    if (inEdit && pf.Str["trigger"] != "Edit key" && FocusOk()) { inEdit = false; Run(Pullout); }
                }
            }
        }

        // ---------------------------------------------------------------- dispatch
        void Run(Action a)
        {
            var t = new Thread(() =>
            {
                try { a(); }
                catch (Exception e) { Log.E("macro error: " + e); }
            })
            { IsBackground = true };
            t.Start();
        }

        DragState Drag(string key) { return drags.GetOrAdd(key, _ => new DragState()); }

        static string FidOf(string target)
        {
            int ix = target.IndexOf(':');
            return ix >= 0 ? target.Substring(0, ix) : target;
        }

        void Press(string target)
        {
            string fid = FidOf(target);
            string arg = target.Length > fid.Length ? target.Substring(fid.Length + 1) : null;
            var f = S.Features[fid];
            var spec = Specs.ById[fid];
            switch (spec.Kind)
            {
                case "autobuild":
                    var st = Drag(target);
                    if (f.Str["mode"] == "Toggle" && st.Held) DragUp(target, false);
                    else { st.Held = true; Run(() => DragDown(target, f, arg)); }
                    break;
                case "fbdrag":
                    var fs = Drag(target);
                    if (fs.Held || fs.Down) break;
                    fs.Held = true;
                    Run(() => FbDragDown(target, f));
                    break;
                case "pickup":
                    CancellationTokenSource oldp;
                    if (loops.TryRemove(target, out oldp)) { oldp.Cancel(); if (f.Str["mode"] == "Toggle") break; }
                    var ctp = new CancellationTokenSource();
                    loops[target] = ctp;
                    Run(() => PickupLoop(ctp.Token, f));
                    break;
                case "instabuild":
                    if (f.Str["mode"] == "Once")
                    {
                        if (busy.TryAdd(target, 0))
                            Run(() =>
                            {
                                try { InstantBuild(f, arg, CancellationToken.None, true); }
                                finally { byte b; busy.TryRemove(target, out b); }
                            });
                        break;
                    }
                    CancellationTokenSource oldi;
                    if (loops.TryRemove(target, out oldi)) { oldi.Cancel(); if (f.Str["mode"] == "Toggle") break; }
                    var cti = new CancellationTokenSource();
                    loops[target] = cti;
                    Run(() => InstantBuild(f, arg, cti.Token, false));
                    break;
                case "editreset":
                    CancellationTokenSource oldr;
                    if (loops.TryRemove(target, out oldr)) { oldr.Cancel(); if (f.Str["mode"] == "Toggle") break; }
                    var ctr = new CancellationTokenSource();
                    loops[target] = ctr;
                    Run(() => EditResetLoop(ctr.Token, f));
                    break;
                default:    // edit / double / dragedit / single / fbdouble / rapid
                    if (f.Str["mode"] == "Once")
                    {
                        if (busy.TryAdd(fid, 0)) Run(() => OneShot(fid, f));
                        break;
                    }
                    CancellationTokenSource olde;
                    if (loops.TryRemove(target, out olde)) { olde.Cancel(); if (f.Str["mode"] == "Toggle") break; }
                    var cte = new CancellationTokenSource();
                    loops[target] = cte;
                    Run(() => RepeatLoop(fid, f, cte.Token));
                    break;
            }
        }

        void Release(string target)
        {
            string fid = FidOf(target);
            var f = S.Features[fid];
            string kind = Specs.ById[fid].Kind;
            if (kind == "fbdrag") { FbDragUp(target, f); return; }
            if (f.Str["mode"] != "Hold") return;
            if (kind == "autobuild") DragUp(target, false);
            else
            {
                CancellationTokenSource c;
                if (loops.TryRemove(target, out c)) c.Cancel();
            }
        }

        // ---------------------------------------------------------------- primitives
        void Tap(string name, int hold = -1)
        {
            var d = Input.Inp(name);
            if (!d.HasValue) return;
            Input.Send(d);
            Input.SleepMs(hold < 0 ? S.TapMs : hold);
            Input.Send(Input.Inp(name, true));
        }

        void Click(int hold)
        {
            Input.Send(Input.Inp("MOUSE1"));
            Input.SleepMs(Math.Max(1, hold));
            Input.Send(Input.Inp("MOUSE1", true));
        }

        void Sweep(int dx, int dy, int steps, int delay)
        {
            steps = Math.Max(1, steps);
            int px = 0, py = 0;
            for (int i = 1; i <= steps; i++)
            {
                int nx = (int)Math.Round(dx * (double)i / steps), ny = (int)Math.Round(dy * (double)i / steps);
                Input.Send(Input.Move(nx - px, ny - py));
                px = nx; py = ny;
                if (i < steps) Input.SleepMs(delay);
            }
        }

        // ---- FPS-aware minimums. Roblox reads input once per frame, so holds shorter than a frame can be missed.
        static int FrameMs(int fps, double frames)
        {
            return (int)Math.Ceiling(1000.0 / Math.Max(30, fps) * frames);
        }

        // global FPS (Settings > Performance), only when "Enforce minimum timings" is on
        int Fl(int v, double frames)
        {
            if (!S.FpsFloor) return v;
            return Math.Max(v, FrameMs(S.Fps, frames));
        }

        // per-macro FPS slider: if the macro has its own "fps" slider it ALWAYS uses it, otherwise falls back to Fl
        int F(FeatureCfg f, int v, double frames)
        {
            int fps;
            if (f != null && f.Num.TryGetValue("fps", out fps) && fps > 0) return Math.Max(v, FrameMs(fps, frames));
            return Fl(v, frames);
        }

        void TapE(string name) { Tap(name, Fl(S.TapMs, 1.5)); }
        void ClickE(int hold) { Click(Fl(hold, 2)); }
        void TapF(FeatureCfg f, string name) { Tap(name, F(f, S.TapMs, 1.5)); }
        void ClickF(FeatureCfg f, int hold) { Click(F(f, hold, 2)); }

        static List<int[]> Tour(string mask, int sx, int sy)
        {
            var rem = new List<int[]>();
            for (int i = 0; i < 9 && i < mask.Length; i++)
                if (mask[i] == '1') rem.Add(new[] { i % 3 - 1, i / 3 - 1 });
            var path = new List<int[]>();
            int cx = sx, cy = sy;
            while (rem.Count > 0)
            {
                int best = 0, bd = int.MaxValue;
                for (int k = 0; k < rem.Count; k++)
                {
                    int d = Math.Abs(rem[k][0] - cx) + Math.Abs(rem[k][1] - cy);
                    if (d < bd) { bd = d; best = k; }
                }
                path.Add(rem[best]);
                cx = rem[best][0]; cy = rem[best][1];
                rem.RemoveAt(best);
            }
            return path;
        }

        void ResetAction(FeatureCfg f = null)
        {
            if (S.ResetMethod == "Scroll Down")
            {
                int n = Math.Max(1, S.ScrollMs / 25);
                for (int i = 0; i < n; i++)
                {
                    if (i > 0) Input.SleepMs(25);
                    Input.Send(Input.Wheel(-1));
                }
            }
            else Tap("MOUSE2", F(f, S.TapMs, 1.5));
        }

        void ResetSeq(int step, FeatureCfg f = null)
        {
            TapF(f, S.Keys["edit"]);
            Input.SleepMs(step);
            ResetAction(f);
            Input.SleepMs(step);
            TapF(f, S.Keys["edit"]);
        }

        void Pullout()
        {
            var f = S.Features["weapon_pullout"];
            if (!f.Enabled) return;
            Input.SleepMs(f.Num["equip_delay"]);
            string slot = S.Keys["slot" + f.Str["slot"][f.Str["slot"].Length - 1]];
            bool fire = f.Num["fire"] == 1;
            var dn = new List<Native.INPUT?> { Input.Inp(slot) };
            var up = new List<Native.INPUT?> { Input.Inp(slot, true) };
            if (fire && f.Delay == 0) { dn.Add(Input.Inp("MOUSE1")); up.Add(Input.Inp("MOUSE1", true)); }
            Input.Send(dn.ToArray());
            Input.SleepMs(S.TapMs);
            Input.Send(up.ToArray());
            if (fire && f.Delay > 0) { Input.SleepMs(f.Delay); Tap("MOUSE1"); }
        }

        // switch to the Weapon Pullout slot (even if Weapon Pullout itself is turned off)
        void EquipWeapon(FeatureCfg f)
        {
            string s = S.Features["weapon_pullout"].Str["slot"];
            Tap(S.Keys["slot" + s[s.Length - 1]], F(f, 1, 1.5));
        }

        // ---------------------------------------------------------------- macros
        void RunCycle(string fid, FeatureCfg f)
        {
            switch (Specs.ById[fid].Kind)
            {
                case "edit": RunEdit(f); break;
                case "double": RunDouble(f); break;
                case "dragedit": RunDragEdit(f); break;
                case "single": RunSingle(f); break;
                case "fbdouble": RunFbDouble(f); break;
                case "rapid": RunRapid(f); break;
            }
        }

        void OneShot(string fid, FeatureCfg f)
        {
            try
            {
                inEdit = clickArmed = false;
                RunCycle(fid, f);
                Pullout();
            }
            finally { byte b; busy.TryRemove(fid, out b); }
        }

        void RepeatLoop(string fid, FeatureCfg f, CancellationToken ct)
        {
            double gapFrames = Specs.ById[fid].Kind == "rapid" ? 1 : 2;   // Rapid Edit only waits 1 frame between edits
            try
            {
                do
                {
                    inEdit = clickArmed = false;
                    RunCycle(fid, f);
                    if (ct.IsCancellationRequested) break;
                    Input.SleepMs(F(f, f.Num["repeat_gap"], gapFrames));
                } while (!ct.IsCancellationRequested);
            }
            finally { Pullout(); }
        }

        void RunEdit(FeatureCfg f)
        {
            int d = Fl(f.Delay, 2);
            TapE(S.Keys["edit"]);
            Input.SleepMs(d);
            ClickE(f.Num["click_hold"]);
            if (f.Num["confirm_edit"] == 1) { Input.SleepMs(d); TapE(S.Keys["edit"]); }
            if (f.Num["auto_reset"] == 1) { Input.SleepMs(Fl(f.Num["reset_delay"], 2)); ResetSeq(Fl(f.Num["reset_step"], 2)); }
        }

        void RunDouble(FeatureCfg f)
        {
            int d = Fl(f.Delay, 2);
            TapE(S.Keys["edit"]);
            Input.SleepMs(d);
            ClickE(f.Num["click_hold"]);
            Input.SleepMs(Fl(f.Num["gap"], 3));
            TapE(S.Keys["edit"]);
            Input.SleepMs(d);
            ClickE(f.Num["click_hold"]);
            if (f.Num["auto_reset"] == 1) { Input.SleepMs(Fl(f.Num["reset_delay"], 2)); ResetSeq(Fl(f.Num["reset_step"], 2)); }
        }

        void RunSingle(FeatureCfg f)
        {
            string edit = S.Keys["edit"], confirm = f.Str["confirm"];
            var st = Specs.CellXY[f.Str["start"]];
            var path = Tour(f.Str["tiles"], st[0], st[1]);
            if (path.Count == 0) return;
            int dist = f.Num["dist"], steps = f.Num["steps"], sd = Fl(f.Num["step_delay"], 1);
            int dwell = Fl(f.Num["tile_dwell"], 1.5), rel = Fl(f.Num["release_delay"], 2);
            int cx = st[0], cy = st[1], tx = 0, ty = 0;
            bool hold = confirm == "Hold Edit";
            if (hold) Input.Send(Input.Inp(edit)); else TapE(edit);
            try
            {
                Input.SleepMs(Fl(f.Delay, 2));
                foreach (var c in path)
                {
                    int dx = (c[0] - cx) * dist, dy = (c[1] - cy) * dist;
                    if (dx != 0 || dy != 0) Sweep(dx, dy, steps, sd);
                    tx += dx; ty += dy; cx = c[0]; cy = c[1];
                    Input.SleepMs(dwell);
                    ClickE(f.Num["click_hold"]);
                }
                Input.SleepMs(rel);
                if (confirm == "Tap Edit") TapE(edit);
            }
            finally { if (hold) Input.Send(Input.Inp(edit, true)); }
            if (f.Num["return_aim"] == 1 && (tx != 0 || ty != 0))
            {
                Input.SleepMs(rel);
                Sweep(-tx, -ty, steps, sd);
            }
        }

        void RunDragEdit(FeatureCfg f)
        {
            string edit = S.Keys["edit"], confirm = f.Str["confirm"];
            var st = Specs.CellXY[f.Str["start"]];
            var path = Tour(f.Str["tiles"], st[0], st[1]);
            if (path.Count == 0) return;
            int dist = f.Num["dist"], steps = f.Num["steps"], sd = Fl(f.Num["step_delay"], 1);
            int dwell = Fl(f.Num["tile_dwell"], 1.5), rel = Fl(f.Num["release_delay"], 2);
            int cx = st[0], cy = st[1], tx = 0, ty = 0;
            bool hold = confirm == "Hold Edit";
            if (hold) Input.Send(Input.Inp(edit)); else TapE(edit);
            try
            {
                Input.SleepMs(Fl(f.Delay, 2));
                Input.Send(Input.Inp("MOUSE1"));
                try
                {
                    foreach (var c in path)
                    {
                        int dx = (c[0] - cx) * dist, dy = (c[1] - cy) * dist;
                        if (dx != 0 || dy != 0) Sweep(dx, dy, steps, sd);
                        tx += dx; ty += dy; cx = c[0]; cy = c[1];
                        Input.SleepMs(dwell);
                    }
                    Input.SleepMs(rel);
                }
                finally { Input.Send(Input.Inp("MOUSE1", true)); }
                if (confirm == "Tap Edit") { Input.SleepMs(rel); TapE(edit); }
                else if (hold) Input.SleepMs(rel);
            }
            finally { if (hold) Input.Send(Input.Inp(edit, true)); }
            if (f.Num["return_aim"] == 1 && (tx != 0 || ty != 0))
            {
                Input.SleepMs(rel);
                Sweep(-tx, -ty, steps, sd);
            }
        }

        // ---------------------------------------------------------------- Framebreak
        // Drag: press = (open edit) + hold left click, you move the mouse over the tiles, let go = release (+ confirm) (+ auto reset)
        void FbDragDown(string key, FeatureCfg f)
        {
            var st = Drag(key);
            inEdit = clickArmed = false;
            if (f.Num["open_edit"] == 1)
            {
                TapF(f, S.Keys["edit"]);
                Input.SleepMs(F(f, f.Delay, 2));
            }
            lock (st.Lock)
            {
                if (st.Down) return;
                if (st.Held)
                {
                    Input.Send(Input.Inp("MOUSE1"));
                    st.Down = true;
                    st.T = Stopwatch.GetTimestamp();
                    return;
                }
            }
            ClickF(f, MinClick);
            FbConfirm(f);
        }

        void FbDragUp(string key, FeatureCfg f)
        {
            var st = Drag(key);
            st.Held = false;
            Run(() =>
            {
                lock (st.Lock)
                {
                    if (!st.Down) return;
                    double wait = F(f, MinClick, 2) - (Stopwatch.GetTimestamp() - st.T) * 1000.0 / Stopwatch.Frequency;
                    if (wait > 0) Input.SleepMs(wait);
                    Input.Send(Input.Inp("MOUSE1", true));
                    st.Down = false;
                }
                FbConfirm(f);
            });
        }

        void FbConfirm(FeatureCfg f)
        {
            if (f.Str["confirm"] == "Tap Edit")
            {
                Input.SleepMs(F(f, f.Num["release_delay"], 2));
                TapF(f, S.Keys["edit"]);
            }
            if (f.Num.ContainsKey("auto_reset") && f.Num["auto_reset"] == 1)
            {
                Input.SleepMs(F(f, f.Num["reset_delay"], 2));
                ResetSeq(F(f, f.Num["reset_step"], 2), f);
            }
            Pullout();
        }

        void RunFbDouble(FeatureCfg f)
        {
            string edit = S.Keys["edit"];
            int d = Fl(f.Delay, 2);
            bool tapConfirm = f.Str["confirm"] == "Tap Edit";
            for (int i = 0; i < 2; i++)
            {
                if (i == 1) Input.SleepMs(Fl(f.Num["gap"], 3));
                TapE(edit);
                Input.SleepMs(d);
                ClickE(f.Num["click_hold"]);
                if (tapConfirm) { Input.SleepMs(d); TapE(edit); }
            }
        }

        // Rapid Edit: one cycle = edit, click the tile under the crosshair, (confirm). Every step is 1 frame of the FPS slider.
        void RunRapid(FeatureCfg f)
        {
            string edit = S.Keys["edit"];
            int step = F(f, f.Delay, 1);
            Tap(edit, F(f, 1, 1));
            Input.SleepMs(step);
            Click(F(f, f.Num["click_hold"], 1));
            if (f.Str["confirm"] == "Tap Edit")
            {
                Input.SleepMs(step);
                Tap(edit, F(f, 1, 1));
            }
        }

        // Instant Build: equip the piece and place it with no click from you. Hold / Toggle keeps placing (Turbo Building must be OFF).
        void InstantBuild(FeatureCfg f, string piece, CancellationToken ct, bool once)
        {
            inEdit = clickArmed = false;
            string place = f.Str["place"] == "Left Click" ? "MOUSE1" : "MOUSE2";
            Tap(S.Keys[piece], F(f, 1, 1));
            Input.SleepMs(F(f, f.Delay, 1));
            do
            {
                Tap(place, F(f, f.Num["click_hold"], 1));
                if (once || ct.IsCancellationRequested) break;
                Input.SleepMs(F(f, f.Num["place_gap"], 1));
            } while (!ct.IsCancellationRequested);
            if (f.Num["swap_back"] == 1)
            {
                Input.SleepMs(F(f, 1, 2));
                EquipWeapon(f);
            }
        }

        // Edit Reset: scroll-wheel ticks for as long as the trigger is held (or toggled on)
        void EditResetLoop(CancellationToken ct, FeatureCfg f)
        {
            int dir = f.Str["direction"] == "Scroll Up" ? 1 : -1;
            if (f.Num["open_edit"] == 1)
            {
                Tap(S.Keys["edit"], F(f, 1, 1.5));
                Input.SleepMs(F(f, f.Delay, 2));
            }
            while (!ct.IsCancellationRequested)
            {
                Input.Send(Input.Wheel(dir));
                Input.SleepMs(F(f, f.Num["tick_gap"], 1));
            }
        }

        void PickupLoop(CancellationToken ct, FeatureCfg f)
        {
            while (!ct.IsCancellationRequested)
            {
                Tap(S.Keys["pickup"], f.Num["hold"]);
                Input.SleepMs(f.Delay);
            }
        }

        void DragDown(string key, FeatureCfg f, string piece)
        {
            var st = Drag(key);
            inEdit = clickArmed = false;
            Tap(S.Keys[piece]);
            Input.SleepMs(f.Delay);
            lock (st.Lock)
            {
                if (st.Down) return;
                if (!st.Held)
                {
                    if (f.Str["mode"] == "Hold")
                    {
                        Input.Send(Input.Inp("MOUSE1"));
                        Input.SleepMs(MinClick);
                        Input.Send(Input.Inp("MOUSE1", true));
                    }
                    return;
                }
                Input.Send(Input.Inp("MOUSE1"));
                st.Down = true;
                st.T = Stopwatch.GetTimestamp();
            }
            if (f.Num["repress"] == 1)
            {
                Input.SleepMs(18);
                lock (st.Lock)
                {
                    if (st.Held && st.Down)
                    {
                        Input.Send(Input.Inp("MOUSE1", true));
                        Input.SleepMs(2);
                        Input.Send(Input.Inp("MOUSE1"));
                        st.T = Stopwatch.GetTimestamp();
                    }
                }
            }
        }

        void DragUp(string key, bool now)
        {
            var st = Drag(key);
            st.Held = false;
            if (!st.Down) return;
            Run(() =>
            {
                double wait = MinClick - (Stopwatch.GetTimestamp() - st.T) * 1000.0 / Stopwatch.Frequency;
                if (!now && wait > 0) Input.SleepMs(wait);
                ReleaseMouse(key);
            });
        }

        void ReleaseMouse(string key)
        {
            var st = Drag(key);
            lock (st.Lock)
            {
                if (st.Down) { Input.Send(Input.Inp("MOUSE1", true)); st.Down = false; }
            }
        }

        public void StopAll()
        {
            Running = false;
            foreach (var kv in loops) kv.Value.Cancel();
            loops.Clear();
            foreach (var k in drags.Keys.ToList()) DragUp(k, true);
            Input.Send(Input.Inp("MOUSE1", true));
            held.Clear();
            inEdit = clickArmed = false;
        }

        /// <summary>Used by the on/off switches: stops one macro right away (loops end, held clicks are released).</summary>
        public void StopFeature(string fid)
        {
            foreach (var key in loops.Keys.ToList())
            {
                if (FidOf(key) != fid) continue;
                CancellationTokenSource c;
                if (loops.TryRemove(key, out c)) c.Cancel();
            }
            foreach (var key in drags.Keys.ToList())
                if (FidOf(key) == fid) DragUp(key, true);
        }

        public void ReleaseBuilds() { StopFeature("auto_build"); }

        void Panic()
        {
            Run(() => { StopAll(); if (PanicEvt != null) PanicEvt(); });
        }
    }
}
