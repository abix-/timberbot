// TimberbotAgent.cs. Launches an interactive agent session for the player.
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
        private string _terminalOverride;  // per-session override from UI, null = use constructor value
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

        public string Start(string binary, string model, string effort, int timeout, string goal, string command = null, string terminal = null)
        {
            if (_status != AgentStatus.Idle && _status != AgentStatus.Done && _status != AgentStatus.Error)
                return _jw.Error("agent_busy", ("status", _status.ToString().ToLowerInvariant()));

            _binary = binary ?? "claude";
            _model = model;
            _effort = effort;
            _commandTemplate = string.IsNullOrWhiteSpace(command) ? null : command;
            _terminalOverride = terminal;  // null = use constructor default
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

        private static string JsonEscape(string s) => TimberbotPure.JsonEscape(s);

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

        private static bool IsCodexBinary(string binary) => TimberbotPure.IsCodexBinary(binary);

        private static string QuoteArg(string value) => TimberbotPure.QuoteArg(value);

        private static string ShellQuoteArg(string value) => TimberbotPure.ShellQuoteArg(value);

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

        private string BuildMergedInstructions(string skillFile, string startupPrompt, string modDir)
        {
            var mergedPath = Path.Combine(modDir, "agent-instructions.md");
            var staticPrompt = "";
            try
            {
                staticPrompt = File.ReadAllText(skillFile);
            }
            catch (Exception ex)
            {
                TimberbotLog.Info($"agent.instructions.read.fail: {ex.Message}");
            }

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(staticPrompt))
                sb.AppendLine(staticPrompt.TrimEnd());
            if (sb.Length > 0)
                sb.AppendLine().AppendLine();
            sb.AppendLine(startupPrompt.TrimEnd());

            var merged = sb.ToString();
            File.WriteAllText(mergedPath, merged, new UTF8Encoding(false));
            TimberbotLog.Info($"agent.instructions.generated path={mergedPath} bytes={Encoding.UTF8.GetByteCount(merged)}");
            return mergedPath;
        }

        private static string BuildStartupKickoff()
        {
            return "Begin now. Print the boot report first.";
        }

        private (string exe, string args) BuildCustomCommand(string template, string skillFile, string startupPrompt, string instructionsFile, string modDir)
        {
            var cmd = template;

            cmd = cmd.Replace("{skill}", QuoteArg(skillFile));
            cmd = cmd.Replace("{instructions_file}", QuoteArg(instructionsFile));

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

        private string _activeSessionLockPath;  // Windows lock file for terminal session tracking
        private string _activeSessionLogPath;   // Windows wrapper log with launch/exit details
        private string _activeStartupPromptPath;

        private ProcessStartInfo BuildWindowsTerminalStartInfo(string modDir, string instructionsFile, string kickoffPrompt, string terminalOverride = null)
        {
            var terminal = terminalOverride ?? _terminal;
            var lockFile = Path.Combine(modDir, "agent-session.lock");
            var sessionLog = Path.Combine(modDir, "agent-session.log");
            var scriptPath = Path.Combine(modDir, "agent-session.ps1");
            var promptPath = Path.Combine(modDir, "agent-startup-prompt.txt");
            File.WriteAllText(promptPath, kickoffPrompt, new UTF8Encoding(false));
            _activeStartupPromptPath = promptPath;
            var script = new StringBuilder();
            script.AppendLine("$ErrorActionPreference = 'Stop'");
            script.Append("$lockFile = ").AppendLine(ShellQuoteArg(lockFile));
            script.Append("$logFile = ").AppendLine(ShellQuoteArg(sessionLog));
            script.Append("$promptFile = ").AppendLine(ShellQuoteArg(promptPath));
            script.Append("$cwd = ").AppendLine(ShellQuoteArg(modDir));
            script.Append("$binary = ").AppendLine(ShellQuoteArg(_binary ?? ""));
            script.Append("$instructionsFile = ").AppendLine(ShellQuoteArg(instructionsFile));
            script.Append("$model = ").AppendLine(ShellQuoteArg(_model ?? ""));
            script.Append("$effort = ").AppendLine(ShellQuoteArg(_effort ?? ""));
            script.Append("$isCodex = ").AppendLine(IsCodexBinary(_binary) ? "$true" : "$false");
            script.AppendLine("Set-Content -Path $logFile -Value '[Timberbot] wrapper start' -Encoding UTF8");
            script.AppendLine("Add-Content -Path $logFile -Value ('[Timberbot] cwd=' + $cwd)");
            script.AppendLine("Add-Content -Path $logFile -Value ('[Timberbot] binary=' + $binary)");
            script.AppendLine("Add-Content -Path $logFile -Value ('[Timberbot] instructions=' + $instructionsFile)");
            script.AppendLine("Add-Content -Path $logFile -Value ('[Timberbot] promptFile=' + $promptFile)");
            script.AppendLine("if ($model) { Add-Content -Path $logFile -Value ('[Timberbot] model=' + $model) }");
            script.AppendLine("if ($effort) { Add-Content -Path $logFile -Value ('[Timberbot] effort=' + $effort) }");
            script.AppendLine("Set-Content -Path $lockFile -Value 'running' -Encoding UTF8");
            script.AppendLine("Set-Location -Path $cwd");
            script.AppendLine("$prompt = Get-Content -Path $promptFile -Raw");
            script.AppendLine("Add-Content -Path $logFile -Value ('[Timberbot] promptBytes=' + ([Text.Encoding]::UTF8.GetByteCount($prompt)))");
            script.AppendLine("$args = New-Object System.Collections.Generic.List[string]");
            script.AppendLine("if ($isCodex) {");
            script.AppendLine("  $args.Add('-c')");
            script.AppendLine("  $args.Add('model_instructions_file=\"' + $instructionsFile + '\"')");
            script.AppendLine("  if ($model) { $args.Add('--model'); $args.Add($model) }");
            script.AppendLine("  if ($effort) { $args.Add('-c'); $args.Add('model_reasoning_effort=\"' + $effort + '\"') }");
            script.AppendLine("} else {");
            script.AppendLine("  $args.Add('--system-prompt-file')");
            script.AppendLine("  $args.Add($instructionsFile)");
            script.AppendLine("  if ($model) { $args.Add('--model'); $args.Add($model) }");
            script.AppendLine("  if ($effort) { $args.Add('--effort'); $args.Add($effort) }");
            script.AppendLine("}");
            script.AppendLine("$args.Add($prompt)");
            script.AppendLine("$exitCode = 0");
            script.AppendLine("try {");
            script.AppendLine("  & $binary @args");
            script.AppendLine("  $exitCode = $LASTEXITCODE");
            script.AppendLine("} catch {");
            script.AppendLine("  Add-Content -Path $logFile -Value ('[Timberbot] exception=' + $_.Exception.Message)");
            script.AppendLine("  $exitCode = 1");
            script.AppendLine("} finally {");
            script.AppendLine("  Add-Content -Path $logFile -Value ('[Timberbot] exitCode=' + $exitCode)");
            script.AppendLine("  Remove-Item -LiteralPath $lockFile -ErrorAction SilentlyContinue");
            script.AppendLine("}");
            script.AppendLine("if ($exitCode -ne 0) {");
            script.AppendLine("  Add-Content -Path $logFile -Value '[Timberbot] launch failed, press Enter to close'");
            script.AppendLine("  Read-Host | Out-Null");
            script.AppendLine("}");
            script.AppendLine("exit $exitCode");
            File.WriteAllText(scriptPath, script.ToString(), Encoding.UTF8);
            _activeSessionLockPath = lockFile;
            _activeSessionLogPath = sessionLog;

            var termCmd = terminal.Trim().Replace("{cwd}", "\"" + modDir + "\"");
            var wrappedCmd = "powershell.exe -ExecutionPolicy Bypass -File " + QuoteArg(scriptPath);
            if (termCmd.Contains("{command}"))
                termCmd = termCmd.Replace("{command}", wrappedCmd);
            else
                termCmd = termCmd + " " + wrappedCmd;

            var termParts = SplitCommand(termCmd);
            TimberbotLog.Info($"agent.terminal.launch script={scriptPath} prompt={promptPath} lock={lockFile} log={sessionLog}");
            TimberbotLog.Info($"agent.terminal.command exe={termParts.exe} args={termParts.args}");
            return new ProcessStartInfo
            {
                FileName = termParts.exe,
                Arguments = termParts.args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = modDir
            };
        }

        private bool WaitForWindowsSession()
        {
            if (string.IsNullOrWhiteSpace(_activeSessionLockPath))
                return false;

            // wait for lock file to appear (terminal starting the script)
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!_cancelRequested && DateTime.UtcNow < deadline)
            {
                if (File.Exists(_activeSessionLockPath))
                    break;
                Thread.Sleep(200);
            }

            if (!File.Exists(_activeSessionLockPath) && !_cancelRequested)
            {
                LogWindowsSessionTail("agent.terminal.no_lock");
                return false;
            }

            // wait for lock file to disappear (script finished)
            while (!_cancelRequested)
            {
                if (!File.Exists(_activeSessionLockPath))
                {
                    LogWindowsSessionTail("agent.terminal.session_tail");
                    return true;
                }
                Thread.Sleep(500);
            }
            LogWindowsSessionTail("agent.terminal.cancelled_tail");
            return true;
        }

        private void LogWindowsSessionTail(string prefix)
        {
            if (string.IsNullOrWhiteSpace(_activeSessionLogPath) || !File.Exists(_activeSessionLogPath))
            {
                TimberbotLog.Info($"{prefix} log=missing");
                return;
            }

            try
            {
                var lines = File.ReadAllLines(_activeSessionLogPath);
                var start = Math.Max(0, lines.Length - 8);
                var tail = string.Join(" | ", lines, start, lines.Length - start);
                TimberbotLog.Info($"{prefix} {tail}");
            }
            catch (Exception ex)
            {
                TimberbotLog.Info($"{prefix} read_fail={ex.Message}");
            }
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

        private string BuildMacBuiltInShellCommand(string instructionsFile, string kickoffPrompt)
        {
            var parts = new List<string> { ShellQuoteArg(_binary) };
            if (IsCodexBinary(_binary))
            {
                parts.Add(ShellQuoteArg("-c"));
                parts.Add(ShellQuoteArg("model_instructions_file=\"" + instructionsFile + "\""));
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
                parts.Add(ShellQuoteArg(instructionsFile));
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

            parts.Add(ShellQuoteArg(kickoffPrompt));
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
                var instructionsFile = BuildMergedInstructions(skillFile, startupPrompt, modDir);
                var kickoffPrompt = BuildStartupKickoff();
                TimberbotLog.Info($"agent.launch binary={_binary} model={_model ?? "default"} effort={_effort ?? "default"} instructions={instructionsFile} startupBytes={Encoding.UTF8.GetByteCount(startupPrompt)} kickoffBytes={Encoding.UTF8.GetByteCount(kickoffPrompt)}");

                _status = AgentStatus.Interactive;
                _currentCmd = null;

                string launchExe;
                string launchArgs;

                if (_commandTemplate != null)
                {
                    var custom = BuildCustomCommand(_commandTemplate, skillFile, kickoffPrompt, instructionsFile, modDir);
                    launchExe = custom.exe;
                    launchArgs = custom.args;
                    TimberbotLog.Info($"agent.custom.launch exe={launchExe} args={launchArgs.Length}chars");
                }
                else
                {
                    var args = new StringBuilder();
                    if (IsCodexBinary(_binary))
                    {
                        args.Append("-c ").Append(QuoteArg("model_instructions_file=\"" + instructionsFile + "\""));
                        if (!string.IsNullOrEmpty(_model))
                            args.Append(" --model ").Append(_model);
                        if (!string.IsNullOrEmpty(_effort))
                            args.Append(" -c ").Append(QuoteArg("model_reasoning_effort=\"" + _effort + "\""));
                    }
                    else
                    {
                        args.Append("--system-prompt-file ").Append(QuoteArg(instructionsFile));
                        if (!string.IsNullOrEmpty(_model))
                            args.Append(" --model ").Append(_model);
                        if (!string.IsNullOrEmpty(_effort))
                            args.Append(" --effort ").Append(_effort);
                    }

                    args.Append(" ").Append(QuoteArg(kickoffPrompt));
                    launchExe = _binary;
                    launchArgs = args.ToString();
                }

                ProcessStartInfo psi;
                bool waitForMacSession = false;
                var effectiveTerminal = _terminalOverride ?? _terminal;

                bool waitForTerminalSession = false;
                if (!string.IsNullOrWhiteSpace(effectiveTerminal))
                {
                    if (_commandTemplate != null)
                        throw new InvalidOperationException("Custom command templates are not supported with terminal wrappers on Windows yet. Clear Startup -> terminal or use built-in claude/codex.");

                    psi = BuildWindowsTerminalStartInfo(modDir, instructionsFile, kickoffPrompt, effectiveTerminal);
                    waitForTerminalSession = true;
                }
                else if (TimberbotPaths.IsMacOS)
                {
                    if (_commandTemplate != null)
                        throw new InvalidOperationException("Custom binaries on macOS require Startup -> terminal.");

                    psi = BuildMacDefaultStartInfo(modDir, BuildMacBuiltInShellCommand(instructionsFile, kickoffPrompt));
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
                else if (waitForTerminalSession)
                {
                    proc.WaitForExit();  // terminal launcher exits immediately
                    _activeProcess = null;
                    TimberbotLog.Info("agent.terminal.launcher exited, polling lock file");
                    WaitForWindowsSession();
                    TimberbotLog.Info($"agent.terminal.session done cancelled={_cancelRequested}");
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
                _activeSessionLockPath = null;
                _activeSessionLogPath = null;
                _activeStartupPromptPath = null;
            }
        }
    }
}
