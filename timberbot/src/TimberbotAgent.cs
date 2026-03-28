// TimberbotAgent.cs -- AI agent loop: spawns claude/codex/custom binary per cycle.
//
// Each decision cycle:
//   1. Runs `timberbot.py brain` to get game state in TOON format
//   2. Spawns binary (claude --print / codex / custom) with state on stdin
//   3. Parses response as timberbot.py commands (one per line)
//   4. Executes each command via `timberbot.py <cmd>`
//   5. Increments turn counter; stops after N turns
//
// Triggered via POST /api/agent/start, monitored via GET /api/agent/status,
// cancelled via POST /api/agent/stop.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Timberbot
{
    public enum AgentStatus
    {
        Idle,
        GatheringState,
        Thinking,
        Executing,
        Done,
        Error
    }

    class TurnRecord
    {
        public int Turn;
        public List<string> Commands = new List<string>();   // "ok: set_speed speed:1" or "FAIL: place_building ..."
        public int Ok;
        public int Failed;
        public double Seconds;
        public string Error;
    }

    public class TimberbotAgent
    {
        private string _binary;
        private string _model;
        private string _prompt;
        private string _goal;
        private int _totalTurns;
        private int _intervalSeconds;
        private int _processTimeoutSeconds;
        private string _timberbotCmd;

        private const string DEFAULT_GOAL = "survive and grow the colony. keep beavers fed and watered. expand housing and production as resources allow.";

        private int _currentTurn;
        private AgentStatus _status = AgentStatus.Idle;
        private string _lastResponse;
        private string _lastError;
        private string _currentCmd;  // command currently being executed
        private Thread _thread;
        private volatile bool _cancelRequested;
        private readonly List<TurnRecord> _history = new List<TurnRecord>();

        private readonly TimberbotJw _jw = new TimberbotJw(1024);
        private readonly TimberbotJw _statusJw = new TimberbotJw(4096);

        public string Start(string binary, int turns, string model, int interval, string prompt, int timeout, string goal)
        {
            if (_status != AgentStatus.Idle && _status != AgentStatus.Done && _status != AgentStatus.Error)
                return _jw.Error("agent_busy", ("status", _status.ToString().ToLowerInvariant()), ("turn", _currentTurn), ("totalTurns", _totalTurns));

            _binary = binary ?? "claude";
            _totalTurns = turns > 0 ? turns : 1;
            _model = model;
            _intervalSeconds = interval > 0 ? interval : 10;
            _processTimeoutSeconds = timeout > 0 ? timeout : 120;
            _goal = string.IsNullOrEmpty(goal) ? DEFAULT_GOAL : goal;
            _prompt = LoadPrompt(prompt);
            _timberbotCmd = ResolveTimberbotCommand();
            _currentTurn = 0;
            _lastResponse = null;
            _lastError = null;
            _currentCmd = null;
            _cancelRequested = false;
            _history.Clear();
            _status = AgentStatus.GatheringState;

            _thread = new Thread(AgentLoop) { IsBackground = true, Name = "Timberbot-Agent" };
            _thread.Start();

            TimberbotLog.Info($"agent.start binary={_binary} turns={_totalTurns} model={_model ?? "default"} interval={_intervalSeconds}s timeout={_processTimeoutSeconds}s");
            return _jw.Reset().OpenObj()
                .Prop("status", "started")
                .Prop("binary", _binary)
                .Prop("turns", _totalTurns)
                .Prop("interval", _intervalSeconds)
                .CloseObj().ToString();
        }

        public string Stop()
        {
            if (_status == AgentStatus.Idle || _status == AgentStatus.Done || _status == AgentStatus.Error)
                return _jw.Error("agent_not_running");

            _cancelRequested = true;
            TimberbotLog.Info("agent.stop requested");
            return _jw.Reset().OpenObj().Prop("status", "stopping").CloseObj().ToString();
        }

        public string Status()
        {
            var jw = _statusJw.Reset().OpenObj()
                .Prop("status", _status.ToString().ToLowerInvariant())
                .Prop("turn", _currentTurn)
                .Prop("totalTurns", _totalTurns)
                .Prop("binary", _binary ?? "")
                .Prop("model", _model ?? "")
                .Prop("goal", JsonEscape(_goal))
                .Prop("currentCmd", JsonEscape(_currentCmd))
                .Prop("lastError", JsonEscape(_lastError));

            // turn history array
            jw.Arr("history");
            // show last 10 turns
            int start = _history.Count > 10 ? _history.Count - 10 : 0;
            for (int i = start; i < _history.Count; i++)
            {
                var rec = _history[i];
                jw.OpenObj()
                    .Prop("turn", rec.Turn)
                    .Prop("ok", rec.Ok)
                    .Prop("failed", rec.Failed)
                    .Prop("seconds", (float)rec.Seconds, "F1");
                jw.Arr("commands");
                foreach (var cmd in rec.Commands)
                    jw.Str(JsonEscape(cmd));
                jw.CloseArr();
                if (rec.Error != null)
                    jw.Prop("error", JsonEscape(rec.Error));
                jw.CloseObj();
            }
            jw.CloseArr();

            return jw.CloseObj().ToString();
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // truncate to avoid huge status responses
            if (s.Length > 2000) s = s.Substring(0, 2000) + "...(truncated)";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        private void AgentLoop()
        {
            try
            {
                for (int turn = 1; turn <= _totalTurns; turn++)
                {
                    if (_cancelRequested) break;

                    _currentTurn = turn;
                    _currentCmd = null;
                    TimberbotLog.Info($"agent.cycle turn={turn}/{_totalTurns}");
                    var turnStart = System.Diagnostics.Stopwatch.StartNew();
                    var rec = new TurnRecord { Turn = turn };

                    // 1. Get brain state (TOON format) -- set goal on first turn
                    _status = AgentStatus.GatheringState;
                    var brainArgs = turn == 1 ? "brain \"goal:" + _goal.Replace("\"", "'") + "\"" : "brain";
                    _currentCmd = brainArgs;
                    var (brainOk, brainOut) = RunProcess(_timberbotCmd, brainArgs, null, _processTimeoutSeconds);
                    if (!brainOk)
                    {
                        _lastError = "brain failed: " + brainOut;
                        rec.Error = _lastError;
                        rec.Seconds = turnStart.Elapsed.TotalSeconds;
                        _history.Add(rec);
                        TimberbotLog.Info($"ERROR agent.brain.fail turn={turn}: {brainOut}");
                        _status = AgentStatus.Error;
                        return;
                    }
                    TimberbotLog.Info($"agent.brain.ok turn={turn} bytes={brainOut.Length}");

                    if (_cancelRequested) break;

                    // 2. Spawn binary with state on stdin
                    _status = AgentStatus.Thinking;
                    _currentCmd = "thinking...";
                    var message = "GOAL: " + _goal + "\n\nTURN: " + turn + "/" + _totalTurns +
                        "\n\nCurrent game state:\n\n" + brainOut +
                        "\n\nRespond ONLY with timberbot.py commands, one per line. NO prose, NO explanations, NO markdown.\n" +
                        "Example: place_building prefab:LumberjackFlag.IronTeeth x:120 y:130 z:2\n" +
                        "Example: set_speed speed:2\n" +
                        "Example: mark_trees x1:100 y1:100 x2:110 y2:110 z:2\n" +
                        "If no action needed, respond with: NONE";

                    var args = BuildBinaryArgs(_binary, _model, _prompt);
                    var (thinkOk, response) = RunProcess(_binary, args, message, _processTimeoutSeconds);
                    if (!thinkOk)
                    {
                        _lastError = "binary failed: " + response;
                        rec.Error = _lastError;
                        rec.Seconds = turnStart.Elapsed.TotalSeconds;
                        _history.Add(rec);
                        TimberbotLog.Info($"ERROR agent.think.fail turn={turn}: {response}");
                        _lastResponse = response;
                        if (turn < _totalTurns && !_cancelRequested)
                            Thread.Sleep(_intervalSeconds * 1000);
                        continue;
                    }
                    _lastResponse = response;
                    TimberbotLog.Info($"agent.think.ok turn={turn} bytes={response.Length}");

                    if (_cancelRequested) break;

                    // 3. Parse + execute commands
                    _status = AgentStatus.Executing;
                    var lines = response.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var rawLine in lines)
                    {
                        if (_cancelRequested) break;

                        var line = rawLine.Trim();
                        if (string.IsNullOrEmpty(line)) continue;
                        if (line.StartsWith("#") || line.StartsWith("//")) continue;
                        if (line.Equals("NONE", StringComparison.OrdinalIgnoreCase)) continue;

                        var cmd = line;
                        if (cmd.StartsWith("timberbot.py "))
                            cmd = cmd.Substring("timberbot.py ".Length);

                        _currentCmd = cmd;
                        TimberbotLog.Info($"agent.exec turn={turn} cmd={cmd}");
                        var (execOk, execOut) = RunProcess(_timberbotCmd, cmd, null, _processTimeoutSeconds);
                        if (execOk)
                        {
                            rec.Commands.Add("ok: " + cmd);
                            rec.Ok++;
                            TimberbotLog.Info($"agent.exec.ok turn={turn} cmd={cmd}");
                        }
                        else
                        {
                            rec.Commands.Add("FAIL: " + cmd);
                            rec.Failed++;
                            TimberbotLog.Info($"ERROR agent.exec.fail turn={turn} cmd={cmd}: {execOut}");
                        }
                    }
                    rec.Seconds = turnStart.Elapsed.TotalSeconds;
                    _history.Add(rec);
                    _currentCmd = null;
                    TimberbotLog.Info($"agent.cycle.done turn={turn} ok={rec.Ok} fail={rec.Failed} {rec.Seconds:F1}s");

                    // delay between cycles (skip after last turn)
                    if (turn < _totalTurns && !_cancelRequested)
                        Thread.Sleep(_intervalSeconds * 1000);
                }

                _status = _cancelRequested ? AgentStatus.Idle : AgentStatus.Done;
                TimberbotLog.Info($"agent.done turns={_currentTurn}/{_totalTurns} cancelled={_cancelRequested}");
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _status = AgentStatus.Error;
                TimberbotLog.Error("agent.loop", ex);
            }
        }

        private static string BuildBinaryArgs(string binary, string model, string prompt)
        {
            // claude: --print mode with optional model and system prompt
            if (binary == "claude" || binary.EndsWith("/claude") || binary.EndsWith("\\claude") ||
                binary.EndsWith("/claude.exe") || binary.EndsWith("\\claude.exe"))
            {
                var sb = new StringBuilder("--print --output-format text --dangerously-skip-permissions");
                if (!string.IsNullOrEmpty(model))
                    sb.Append(" --model ").Append(model);
                if (!string.IsNullOrEmpty(prompt))
                    sb.Append(" --system-prompt \"").Append(prompt.Replace("\"", "\\\"")).Append("\"");
                return sb.ToString();
            }

            // codex: similar flags (adjust when codex CLI is finalized)
            if (binary == "codex" || binary.EndsWith("/codex") || binary.EndsWith("\\codex") ||
                binary.EndsWith("/codex.exe") || binary.EndsWith("\\codex.exe"))
            {
                var sb = new StringBuilder("--print --output-format text --dangerously-skip-permissions");
                if (!string.IsNullOrEmpty(model))
                    sb.Append(" --model ").Append(model);
                if (!string.IsNullOrEmpty(prompt))
                    sb.Append(" --system-prompt \"").Append(prompt.Replace("\"", "\\\"")).Append("\"");
                return sb.ToString();
            }

            // custom binary: no args, stdin/stdout only
            return "";
        }

        private static (bool ok, string output) RunProcess(string binary, string args, string stdin, int timeoutSeconds)
        {
            try
            {
                // On Windows, .py files need python.exe to run them.
                // .exe and known CLI tools (claude, codex) invoke directly.
                string fileName;
                string finalArgs;
                if (binary.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                {
                    fileName = "python.exe";
                    finalArgs = $"\"{binary}\" {args ?? ""}";
                }
                else
                {
                    fileName = binary;
                    finalArgs = args ?? "";
                }

                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = finalArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = stdin != null,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                // remove CLAUDECODE env var to prevent interference (like endless)
                psi.Environment.Remove("CLAUDECODE");

                var proc = new Process { StartInfo = psi };
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                if (stdin != null)
                {
                    proc.StandardInput.Write(stdin);
                    proc.StandardInput.Close();
                }

                if (!proc.WaitForExit(timeoutSeconds * 1000))
                {
                    try { proc.Kill(); } catch { }
                    return (false, "timeout after " + timeoutSeconds + "s");
                }

                if (proc.ExitCode != 0)
                    return (false, "exit=" + proc.ExitCode + " stderr=" + stderr.ToString().Trim());

                return (true, stdout.ToString().Trim());
            }
            catch (Exception ex)
            {
                return (false, "spawn failed: " + ex.Message);
            }
        }

        /// Find timberbot.py and return the command to invoke it.
        /// Searches: mod folder, repo paths, then falls back to PATH via cmd /c.
        private static string ResolveTimberbotCommand()
        {
            // check known locations for timberbot.py
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Timberborn", "Mods", "Timberbot", "timberbot.py"),
                @"C:\code\timberborn\timberbot\script\timberbot.py",
            };
            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    TimberbotLog.Info($"agent.resolve timberbot.py found at {path}");
                    return path;
                }
            }
            // fallback: assume it's on PATH
            TimberbotLog.Info("agent.resolve timberbot.py not found, using PATH fallback");
            return "timberbot.py";
        }

        private static string LoadPrompt(string overridePrompt)
        {
            if (!string.IsNullOrEmpty(overridePrompt))
                return overridePrompt;

            // search for the timberbot gameplay skill (full game reference)
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidates = new[]
            {
                // primary: the timberbot gameplay skill
                Path.Combine(userProfile, ".claude", "skills", "timberbot", "SKILL.md"),
                // fallback: repo docs
                @"C:\code\timberborn\docs\timberbot.md",
                // fallback: agent-prompt.md in mod folder
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Timberborn", "Mods", "Timberbot", "agent-prompt.md"),
            };
            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        var content = File.ReadAllText(path);
                        TimberbotLog.Info($"agent.prompt loaded from {path} ({content.Length} bytes)");
                        return content;
                    }
                    catch { }
                }
            }

            // fallback minimal prompt
            TimberbotLog.Info("agent.prompt using fallback (no skill file found)");
            return "You are playing Timberborn via the timberbot.py CLI. " +
                "Respond with timberbot.py commands, one per line. " +
                "Available commands: summary, buildings, beavers, trees, crops, prefabs, " +
                "place_building, demolish_building, place_path, set_speed, mark_trees, " +
                "plant_crop, set_priority, set_workers, unlock_building, set_floodgate. " +
                "Use key:value syntax for arguments. If no action needed, respond NONE.";
        }
    }
}
