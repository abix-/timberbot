// TimberbotAgent.cs -- Launches interactive claude session for the player.
//
// The player gets a real claude terminal where they can chat, guide, and
// correct the AI. Claude calls timberbot.py via its Bash tool to control
// the game. The player approves each action via normal permission prompts.
//
// Flow:
//   1. Gets game state via `timberbot.py brain`
//   2. Writes system prompt + game state to a temp file
//   3. Launches claude interactively (UseShellExecute=true)
//   4. Player interacts with claude in the terminal
//   5. When the player exits claude, agent goes Done
//
// Triggered via POST /api/agent/start or the in-game UI panel.

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
        Interactive,
        Done,
        Error
    }

    public class TimberbotAgent
    {
        private readonly string _terminal;  // terminal command prefix from settings.json
        private string _binary;
        private string _model;
        private string _effort;
        private string _goal;
        private int _processTimeoutSeconds;

        public TimberbotAgent(string terminal)
        {
            _terminal = terminal ?? "";
        }

        private const string DEFAULT_GOAL = "reach 50 beavers with 77 well-being";

        private AgentStatus _status = AgentStatus.Idle;
        private string _lastError;
        private string _currentCmd;
        private Thread _thread;
        private volatile bool _cancelRequested;
        private volatile Process _activeProcess;

        // read-only properties for in-game UI
        public AgentStatus CurrentStatus => _status;
        public string CurrentGoal => _goal;
        public string CurrentCommand => _currentCmd;
        public string LastError => _lastError;
        public string Binary => _binary;
        public string Effort => _effort;

        private readonly TimberbotJw _jw = new TimberbotJw(1024);
        private readonly TimberbotJw _statusJw = new TimberbotJw(4096);

        public string Start(string binary, string model, string effort, int timeout, string goal)
        {
            if (_status != AgentStatus.Idle && _status != AgentStatus.Done && _status != AgentStatus.Error)
                return _jw.Error("agent_busy", ("status", _status.ToString().ToLowerInvariant()));

            _binary = binary ?? "claude";
            _model = model;
            _effort = effort;
            _processTimeoutSeconds = timeout > 0 ? timeout : 120;
            _goal = string.IsNullOrEmpty(goal) ? DEFAULT_GOAL : goal;
            _lastError = null;
            _currentCmd = null;
            _cancelRequested = false;
            _status = AgentStatus.GatheringState;

            _thread = new Thread(InteractiveSession) { IsBackground = true, Name = "Timberbot-Agent" };
            _thread.Start();

            TimberbotLog.Info($"agent.start binary={_binary} model={_model ?? "default"}");
            return _jw.Reset().OpenObj()
                .Prop("status", "started")
                .Prop("binary", _binary)
                .CloseObj().ToString();
        }

        public string Stop()
        {
            if (_status == AgentStatus.Idle || _status == AgentStatus.Done || _status == AgentStatus.Error)
                return _jw.Error("agent_not_running");

            _cancelRequested = true;
            try { _activeProcess?.Kill(); } catch { }
            TimberbotLog.Info("agent.stop requested");
            return _jw.Reset().OpenObj().Prop("status", "stopping").CloseObj().ToString();
        }

        public string Status()
        {
            return _statusJw.Reset().OpenObj()
                .Prop("status", _status.ToString().ToLowerInvariant())
                .Prop("binary", _binary ?? "")
                .Prop("model", _model ?? "")
                .Prop("goal", JsonEscape(_goal))
                .Prop("currentCmd", JsonEscape(_currentCmd))
                .Prop("lastError", JsonEscape(_lastError))
                .CloseObj().ToString();
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length > 2000) s = s.Substring(0, 2000) + "...(truncated)";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        private static (bool ok, string output) RunProcess(string cmd, string args, int timeoutSeconds)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = cmd,
                    Arguments = args ?? "",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                var proc = new Process { StartInfo = psi };
                var stdout = new StringBuilder();
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                if (!proc.WaitForExit(timeoutSeconds * 1000))
                {
                    try { proc.Kill(); } catch { }
                    return (false, "timeout");
                }
                return (proc.ExitCode == 0, stdout.ToString().Trim());
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        private string BuildCombinedPrompt(string modDir)
        {
            var sb = new StringBuilder();

            // 1. rules
            var rulesFile = Path.Combine(modDir, "skill", "rules.txt");
            if (File.Exists(rulesFile))
            {
                sb.AppendLine("## SESSION RULES\n");
                sb.AppendLine(File.ReadAllText(rulesFile));
                sb.AppendLine();
            }

            // 2. live colony state via brain
            _currentCmd = "gathering colony state";
            var brainArgs = "brain \"goal:" + _goal.Replace("\"", "'") + "\"";
            var (ok, brainOut) = RunProcess("timberbot.py", brainArgs, _processTimeoutSeconds);
            if (ok && !string.IsNullOrEmpty(brainOut))
            {
                sb.AppendLine("## CURRENT COLONY STATE\n");
                sb.AppendLine(brainOut);
                sb.AppendLine();
                TimberbotLog.Info($"agent.brain.ok bytes={brainOut.Length}");
            }
            else
            {
                sb.AppendLine("## COLONY STATE: could not gather. Run `timberbot.py brain` manually.\n");
                TimberbotLog.Info($"agent.brain.fail: {brainOut}");
            }

            // 3. skill (game reference)
            var skillFile = Path.Combine(modDir, "skill", "timberbot.md");
            if (File.Exists(skillFile))
            {
                sb.AppendLine("## GAME REFERENCE\n");
                sb.AppendLine(File.ReadAllText(skillFile));
            }

            // write combined prompt to temp file
            var tempFile = Path.Combine(Path.GetTempPath(), "timberbot-prompt.md");
            File.WriteAllText(tempFile, sb.ToString());
            TimberbotLog.Info($"agent.prompt.written path={tempFile} bytes={sb.Length}");
            return tempFile;
        }

        private void InteractiveSession()
        {
            try
            {
                _status = AgentStatus.GatheringState;

                var modDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Timberborn", "Mods", "Timberbot");

                // build combined prompt: rules + brain state + skill
                var promptFile = BuildCombinedPrompt(modDir);

                _status = AgentStatus.Interactive;
                _currentCmd = "interactive session";

                var args = new StringBuilder();
                args.Append("--system-prompt-file \"").Append(promptFile).Append("\"");
                if (!string.IsNullOrEmpty(_model))
                    args.Append(" --model ").Append(_model);
                if (!string.IsNullOrEmpty(_effort))
                    args.Append(" --effort ").Append(_effort);
                // initial message: demand boot report, then goal
                args.Append(" \"Your system prompt contains session rules, colony state, and the game guide. Complete the boot sequence from the guide FIRST (print the boot report), then work on this goal: ")
                    .Append(_goal.Replace("\"", "'")).Append("\"");

                // build the full claude command (binary + flags + goal)
                var claudeCmd = _binary + " " + args;

                ProcessStartInfo psi;
                if (!string.IsNullOrWhiteSpace(_terminal))
                {
                    // terminal setting is a command prefix with optional {cwd} placeholder
                    // e.g. "wezterm start --cwd {cwd} --"
                    //      "wt -d {cwd} --"
                    //      "alacritty --working-directory {cwd} -e"
                    var termCmd = _terminal.Trim().Replace("{cwd}", "\"" + modDir + "\"");
                    var termParts = termCmd.Split(new[] { ' ' }, 2);
                    var termExe = termParts[0];
                    var termArgs = termParts.Length > 1 ? termParts[1] + " " : "";
                    psi = new ProcessStartInfo
                    {
                        FileName = termExe,
                        Arguments = termArgs + claudeCmd,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = modDir
                    };
                }
                else
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = _binary,
                        Arguments = args.ToString(),
                        UseShellExecute = true,
                        WorkingDirectory = modDir
                    };
                }

                var proc = new Process { StartInfo = psi };
                _activeProcess = proc;
                proc.Start();
                proc.WaitForExit();
                _activeProcess = null;

                _status = _cancelRequested ? AgentStatus.Idle : AgentStatus.Done;
                TimberbotLog.Info($"agent.interactive.done exitCode={proc.ExitCode} cancelled={_cancelRequested}");
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _status = AgentStatus.Error;
                TimberbotLog.Error("agent.interactive", ex);
            }
            finally
            {
                _activeProcess = null;
            }
        }


    }
}
