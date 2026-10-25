using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace Shush
{
    /// <summary>Non-blocking log to config/shush.log (errors always, inputs when Debug is on).</summary>
    internal static class Log
    {
        static readonly BlockingCollection<string> Q = new BlockingCollection<string>();
        static readonly object Lk = new object();
        static bool started;
        static int ec, dc;

        static void Put(string s)
        {
            lock (Lk)
            {
                if (!started)
                {
                    started = true;
                    var t = new Thread(Writer) { IsBackground = true };
                    t.Start();
                }
            }
            Q.Add(s);
        }

        static void Writer()
        {
            foreach (var s in Q.GetConsumingEnumerable())
            {
                try
                {
                    Directory.CreateDirectory(Store.Dir);
                    File.AppendAllText(Path.Combine(Store.Dir, "shush.log"), s + Environment.NewLine);
                }
                catch { }
            }
        }

        public static void E(string m) { if (Interlocked.Increment(ref ec) <= 300) Put(DateTime.Now.ToString("HH:mm:ss  ") + m); }
        public static void D(string m) { if (Interlocked.Increment(ref dc) <= 4000) Put(DateTime.Now.ToString("HH:mm:ss.fff  ") + m); }
    }
}
