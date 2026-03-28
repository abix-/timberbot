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

    public class TimberbotAgent
    {
        private string _binary;
        private string _model;
        private string _prompt;
        private int _totalTurns;
        private int _intervalSeconds;
        private int _processTimeoutSeconds;
        private string _timberbotCmd;  // resolved path to run timberbot.py

        private int _currentTurn;
        private AgentStatus _status = AgentStatus.Idle;
        private string _lastResponse;
        private string _lastError;
        private Thread _thread;
        private volatile bool _cancelRequested;

        // separate JW instances: _jw for main-thread POST calls (Start/Stop),
        // _statusJw for listener-thread GET calls (Status) -- avoids race conditions
        private readonly TimberbotJw _jw = new TimberbotJw(1024);
        private readonly TimberbotJw _statusJw = new TimberbotJw(1024);

        public string Start(string binary, int turns, string model, int interval, string prompt, int timeout)
        {
            if (_status != AgentStatus.Idle && _status != AgentStatus.Done && _status != AgentStatus.Error)
                return _jw.Error("agent_busy", ("status", _status.ToString().ToLowerInvariant()), ("turn", _currentTurn), ("totalTurns", _totalTurns));

            _binary = binary ?? "claude";
            _totalTurns = turns > 0 ? turns : 1;
            _model = model;
            _intervalSeconds = interval > 0 ? interval : 10;
            _processTimeoutSeconds = timeout > 0 ? timeout : 120;
            _prompt = LoadPrompt(prompt);
            _timberbotCmd = ResolveTimberbotCommand();
            _currentTurn = 0;
            _lastResponse = null;
            _lastError = null;
            _cancelRequested = false;
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
            return _statusJw.Reset().OpenObj()
                .Prop("status", _status.ToString().ToLowerInvariant())
                .Prop("turn", _currentTurn)
                .Prop("totalTurns", _totalTurns)
                .Prop("binary", _binary ?? "")
                .Prop("lastResponse", JsonEscape(_lastResponse))
                .Prop("lastError", JsonEscape(_lastError))
                .CloseObj().ToString();
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
                    TimberbotLog.Info($"agent.cycle turn={turn}/{_totalTurns}");

                    // 1. Get brain state (TOON format)
                    _status = AgentStatus.GatheringState;
                    var (brainOk, brainOut) = RunProcess(_timberbotCmd, "brain", null, _processTimeoutSeconds);
                    if (!brainOk)
                    {
                        _lastError = "brain failed: " + brainOut;
                        TimberbotLog.Info($"ERROR agent.brain.fail turn={turn}: {brainOut}");
                        _status = AgentStatus.Error;
                        return;
                    }
                    TimberbotLog.Info($"agent.brain.ok turn={turn} bytes={brainOut.Length}");

                    if (_cancelRequested) break;

                    // 2. Spawn binary with state on stdin
                    _status = AgentStatus.Thinking;
                    var message = "Current game state:\n\n" + brainOut +
                        "\n\nRespond with timberbot.py commands, one per line. " +
                        "Example: place_building prefab:LumberjackFlag.IronTeeth x:120 y:130 z:2\n" +
                        "Example: set_speed speed:2\n" +
                        "Example: mark_trees x1:100 y1:100 x2:110 y2:110 z:2\n" +
                        "If no action needed, respond with: NONE";

                    var args = BuildBinaryArgs(_binary, _model, _prompt);
                    var (thinkOk, response) = RunProcess(_binary, args, message, _processTimeoutSeconds);
                    if (!thinkOk)
                    {
                        _lastError = "binary failed: " + response;
                        TimberbotLog.Info($"ERROR agent.think.fail turn={turn}: {response}");
                        // non-fatal: log and continue to next turn
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
                    int executed = 0;
                    foreach (var rawLine in lines)
                    {
                        if (_cancelRequested) break;

                        var line = rawLine.Trim();
                        if (string.IsNullOrEmpty(line)) continue;
                        if (line.StartsWith("#") || line.StartsWith("//")) continue;
                        if (line.Equals("NONE", StringComparison.OrdinalIgnoreCase)) continue;

                        // strip leading "timberbot.py " if present
                        var cmd = line;
                        if (cmd.StartsWith("timberbot.py "))
                            cmd = cmd.Substring("timberbot.py ".Length);

                        TimberbotLog.Info($"agent.exec turn={turn} cmd={cmd}");
                        var (execOk, execOut) = RunProcess(_timberbotCmd, cmd, null, _processTimeoutSeconds);
                        if (!execOk)
                            TimberbotLog.Info($"ERROR agent.exec.fail turn={turn} cmd={cmd}: {execOut}");
                        else
                            TimberbotLog.Info($"agent.exec.ok turn={turn} cmd={cmd}");
                        executed++;
                    }
                    TimberbotLog.Info($"agent.cycle.done turn={turn} executed={executed}");

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

            // try loading from mod folder
            var modDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Timberborn", "Mods", "Timberbot");
            var promptPath = Path.Combine(modDir, "agent-prompt.md");
            if (File.Exists(promptPath))
            {
                try { return File.ReadAllText(promptPath); }
                catch { }
            }

            // fallback minimal prompt
            return "You are playing Timberborn via the timberbot.py CLI. " +
                "Respond with timberbot.py commands, one per line. " +
                "Available commands: summary, buildings, beavers, trees, crops, prefabs, " +
                "place_building, demolish_building, place_path, set_speed, mark_trees, " +
                "plant_crop, set_priority, set_workers, unlock_building, set_floodgate. " +
                "Use key:value syntax for arguments. If no action needed, respond NONE.";
        }
    }
}
