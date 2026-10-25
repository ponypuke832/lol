using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Shush
{
    internal static class Native
    {
        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint tid);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")]
        public static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint code, uint type);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint ms);

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLL { public uint vk, scan, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)]
        public struct MSLL { public POINT pt; public uint data, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Explicit)]
        public struct IU { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public IU u; }
    }

    /// <summary>Key tables + SendInput helpers (scan-code input, same approach as the Python version).</summary>
    internal static class Input
    {
        public static readonly Dictionary<string, int> VK = new Dictionary<string, int>();
        public static readonly Dictionary<int, string> VkName = new Dictionary<int, string>();
        public static readonly Dictionary<string, uint[]> Mouse = new Dictionary<string, uint[]>
        {
            { "MOUSE1", new uint[] { 0x2, 0x4, 0 } }, { "MOUSE2", new uint[] { 0x8, 0x10, 0 } },
            { "MOUSE3", new uint[] { 0x20, 0x40, 0 } }, { "MOUSE4", new uint[] { 0x80, 0x100, 1 } },
            { "MOUSE5", new uint[] { 0x80, 0x100, 2 } },
        };
        static readonly HashSet<int> Ext = new HashSet<int> { 0x26, 0x28, 0x25, 0x27, 0x2E, 0x24, 0x23, 0x21, 0x22, 0xA3, 0xA5 };
        static readonly IntPtr Magic = new IntPtr(0x5348555348L);
        static Dictionary<int, string> scanName;
        public static volatile bool Debug;
        public static volatile int HookThreadId;
        public static readonly ConcurrentDictionary<int, long> Sending = new ConcurrentDictionary<int, long>();

        static Input()
        {
            for (char c = 'A'; c <= 'Z'; c++) VK[c.ToString()] = c;
            for (char c = '0'; c <= '9'; c++) VK[c.ToString()] = c;
            for (int i = 1; i <= 11; i++) VK["F" + i] = 0x6F + i;   // F12 = panic, reserved
            string[] n = { "SPACE", "TAB", "ENTER", "BACKSPACE", "CAPS", "LSHIFT", "RSHIFT", "LCTRL", "RCTRL", "LALT", "RALT",
                           "UP", "DOWN", "LEFT", "RIGHT", "DELETE", "HOME", "END", "PGUP", "PGDN",
                           ";", "=", ",", "-", ".", "/", "`", "[", "\\", "]", "'" };
            int[] v = { 0x20, 9, 13, 8, 0x14, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5,
                        0x26, 0x28, 0x25, 0x27, 0x2E, 0x24, 0x23, 0x21, 0x22,
                        0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, 0xDB, 0xDC, 0xDD, 0xDE };
            for (int i = 0; i < n.Length; i++) VK[n[i]] = v[i];
            foreach (var kv in VK) VkName[kv.Value] = kv.Key;
            VkName[0x1B] = "ESC"; VkName[0x7B] = "F12"; VkName[0x2D] = "INSERT";   // reserved, not bindable

            // NEW: many mouse drivers (Logitech G Hub, Razer Synapse, etc.) send side buttons as the keyboard
            // "Browser Back / Browser Forward" keys instead of XBUTTON. Treat those as Side Button 4 / 5 too.
            VkName[0xA6] = "MOUSE4";
            VkName[0xA7] = "MOUSE5";
        }

        public static Native.INPUT? Inp(string name, bool up = false)
        {
            uint[] m;
            if (name != null && Mouse.TryGetValue(name, out m))
            {
                var i = new Native.INPUT { type = 0 };
                i.u.mi = new Native.MOUSEINPUT { data = m[2], flags = up ? m[1] : m[0], extra = Magic };
                return i;
            }
            int vk;
            if (name == null || !VK.TryGetValue(name, out vk)) return null;
            var k = new Native.INPUT { type = 1 };
            k.u.ki = new Native.KEYBDINPUT
            {
                scan = (ushort)Native.MapVirtualKey((uint)vk, 0),
                flags = 0x8u | (up ? 0x2u : 0u) | (Ext.Contains(vk) ? 0x1u : 0u),
                extra = Magic
            };
            return k;
        }

        public static Native.INPUT? Move(int dx, int dy)
        {
            var i = new Native.INPUT { type = 0 };
            i.u.mi = new Native.MOUSEINPUT { dx = dx, dy = dy, flags = 0x1, extra = Magic };
            return i;
        }

        public static Native.INPUT? Wheel(int notches)
        {
            var i = new Native.INPUT { type = 0 };
            i.u.mi = new Native.MOUSEINPUT { data = unchecked((uint)(120 * notches)), flags = 0x800, extra = Magic };
            return i;
        }

        public static void Send(params Native.INPUT?[] items)
        {
            // SAFETY: never call SendInput from inside the hook callback.
            if (Environment.CurrentManagedThreadId == HookThreadId)
            {
                var copy = items;
                ThreadPool.UnsafeQueueUserWorkItem(_ => Send(copy), null);
                return;
            }
            var list = new List<Native.INPUT>();
            foreach (var i in items) if (i.HasValue) list.Add(i.Value);
            if (list.Count == 0) return;
            if (Debug)
            {
                var parts = new List<string>();
                foreach (var i in list) parts.Add(Describe(i));
                Log.D("SEND " + string.Join(" + ", parts));
            }
            int tid = Environment.CurrentManagedThreadId;
            Sending[tid] = Stopwatch.GetTimestamp();
            try { Native.SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf(typeof(Native.INPUT))); }
            finally { long z; Sending.TryRemove(tid, out z); }
        }

        static string Describe(Native.INPUT i)
        {
            if (i.type == 1)
            {
                if (scanName == null)
                {
                    scanName = new Dictionary<int, string>();
                    foreach (var kv in VK) scanName[(int)Native.MapVirtualKey((uint)kv.Value, 0)] = kv.Key;
                }
                string nm;
                if (!scanName.TryGetValue(i.u.ki.scan, out nm)) nm = "0x" + i.u.ki.scan.ToString("X");
                return nm + ((i.u.ki.flags & 2) != 0 ? " UP" : " DOWN");
            }
            uint f = i.u.mi.flags;
            if ((f & 1) != 0) return "MOUSE MOVE (" + i.u.mi.dx + "," + i.u.mi.dy + ")";
            if ((f & 0x800) != 0) return "WHEEL";
            foreach (var kv in Mouse)
                if ((f == kv.Value[0] || f == kv.Value[1]) && (kv.Value[2] == 0 || kv.Value[2] == i.u.mi.data))
                    return kv.Key + (f == kv.Value[0] ? " DOWN" : " UP");
            return "0x" + f.ToString("X");
        }

        public static void SleepMs(double ms)
        {
            if (ms <= 0) return;
            long end = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000.0);
            while (true)
            {
                long rem = end - Stopwatch.GetTimestamp();
                if (rem <= 0) return;
                double r = rem * 1000.0 / Stopwatch.Frequency;
                if (r > 2.0) Thread.Sleep((int)(r - 1.5)); else Thread.SpinWait(20);
            }
        }
    }
}
