using System;
using UnityEngine;

namespace Timberbot
{
    // File-based logging for the mod. Fresh log file each session (overwritten on Init).
    //
    // Dual output: writes to both Unity's console (Debug.Log/LogWarning, visible in
    // BepInEx console and Player.log) AND a dedicated timberbot.log file for easy
    // access without digging through Unity's logs.
    //
    // Thread-safe: lock protects file writes because PushEvent and HTTP responses
    // can trigger logging from background threads.
    //
    // Log file: Documents/Timberborn/Mods/Timberbot/timberbot.log
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
            Debug.LogWarning($"[Timberbot] {context}: {ex.GetType().Name}: {ex.Message}");
            Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR {context}: {ex}");
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
