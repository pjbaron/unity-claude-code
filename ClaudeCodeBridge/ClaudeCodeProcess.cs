// ClaudeCodeProcess.cs
// Encapsulates all process lifecycle management for Claude Code CLI invocations.
// Part of the ClaudeCodeBridge refactor of ClaudeCodeTerminal.cs.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ClaudeCodeBridge
{
    internal class ClaudeCodeProcess
    {
        // ------------------------------------------------------------------
        // Process and threading
        // ------------------------------------------------------------------
        private Process _proc;
        private Thread _readerThread;
        private readonly object _lock = new object();
        private readonly Queue<Action> _mainThreadQueue = new Queue<Action>();

        // ------------------------------------------------------------------
        // Process state
        // ------------------------------------------------------------------
        private bool _isRunning;
        private string _sessionId = "";
        private StringBuilder _currentAssistantMsg = new StringBuilder();

        // 0=idle, 1=thinking (after rate_limit_event), 2=tool (after tool_use), 3=starting
        private int _activityState;
        private string _activeToolName;
        private bool _sessionCompleted;
        private bool _pendingAutoResume;
        private int _timeoutSeconds;

        // ------------------------------------------------------------------
        // Debug instrumentation -- DO NOT REMOVE until fix is verified
        // ------------------------------------------------------------------
        private const string kDbg = "[CCT-DBG]";
        private int _stdoutLineCount;
        private int _stderrLineCount;
        private double _processStartTimestamp;
        private double _lastHeartbeatTime;
        private double _lastOutputTime;

        private const int kDefaultTimeout = 600;

        // ------------------------------------------------------------------
        // Public properties
        // ------------------------------------------------------------------
        public bool IsRunning => _isRunning;
        public string SessionId => _sessionId;
        public int ActivityState => _activityState;
        public string ActiveToolName => _activeToolName;
        public bool PendingAutoResume { get => _pendingAutoResume; set => _pendingAutoResume = value; }
        public int MainThreadQueueCount { get { lock (_lock) { return _mainThreadQueue.Count; } } }

        // ------------------------------------------------------------------
        // Events
        // ------------------------------------------------------------------
        public event Action<string, LogType> OnLogEntry;
        public event Action<string> OnCompleted;
        public event Action OnProcessDied;
        public event Action OnErrorDuringExecution;

        // ------------------------------------------------------------------
        // Elapsed helper
        // ------------------------------------------------------------------
        private static string Elapsed(double since)
        {
            double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            return string.Format("{0:F3}s", now - since);
        }

        // ------------------------------------------------------------------
        // Start - launches the Claude Code CLI process
        // ------------------------------------------------------------------
        public void Start(string cliPath, string workingDirectory, string prompt, string sessionId, int maxTurns, int timeoutSeconds)
        {
            Debug.Log(string.Format("{0} Start: isRunning={1}, sessionId={2}",
                kDbg, _isRunning, string.IsNullOrEmpty(sessionId) ? "(none)" : sessionId));

            _timeoutSeconds = timeoutSeconds;

            var args = new StringBuilder();
            args.Append("-p ");
            args.Append(ClaudeCodeHelpers.QuoteArg(prompt));
            args.Append(" --output-format stream-json --verbose");
            args.AppendFormat(" --max-turns {0}", maxTurns);
            args.Append(" --dangerously-skip-permissions");

            // Resume session if we have one
            if (!string.IsNullOrEmpty(sessionId))
            {
                args.Append(" --resume ");
                args.Append(ClaudeCodeHelpers.QuoteArg(sessionId));
            }

            Debug.Log(string.Format("{0} CLI args: {1}", kDbg, args));

            var psi = new ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = args.ToString(),
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            // Strip ANTHROPIC_API_KEY so Claude Code uses the interactive
            // login (Max subscription) instead of billing against API credits.
            psi.Environment.Remove("ANTHROPIC_API_KEY");

            try
            {
                _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _proc.Exited += OnProcessExited;
                _proc.Start();

                _processStartTimestamp = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                _isRunning = true;
                _activityState = 3;
                _activeToolName = null;
                _sessionCompleted = false;
                _sessionId = "";
                _lastOutputTime = _processStartTimestamp;
                _lastHeartbeatTime = 0;
                _stdoutLineCount = 0;
                _stderrLineCount = 0;
                _currentAssistantMsg.Clear();

                Debug.Log(string.Format("{0} Process started: PID={1}", kDbg, _proc.Id));

                _readerThread = new Thread(ReadOutputStream) { IsBackground = true, Name = "CCT-StdoutReader" };
                _readerThread.Start();

                var errThread = new Thread(ReadErrorStream) { IsBackground = true, Name = "CCT-StderrReader" };
                errThread.Start();
            }
            catch (Exception ex)
            {
                Debug.LogError(string.Format("{0} EXCEPTION launching process: {1}", kDbg, ex));
                QueueMainThread(() => OnLogEntry?.Invoke("Failed to launch Claude Code: " + ex.Message, LogType.Error));
                QueueMainThread(() => OnLogEntry?.Invoke("Ensure 'claude' CLI is installed and on PATH, or set the path via Settings.", LogType.Error));
                _isRunning = false;
            }
        }

        // ------------------------------------------------------------------
        // Kill - terminates the running process cleanly
        // ------------------------------------------------------------------
        public void Kill()
        {
            bool wasAlive = _proc != null;
            bool hadExited = true;
            try { hadExited = _proc == null || _proc.HasExited; } catch { }

            Debug.Log(string.Format("{0} Kill: proc={1}, hadExited={2}",
                kDbg, wasAlive ? _proc.Id.ToString() : "(null)", hadExited));

            if (_proc != null && !hadExited)
            {
                try { _proc.Kill(); Debug.Log(string.Format("{0} Kill: Kill() sent", kDbg)); }
                catch (Exception ex) { Debug.LogWarning(string.Format("{0} Kill: Kill() failed: {1}", kDbg, ex.Message)); }
            }
            _proc = null;
            _isRunning = false;
        }

        // ------------------------------------------------------------------
        // DrainMainThreadQueue - processes all pending main-thread callbacks
        // ------------------------------------------------------------------
        public void DrainMainThreadQueue()
        {
            lock (_lock)
            {
                while (_mainThreadQueue.Count > 0)
                {
                    var action = _mainThreadQueue.Dequeue();
                    try { action(); } catch (Exception ex) { Debug.LogException(ex); }
                }
            }
        }

        // ------------------------------------------------------------------
        // UpdateProcessState - orphan detection, heartbeat, timeout check.
        // Call this from EditorApplication.update.
        // ------------------------------------------------------------------
        public void UpdateProcessState()
        {
            if (!_isRunning || _proc == null) return;

            // Orphan detection - safety net for cases where beforeAssemblyReload
            // didn't fire or something else killed the process unexpectedly
            bool procDead;
            try { procDead = _proc.HasExited; }
            catch { procDead = true; }

            bool threadDead = _readerThread == null || !_readerThread.IsAlive;

            if (procDead && threadDead)
            {
                Debug.LogWarning(string.Format("{0} [ORPHAN] proc and reader dead but isRunning=true, cleaning up", kDbg));
                _proc = null;
                _isRunning = false;
                OnLogEntry?.Invoke("[session lost unexpectedly - process died]", LogType.Error);
                return;
            }

            // Heartbeat + timeout while running
            if (_lastOutputTime > 0)
            {
                double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                double silentFor = now - _lastOutputTime;

                // 30-second heartbeat
                if (now - _lastHeartbeatTime >= 30.0)
                {
                    _lastHeartbeatTime = now;
                    bool procAlive = false;
                    try { procAlive = _proc != null && !_proc.HasExited; } catch { }
                    bool readerAlive = _readerThread != null && _readerThread.IsAlive;
                    int queueSize;
                    lock (_lock) { queueSize = _mainThreadQueue.Count; }

                    Debug.LogWarning(string.Format(
                        "{0} [HEARTBEAT] silent={1:F1}s | proc={2} | stdout-thread={3} | queue={4} | lines={5}",
                        kDbg, silentFor, procAlive ? "alive" : "DEAD",
                        readerAlive ? "alive" : "DEAD", queueSize, _stdoutLineCount));
                }

                // Timeout check
                int timeout = _timeoutSeconds > 0 ? _timeoutSeconds : EditorPrefs.GetInt(ClaudeCodeSettings.kPrefTimeout, kDefaultTimeout);
                if (timeout > 0 && silentFor >= timeout)
                {
                    Debug.LogError(string.Format("{0} TIMEOUT: {1:F1}s (limit={2}s), killing",
                        kDbg, silentFor, timeout));
                    Kill();
                    OnLogEntry?.Invoke(string.Format("[timed out after {0}s with no output - killed]", timeout), LogType.Error);
                }
            }
        }

        // ------------------------------------------------------------------
        // SaveStateForReload - persists session state to EditorPrefs for
        // domain reload survival
        // ------------------------------------------------------------------
        public void SaveStateForReload()
        {
            Debug.Log(string.Format("{0} SaveStateForReload: isRunning={1}, proc={2}, sessionId={3}, completed={4}",
                kDbg, _isRunning,
                _proc != null ? _proc.Id.ToString() : "(null)",
                string.IsNullOrEmpty(_sessionId) ? "(empty)" : _sessionId,
                _sessionCompleted));

            if (!_isRunning && _proc == null) return;

            // If the session already completed (got a "result" message),
            // don't treat this as an interruption. Just clean up.
            if (_sessionCompleted)
            {
                Debug.Log(string.Format("{0} SaveStateForReload: session already completed, just cleaning up (no auto-resume)", kDbg));
                Kill();
                return;
            }

            // Persist enough state to resume after reload
            EditorPrefs.SetBool(ClaudeCodeSettings.kPrefInterrupted, true);
            if (!string.IsNullOrEmpty(_sessionId))
            {
                EditorPrefs.SetString(ClaudeCodeSettings.kPrefInterruptedSession, _sessionId);
                Debug.Log(string.Format("{0} SaveStateForReload: saved sessionId={1} to EditorPrefs", kDbg, _sessionId));
            }
            else
            {
                Debug.LogWarning(string.Format("{0} SaveStateForReload: NO SESSION ID to save!", kDbg));
            }

            // Kill cleanly before Unity aborts our threads
            Kill();
        }

        // ------------------------------------------------------------------
        // RestoreAfterReload - reads persisted state back from EditorPrefs
        // after domain reload. Returns true if a session was interrupted and
        // state was restored.
        // ------------------------------------------------------------------
        public bool RestoreAfterReload()
        {
            if (!EditorPrefs.GetBool(ClaudeCodeSettings.kPrefInterrupted, false))
            {
                Debug.Log(string.Format("{0} RestoreAfterReload: normal startup (no interrupted flag)", kDbg));
                return false;
            }

            EditorPrefs.DeleteKey(ClaudeCodeSettings.kPrefInterrupted);
            string savedSession = EditorPrefs.GetString(ClaudeCodeSettings.kPrefInterruptedSession, "");
            EditorPrefs.DeleteKey(ClaudeCodeSettings.kPrefInterruptedSession);

            Debug.Log(string.Format("{0} RestoreAfterReload: RELOAD RECOVERY - savedSession={1}, currentSession={2}",
                kDbg,
                string.IsNullOrEmpty(savedSession) ? "(empty)" : savedSession,
                string.IsNullOrEmpty(_sessionId) ? "(empty)" : _sessionId));

            // Restore session id if we lost it during reload
            if (!string.IsNullOrEmpty(savedSession) && string.IsNullOrEmpty(_sessionId))
            {
                _sessionId = savedSession;
                Debug.Log(string.Format("{0} RestoreAfterReload: restored _sessionId from EditorPrefs", kDbg));
            }

            // Clean up stale runtime state from before reload
            _isRunning = false;
            _proc = null;

            OnLogEntry?.Invoke("[session interrupted by recompile/domain reload]", LogType.Error);

            if (EditorPrefs.GetBool(ClaudeCodeSettings.kPrefAutoResume, true) && !string.IsNullOrEmpty(_sessionId))
            {
                OnLogEntry?.Invoke("[auto-resuming session...]", LogType.Log);
                _pendingAutoResume = true;
                Debug.Log(string.Format("{0} RestoreAfterReload: auto-resume queued for session {1}", kDbg, _sessionId));
            }
            else
            {
                Debug.Log(string.Format("{0} RestoreAfterReload: NOT auto-resuming. autoResume={1}, hasSession={2}",
                    kDbg,
                    EditorPrefs.GetBool(ClaudeCodeSettings.kPrefAutoResume, true),
                    !string.IsNullOrEmpty(_sessionId)));
            }

            return true;
        }

        // ------------------------------------------------------------------
        // ProcessStreamLine - parses a single JSON line from Claude Code CLI
        // stream output
        // ------------------------------------------------------------------
        public void ProcessStreamLine(string json)
        {
            _lastOutputTime = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

            try
            {
                string msgType = ClaudeCodeHelpers.ExtractJsonString(json, "type");
                Debug.Log(string.Format("{0} ProcessStreamLine: type=\"{1}\" (+{2})",
                    kDbg, msgType ?? "(null)", Elapsed(_processStartTimestamp)));

                if (msgType == "system")
                {
                    string subtype = ClaudeCodeHelpers.ExtractJsonString(json, "subtype");
                    Debug.Log(string.Format("{0}   system: subtype={1}", kDbg, subtype ?? "(null)"));

                    if (subtype == "error_during_execution")
                    {
                        Debug.LogError(string.Format("{0}   system/error_during_execution", kDbg));
                        QueueMainThread(() => OnErrorDuringExecution?.Invoke());
                        return;
                    }

                    if (subtype == "init")
                    {
                        string sid = ClaudeCodeHelpers.ExtractJsonString(json, "session_id");
                        Debug.Log(string.Format("{0}   system/init: session_id={1}", kDbg, sid ?? "(null)"));
                        if (!string.IsNullOrEmpty(sid))
                        {
                            // Write session_id immediately on reader thread so it's
                            // available for SaveStateForReload even if main thread
                            // queue hasn't drained yet. String ref assignment is atomic in C#.
                            _sessionId = sid;
                            Debug.Log(string.Format("{0}   system/init: _sessionId set IMMEDIATELY (not queued)", kDbg));
                            QueueMainThread(() =>
                            {
                                OnLogEntry?.Invoke("[session: " + sid.Substring(0, Math.Min(8, sid.Length)) + "...]", LogType.Log);
                            });
                        }
                        // After init, the next thing Claude Code does is send the prompt
                        // to the API. No rate_limit_event is emitted on the first turn,
                        // so switch to THINKING now.
                        _activityState = 1;
                    }
                    return;
                }

                if (msgType == "result")
                {
                    _sessionCompleted = true;
                    FlushAssistantBuffer();

                    Debug.Log(string.Format("{0}   result: session marked COMPLETED", kDbg));
                    string sid = ClaudeCodeHelpers.ExtractJsonString(json, "session_id");
                    string cost = ClaudeCodeHelpers.ExtractJsonString(json, "total_cost_usd");
                    string turns = ClaudeCodeHelpers.ExtractJsonString(json, "num_turns");
                    string subtype = ClaudeCodeHelpers.ExtractJsonString(json, "subtype");
                    string inputTokens = ClaudeCodeHelpers.ExtractJsonString(json, "input_tokens");
                    string outputTokens = ClaudeCodeHelpers.ExtractJsonString(json, "output_tokens");

                    Debug.Log(string.Format("{0}   result: subtype={1}, turns={2}, cost={3}",
                        kDbg, subtype, turns, cost));

                    QueueMainThread(() =>
                    {
                        if (!string.IsNullOrEmpty(sid))
                            _sessionId = sid;

                        string info = "[done";

                        if (EditorPrefs.GetBool(ClaudeCodeSettings.kPrefShowCost, false) && !string.IsNullOrEmpty(cost))
                            info += " | cost: $" + cost;

                        if (EditorPrefs.GetBool(ClaudeCodeSettings.kPrefShowTurns, true) && !string.IsNullOrEmpty(turns))
                            info += " | turns: " + turns;

                        if (EditorPrefs.GetBool(ClaudeCodeSettings.kPrefShowContext, true) &&
                            !string.IsNullOrEmpty(inputTokens) && !string.IsNullOrEmpty(outputTokens))
                        {
                            long inTok, outTok;
                            if (long.TryParse(inputTokens, out inTok) && long.TryParse(outputTokens, out outTok))
                            {
                                long used = inTok + outTok;
                                long contextWindow = 200000;
                                long remaining = contextWindow - used;
                                float pct = (float)remaining / contextWindow * 100f;
                                info += string.Format(" | context: {0:N0}/{1:N0} remaining ({2:F0}%)",
                                    remaining, contextWindow, pct);
                            }
                        }

                        if (subtype == "error")
                        {
                            info += " | ERROR";
                            OnErrorDuringExecution?.Invoke();
                        }
                        info += "]";
                        OnLogEntry?.Invoke(info, LogType.Log);
                        OnCompleted?.Invoke(_sessionId);
                    });
                    return;
                }

                if (msgType == "assistant")
                {
                    string content = ClaudeCodeHelpers.ExtractContentText(json);
                    string toolName = ClaudeCodeHelpers.ExtractToolUse(json);

                    Debug.Log(string.Format("{0}   assistant: hasText={1}, toolName={2}",
                        kDbg, !string.IsNullOrEmpty(content), toolName ?? "(none)"));

                    if (!string.IsNullOrEmpty(content))
                    {
                        lock (_lock)
                        {
                            _currentAssistantMsg.Append(content);
                        }
                        FlushAssistantBuffer();
                    }

                    if (!string.IsNullOrEmpty(toolName))
                    {
                        Debug.Log(string.Format("{0}   >> TOOL_USE: {1} -- ReadLine will BLOCK until tool completes",
                            kDbg, toolName));
                        _activityState = 2;
                        _activeToolName = toolName;
                        QueueMainThread(() => OnLogEntry?.Invoke("[tool: " + toolName + "]", LogType.Log));
                    }
                    return;
                }

                if (msgType == "error" || msgType == "error_during_execution")
                {
                    string errMsg = ClaudeCodeHelpers.ExtractJsonString(json, "error");
                    if (string.IsNullOrEmpty(errMsg)) errMsg = json;
                    string captured = errMsg;
                    Debug.LogError(string.Format("{0}   error: {1}", kDbg, captured));
                    QueueMainThread(() =>
                    {
                        OnLogEntry?.Invoke("Error: " + captured, LogType.Error);
                        OnErrorDuringExecution?.Invoke();
                    });
                    return;
                }

                if (msgType == "tool_result")
                {
                    bool isError = json.Contains("\"is_error\":true") || json.Contains("\"is_error\": true");
                    Debug.Log(string.Format("{0}   tool_result: is_error={1}", kDbg, isError));

                    if (isError)
                    {
                        string errContent = ClaudeCodeHelpers.ExtractContentText(json);
                        if (!string.IsNullOrEmpty(errContent))
                        {
                            string captured = errContent;
                            QueueMainThread(() => OnLogEntry?.Invoke("[tool error] " + captured, LogType.Error));
                        }
                    }
                    return;
                }

                if (msgType == "rate_limit_event")
                {
                    // This is the last message before Claude Code sends the API request.
                    // Silence after this = model is thinking.
                    _activityState = 1;
                    _activeToolName = null;
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError(string.Format("{0} ProcessStreamLine EXCEPTION: {1}", kDbg, ex));
                QueueMainThread(() => OnLogEntry?.Invoke("[parse error] " + ex.Message, LogType.Error));
            }
        }

        // ------------------------------------------------------------------
        // Reader thread entry points
        // ------------------------------------------------------------------
        private void ReadOutputStream()
        {
            Debug.Log(string.Format("{0} [stdout-thread] ENTER", kDbg));
            try
            {
                using (var reader = _proc.StandardOutput)
                {
                    string line;
                    while (true)
                    {
                        int lineNum = _stdoutLineCount;
                        Debug.Log(string.Format("{0} [stdout] BLOCKING ReadLine (after #{1}, +{2})",
                            kDbg, lineNum, Elapsed(_processStartTimestamp)));

                        line = reader.ReadLine();

                        if (line == null)
                        {
                            Debug.Log(string.Format("{0} [stdout] EOF, +{1}", kDbg, Elapsed(_processStartTimestamp)));
                            break;
                        }

                        _stdoutLineCount++;
                        string preview = line.Length > 200 ? line.Substring(0, 200) + "..." : line;
                        Debug.Log(string.Format("{0} [stdout] #{1} ({2}ch, +{3}): {4}",
                            kDbg, _stdoutLineCount, line.Length, Elapsed(_processStartTimestamp), preview));

                        ProcessStreamLine(line);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError(string.Format("{0} [stdout] EXCEPTION: {1}", kDbg, ex));
                QueueMainThread(() => OnLogEntry?.Invoke("Read error: " + ex.Message, LogType.Error));
            }
            Debug.Log(string.Format("{0} [stdout] EXIT (total: {1} lines, +{2})",
                kDbg, _stdoutLineCount, Elapsed(_processStartTimestamp)));
        }

        private void ReadErrorStream()
        {
            Debug.Log(string.Format("{0} [stderr-thread] ENTER", kDbg));
            try
            {
                using (var reader = _proc.StandardError)
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        _stderrLineCount++;
                        _lastOutputTime = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                        string captured = line;
                        Debug.Log(string.Format("{0} [stderr] #{1} (+{2}): {3}",
                            kDbg, _stderrLineCount, Elapsed(_processStartTimestamp), captured));
                        QueueMainThread(() => OnLogEntry?.Invoke("[stderr] " + captured, LogType.Error));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError(string.Format("{0} [stderr] EXCEPTION: {1}", kDbg, ex));
            }
            Debug.Log(string.Format("{0} [stderr] EXIT (total: {1} lines, +{2})",
                kDbg, _stderrLineCount, Elapsed(_processStartTimestamp)));
        }

        // ------------------------------------------------------------------
        // Internal helpers
        // ------------------------------------------------------------------
        private void FlushAssistantBuffer()
        {
            string text;
            lock (_lock)
            {
                if (_currentAssistantMsg.Length == 0) return;
                text = _currentAssistantMsg.ToString();
                _currentAssistantMsg.Clear();
            }
            Debug.Log(string.Format("{0} FlushAssistantBuffer: {1} chars", kDbg, text.Length));
            QueueMainThread(() => OnLogEntry?.Invoke(text, LogType.Log));
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            int exitCode = -1;
            try { exitCode = _proc != null ? _proc.ExitCode : -1; } catch { }
            Debug.Log(string.Format("{0} OnProcessExited: exitCode={1}, +{2}",
                kDbg, exitCode, Elapsed(_processStartTimestamp)));
            FlushAssistantBuffer();
            QueueMainThread(() =>
            {
                Debug.Log(string.Format("{0} OnProcessExited [main-thread]: isRunning -> false", kDbg));
                _isRunning = false;
                OnProcessDied?.Invoke();
            });
        }

        private void QueueMainThread(Action action)
        {
            lock (_lock)
            {
                _mainThreadQueue.Enqueue(action);
            }
        }
    }
}
