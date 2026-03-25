using System;
using UnityEngine;

namespace Timberbot
{
    // file-based error logging. fresh per session, timestamped, thread-safe.
    // log file: Documents/Timberborn/Mods/Timberbot/timberbot.log
    static class TimberbotLog
    {
        private static string _logPath;
        private static readonly object _lock = new object();

        public static void Init(string modDir)
        {
            _logPath = System.IO.Path.Combine(modDir, "timberbot.log");
            try { System.IO.File.WriteAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Timberbot session started\n"); }
            catch { }
        }

        public static void Error(string context, Exception ex)
        {
            var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR {context}: {ex.GetType().Name}: {ex.Message}";
            Debug.LogWarning($"[Timberbot] {context}: {ex.GetType().Name}: {ex.Message}");
            Append(msg);
        }

        public static void Info(string msg)
        {
            Debug.Log($"[Timberbot] {msg}");
            Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}");
        }

        private static void Append(string line)
        {
            if (_logPath == null) return;
            lock (_lock)
            {
                try { System.IO.File.AppendAllText(_logPath, line + "\n"); }
                catch { }
            }
        }
    }
}
