// TimberbotAgent.cs -- Launches an interactive agent session for the player.
//
// The in-game UI gathers live colony state, then launches Claude, Codex, or a
// custom CLI interactively. The player keeps the real terminal session and can
// guide or correct the agent directly.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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
        private readonly string _terminal;
        private readonly string _pythonCommand;
        private string _binary;
        private string _model;
        private string _effort;
        private string _goal;
        private string _commandTemplate;
        private int _processTimeoutSeconds;

        private const string DEFAULT_GOAL = "reach 50 beavers with 77 well-being";

        private AgentStatus _status = AgentStatus.Idle;
        private string _lastError;
        private string _currentCmd;
        private Thread _thread;
        private volatile bool _cancelRequested;
        private volatile Process _activeProcess;
        private string _activeSessionPidPath;

        public TimberbotAgent(string terminal, string pythonCommand)
        {
            _terminal = terminal ?? "";
            _pythonCommand = pythonCommand ?? "";
        }

        public AgentStatus CurrentStatus => _status;
        public string CurrentGoal => _goal;
        public string CurrentCommand => _currentCmd;
        public string LastError => _lastError;
        public string Binary => _binary;
        public string Effort => _effort;

        private readonly TimberbotJw _jw = new TimberbotJw(1024);
        private readonly TimberbotJw _statusJw = new TimberbotJw(4096);

        public string Start(string binary, string model, string effort, int timeout, string goal, string command = null)
        {
            if (_status != AgentStatus.Idle && _status != AgentStatus.Done && _status != AgentStatus.Error)
                return _jw.Error("agent_busy", ("status", _status.ToString().ToLowerInvariant()));

            _binary = binary ?? "claude";
            _model = model;
            _effort = effort;
            _commandTemplate = string.IsNullOrWhiteSpace(command) ? null : command;
            _processTimeoutSeconds = timeout > 0 ? timeout : 120;
            _goal = string.IsNullOrEmpty(goal) ? DEFAULT_GOAL : goal;
            _lastError = null;
            _currentCmd = null;
            _cancelRequested = false;
            _activeSessionPidPath = null;
            _status = AgentStatus.GatheringState;

            _thread = new Thread(InteractiveSession) { IsBackground = true, Name = "Timberbot-Agent" };
            _thread.Start();

            TimberbotLog.Info($"agent.start binary={_binary} model={_model ?? "default"} custom={_commandTemplate != null}");
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
            bool stopped = false;
            try
            {
                if (_activeProcess != null && !_activeProcess.HasExited)
                {
                    _activeProcess.Kill();
                    stopped = true;
                }
            }
            catch { }

            if (!stopped)
                stopped = TryStopSessionPid();

            TimberbotLog.Info($"agent.stop requested stopped={stopped}");
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
                using var proc = new Process { StartInfo = psi };
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                if (!proc.WaitForExit(timeoutSeconds * 1000))
                {
                    try { proc.Kill(); } catch { }
                    return (false, "timeout");
                }
                var output = stdout.ToString().Trim();
                var error = stderr.ToString().Trim();
                if (!string.IsNullOrEmpty(error))
                    output = string.IsNullOrEmpty(output) ? error : output + "\n" + error;
                return (proc.ExitCode == 0, output);
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        private static bool IsCodexBinary(string binary)
        {
            if (string.IsNullOrWhiteSpace(binary))
                return false;

            try
            {
                return string.Equals(Path.GetFileNameWithoutExtension(binary.Trim()), "codex", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string QuoteArg(string value)
        {
            if (value == null)
                value = "";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string ShellQuoteArg(string value)
        {
            if (value == null)
                value = "";
            return "'" + value.Replace("'", "'\"'\"'") + "'";
        }

        private static (string exe, string args) SplitCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return ("", "");

            var text = command.Trim();
            if (text[0] == '"')
            {
                var end = text.IndexOf('"', 1);
                if (end > 0)
                    return (text.Substring(1, end - 1), text.Substring(end + 1).TrimStart());
            }

            var space = text.IndexOf(' ');
            return space < 0 ? (text, "") : (text.Substring(0, space), text.Substring(space + 1));
        }

        private IEnumerable<(string exe, string prefixArgs)> PythonCandidates()
        {
            if (!string.IsNullOrWhiteSpace(_pythonCommand))
            {
                var configured = SplitCommand(_pythonCommand);
                if (!string.IsNullOrWhiteSpace(configured.exe))
                    yield return configured;
                yield break;
            }

            if (TimberbotPaths.IsWindows)
            {
                yield return ("py", "-3");
                yield return ("python", "");
                yield return ("python3", "");
                yield break;
            }

            if (TimberbotPaths.IsMacOS)
            {
                yield return ("python3", "");
                yield return ("/opt/homebrew/bin/python3", "");
                yield return ("/usr/local/bin/python3", "");
                yield return ("/usr/bin/python3", "");
                yield return ("python", "");
                yield break;
            }

            yield return ("python3", "");
            yield return ("python", "");
        }

        private bool TryRunPython(string scriptPath, string scriptArgs, out string output, out string resolvedCommand)
        {
            output = "";
            resolvedCommand = "";
            var errors = new StringBuilder();

            foreach (var candidate in PythonCandidates())
            {
                if (candidate.exe.Contains(Path.DirectorySeparatorChar.ToString()) || candidate.exe.Contains(Path.AltDirectorySeparatorChar.ToString()))
                {
                    if (!File.Exists(candidate.exe))
                        continue;
                }

                var args = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(candidate.prefixArgs))
                    args.Append(candidate.prefixArgs).Append(" ");
                args.Append(QuoteArg(scriptPath));
                if (!string.IsNullOrWhiteSpace(scriptArgs))
                    args.Append(" ").Append(scriptArgs);

                var (ok, runOutput) = RunProcess(candidate.exe, args.ToString(), _processTimeoutSeconds);
                if (ok)
                {
                    output = runOutput;
                    resolvedCommand = string.IsNullOrWhiteSpace(candidate.prefixArgs)
                        ? candidate.exe
                        : candidate.exe + " " + candidate.prefixArgs;
                    return true;
                }

                if (errors.Length > 0)
                    errors.Append(" | ");
                errors.Append(candidate.exe).Append(": ").Append(runOutput);
            }

            output = errors.ToString();
            return false;
        }

        private (string exe, string args) BuildCustomCommand(string template, string skillFile, string startupPrompt, string modDir)
        {
            var cmd = template;

            cmd = cmd.Replace("{skill}", QuoteArg(skillFile));

            if (cmd.Contains("{prompt_file}"))
            {
                var promptFile = Path.Combine(modDir, "agent_prompt.md");
                File.WriteAllText(promptFile, startupPrompt, Encoding.UTF8);
                cmd = cmd.Replace("{prompt_file}", QuoteArg(promptFile));
                TimberbotLog.Info($"agent.custom wrote prompt file: {promptFile}");
            }

            cmd = cmd.Replace("{prompt}", QuoteArg(startupPrompt));

            if (!string.IsNullOrEmpty(_model))
                cmd = cmd.Replace("{model}", _model);
            else
                cmd = Regex.Replace(cmd, @"\S+\s+\{model\}", "");

            if (!string.IsNullOrEmpty(_effort))
                cmd = cmd.Replace("{effort}", _effort);
            else
                cmd = Regex.Replace(cmd, @"\S+\s+\{effort\}", "");

            cmd = Regex.Replace(cmd, @"\s{2,}", " ").Trim();
            return SplitCommand(cmd);
        }

        private string BuildStartupPrompt(string modDir)
        {
            var sb = new StringBuilder();

            _currentCmd = "gathering colony state";
            var brainScript = Path.Combine(modDir, "timberbot.py");
            var brainArgs = "brain " + QuoteArg("goal:" + _goal.Replace("\"", "'"));
            var ok = TryRunPython(brainScript, brainArgs, out var brainOut, out var pythonCmd);
            if (ok && !string.IsNullOrEmpty(brainOut))
            {
                sb.AppendLine("## CURRENT COLONY STATE");
                sb.AppendLine();
                sb.AppendLine(brainOut);
                sb.AppendLine();
                TimberbotLog.Info($"agent.brain.ok python={pythonCmd} bytes={brainOut.Length}");
            }
            else
            {
                sb.AppendLine("## COLONY STATE");
                sb.AppendLine();
                sb.AppendLine("Could not gather colony state for this launch. Run `timberbot.py brain` manually after startup.");
                sb.AppendLine();
                TimberbotLog.Info($"agent.brain.fail: {brainOut}");
            }

            sb.Append("Your system prompt contains session rules and the game guide. Complete the boot sequence from the guide FIRST (print the boot report), then work on this goal: ")
                .Append(_goal.Replace("\"", "'"));
            return sb.ToString();
        }

        private ProcessStartInfo BuildTerminalStartInfo(string modDir, string launchCmd)
        {
            var termCmd = _terminal.Trim().Replace("{cwd}", QuoteArg(modDir));
            if (termCmd.Contains("{command}"))
                termCmd = termCmd.Replace("{command}", launchCmd);
            else
                termCmd = termCmd + " " + launchCmd;

            var termParts = SplitCommand(termCmd);
            return new ProcessStartInfo
            {
                FileName = termParts.exe,
                Arguments = termParts.args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = modDir
            };
        }

        private ProcessStartInfo BuildMacDefaultStartInfo(string modDir, string shellCommand)
        {
            var pidFile = Path.Combine(modDir, "agent-session.pid");
            var scriptPath = Path.Combine(modDir, "agent-session.command");
            var script = new StringBuilder();
            script.AppendLine("#!/bin/bash");
            script.Append("cd ").AppendLine(ShellQuoteArg(modDir));
            script.Append("echo $$ > ").AppendLine(ShellQuoteArg(pidFile));
            script.Append("exec ").AppendLine(shellCommand);
            File.WriteAllText(scriptPath, script.ToString(), new UTF8Encoding(false));
            RunProcess("/bin/chmod", "+x " + QuoteArg(scriptPath), 10);
            _activeSessionPidPath = pidFile;

            return new ProcessStartInfo
            {
                FileName = "open",
                Arguments = "-a Terminal " + QuoteArg(scriptPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = modDir
            };
        }

        private static bool IsPidRunning(int pid)
        {
            if (pid <= 0)
                return false;

            var (ok, _) = RunProcess("/bin/kill", "-0 " + pid, 5);
            return ok;
        }

        private bool TryReadSessionPid(out int pid)
        {
            pid = 0;
            if (string.IsNullOrWhiteSpace(_activeSessionPidPath) || !File.Exists(_activeSessionPidPath))
                return false;

            try
            {
                return int.TryParse(File.ReadAllText(_activeSessionPidPath).Trim(), out pid) && pid > 0;
            }
            catch
            {
                return false;
            }
        }

        private bool TryStopSessionPid()
        {
            if (!TimberbotPaths.IsMacOS || !TryReadSessionPid(out var pid))
                return false;

            RunProcess("/bin/kill", "-TERM " + pid, 5);
            for (int i = 0; i < 20; i++)
            {
                if (!IsPidRunning(pid))
                    return true;
                Thread.Sleep(100);
            }

            RunProcess("/bin/kill", "-KILL " + pid, 5);
            return true;
        }

        private bool WaitForMacSession()
        {
            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(5, _processTimeoutSeconds));
            while (!_cancelRequested && DateTime.UtcNow < deadline)
            {
                if (TryReadSessionPid(out var pid) && IsPidRunning(pid))
                {
                    while (!_cancelRequested && IsPidRunning(pid))
                        Thread.Sleep(500);
                    return true;
                }
                Thread.Sleep(200);
            }
            return false;
        }

        private string BuildMacBuiltInShellCommand(string skillFile, string startupPrompt)
        {
            var parts = new List<string> { ShellQuoteArg(_binary) };
            if (IsCodexBinary(_binary))
            {
                parts.Add(ShellQuoteArg("-c"));
                parts.Add(ShellQuoteArg("model_instructions_file=\"" + skillFile + "\""));
                if (!string.IsNullOrEmpty(_model))
                {
                    parts.Add(ShellQuoteArg("--model"));
                    parts.Add(ShellQuoteArg(_model));
                }
                if (!string.IsNullOrEmpty(_effort))
                {
                    parts.Add(ShellQuoteArg("-c"));
                    parts.Add(ShellQuoteArg("model_reasoning_effort=\"" + _effort + "\""));
                }
            }
            else
            {
                parts.Add(ShellQuoteArg("--system-prompt-file"));
                parts.Add(ShellQuoteArg(skillFile));
                if (!string.IsNullOrEmpty(_model))
                {
                    parts.Add(ShellQuoteArg("--model"));
                    parts.Add(ShellQuoteArg(_model));
                }
                if (!string.IsNullOrEmpty(_effort))
                {
                    parts.Add(ShellQuoteArg("--effort"));
                    parts.Add(ShellQuoteArg(_effort));
                }
            }

            parts.Add(ShellQuoteArg(startupPrompt));
            return string.Join(" ", parts);
        }

        private void InteractiveSession()
        {
            try
            {
                _status = AgentStatus.GatheringState;

                var modDir = TimberbotPaths.ModDir;
                var skillFile = TimberbotPaths.SkillFile;
                var startupPrompt = BuildStartupPrompt(modDir);
                TimberbotLog.Info($"agent.launch binary={_binary} model={_model ?? "default"} effort={_effort ?? "default"} instructions={skillFile} startupBytes={Encoding.UTF8.GetByteCount(startupPrompt)}");

                _status = AgentStatus.Interactive;
                _currentCmd = null;

                string launchExe;
                string launchArgs;

                if (_commandTemplate != null)
                {
                    var custom = BuildCustomCommand(_commandTemplate, skillFile, startupPrompt, modDir);
                    launchExe = custom.exe;
                    launchArgs = custom.args;
                    TimberbotLog.Info($"agent.custom.launch exe={launchExe} args={launchArgs.Length}chars");
                }
                else
                {
                    var args = new StringBuilder();
                    if (IsCodexBinary(_binary))
                    {
                        args.Append("-c ").Append(QuoteArg("model_instructions_file=\"" + skillFile + "\""));
                        if (!string.IsNullOrEmpty(_model))
                            args.Append(" --model ").Append(_model);
                        if (!string.IsNullOrEmpty(_effort))
                            args.Append(" -c ").Append(QuoteArg("model_reasoning_effort=\"" + _effort + "\""));
                    }
                    else
                    {
                        args.Append("--system-prompt-file ").Append(QuoteArg(skillFile));
                        if (!string.IsNullOrEmpty(_model))
                            args.Append(" --model ").Append(_model);
                        if (!string.IsNullOrEmpty(_effort))
                            args.Append(" --effort ").Append(_effort);
                    }

                    args.Append(" ").Append(QuoteArg(startupPrompt));
                    launchExe = _binary;
                    launchArgs = args.ToString();
                }

                var launchCmd = launchExe + " " + launchArgs;
                ProcessStartInfo psi;
                bool waitForMacSession = false;

                if (!string.IsNullOrWhiteSpace(_terminal))
                {
                    psi = BuildTerminalStartInfo(modDir, launchCmd);
                }
                else if (TimberbotPaths.IsMacOS)
                {
                    if (_commandTemplate != null)
                        throw new InvalidOperationException("Custom binaries on macOS require Startup -> terminal.");

                    psi = BuildMacDefaultStartInfo(modDir, BuildMacBuiltInShellCommand(skillFile, startupPrompt));
                    waitForMacSession = true;
                }
                else
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = launchExe,
                        Arguments = launchArgs,
                        UseShellExecute = true,
                        WorkingDirectory = modDir
                    };
                }

                using var proc = new Process { StartInfo = psi };
                _activeProcess = proc;
                proc.Start();

                if (waitForMacSession)
                {
                    proc.WaitForExit();
                    _activeProcess = null;
                    if (!WaitForMacSession() && !_cancelRequested)
                        throw new InvalidOperationException("Terminal.app did not start a tracked agent session.");
                }
                else
                {
                    proc.WaitForExit();
                    _activeProcess = null;
                    TimberbotLog.Info($"agent.interactive.done exitCode={proc.ExitCode} cancelled={_cancelRequested}");
                }

                _status = _cancelRequested ? AgentStatus.Idle : AgentStatus.Done;
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
                _activeSessionPidPath = null;
            }
        }
    }
}
