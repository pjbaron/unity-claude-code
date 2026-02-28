// ClaudeCodeTerminal.cs
// Drop into an Editor/ folder in your Unity project.
// Requires: Claude Code CLI installed and on PATH, unity-mcp package installed and server running.
// Open via Window > Claude Code Terminal

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ClaudeCodeBridge
{
    public class ClaudeCodeTerminal : EditorWindow
    {
        // ------------------------------------------------------------------
        // Serialised state (survives domain reload)
        // ------------------------------------------------------------------
        [SerializeField] private string _inputText = "";
        [SerializeField] private string _sessionId = "";
        [SerializeField] private List<LogEntry> _log = new List<LogEntry>();
        [SerializeField] private Vector2 _scrollPos;
        [SerializeField] private bool _autoScroll = true;
        [SerializeField] private string _claudePath = "claude";
        [SerializeField] private int _maxTurns = 25;

        // ------------------------------------------------------------------
        // Runtime (not serialised)
        // ------------------------------------------------------------------
        private Process _proc;
        private Thread _readerThread;
        private readonly object _lock = new object();
        private readonly Queue<Action> _mainThreadQueue = new Queue<Action>();
        private bool _isRunning;
        private StringBuilder _currentAssistantMsg = new StringBuilder();
        private GUIStyle _logAreaStyle;
        private bool _stylesReady;
        private double _lastOutputTime;

        // Activity state for status indicator
        // 0=idle, 1=thinking (after rate_limit_event), 2=tool (after tool_use), 3=starting
        private int _activityState;
        private string _activeToolName;
        private bool _sessionCompleted; // true once we receive a "result" message

        // ------------------------------------------------------------------
        // Debug instrumentation -- DO NOT REMOVE until fix is verified
        // ------------------------------------------------------------------
        private const string kDbg = "[CCT-DBG]";
        private int _stdoutLineCount;
        private int _stderrLineCount;
        private double _processStartTimestamp;
        private double _lastHeartbeatTime;

        private static string Elapsed(double since)
        {
            double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            return string.Format("{0:F3}s", now - since);
        }

        // ------------------------------------------------------------------
        // Domain reload recovery
        // ------------------------------------------------------------------
        private const string kPrefInterrupted = "ClaudeCodeTerminal_WasInterrupted";
        private const string kPrefInterruptedSession = "ClaudeCodeTerminal_InterruptedSessionId";
        private const string kPrefAutoResume = "ClaudeCodeTerminal_AutoResume";
        private bool _pendingAutoResume;

        private const string kPrefTimeout = "ClaudeCodeTerminal_TimeoutSec";
        private const int kDefaultTimeout = 600;

        private const string kPrefShowCost = "ClaudeCodeTerminal_ShowCost";
        private const string kPrefShowTurns = "ClaudeCodeTerminal_ShowTurns";
        private const string kPrefShowContext = "ClaudeCodeTerminal_ShowContext";
        [SerializeField] private string _logText = "";

        // ------------------------------------------------------------------
        // Data types
        // ------------------------------------------------------------------
        [Serializable]
        private class LogEntry
        {
            public string text;
            public int kind; // 0=assistant, 1=user, 2=error, 3=status
        }

        // ------------------------------------------------------------------
        // Menu item
        // ------------------------------------------------------------------
        [MenuItem("Window/Claude Code Terminal")]
        public static void Open()
        {
            var w = GetWindow<ClaudeCodeTerminal>("Claude Code");
            w.minSize = new Vector2(420, 300);
            w.Show();
        }

        // ------------------------------------------------------------------
        // GUI
        // ------------------------------------------------------------------
        private void EnsureStyles()
        {
            if (_stylesReady) return;

            _logAreaStyle = new GUIStyle(EditorStyles.textArea)
            {
                wordWrap = true,
                richText = false,
                padding = new RectOffset(6, 6, 4, 4),
                font = EditorStyles.standardFont,
            };
            var textCol = new Color(0.816f, 0.816f, 0.816f); // #D0D0D0
            _logAreaStyle.normal.textColor = textCol;
            _logAreaStyle.focused.textColor = textCol;
            _logAreaStyle.active.textColor = textCol;
            _logAreaStyle.hover.textColor = textCol;
            _logAreaStyle.onNormal.textColor = textCol;
            _logAreaStyle.onFocused.textColor = textCol;
            _logAreaStyle.onActive.textColor = textCol;
            _logAreaStyle.onHover.textColor = textCol;
            _stylesReady = true;
        }

        private void OnGUI()
        {
            EnsureStyles();
            ProcessMainThreadQueue();

            DrawToolbar();
            DrawLog();
            DrawInput();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // Status indicator
            string status;
            Color col;
            if (!_isRunning)
            {
                status = "  IDLE";
                col = Color.green;
            }
            else
            {
                switch (_activityState)
                {
                    case 1:
                        status = "  THINKING...";
                        col = new Color(0.4f, 0.7f, 1.0f); // light blue
                        break;
                    case 2:
                        status = "  TOOL...";
                        col = new Color(1.0f, 0.6f, 0.2f); // orange
                        break;
                    default:
                        status = "  WORKING...";
                        col = Color.yellow;
                        break;
                }
            }
            var oldCol = GUI.contentColor;
            GUI.contentColor = col;
            GUILayout.Label(status, EditorStyles.toolbarButton, GUILayout.Width(100));
            GUI.contentColor = oldCol;

            // Show active tool name when waiting for a tool
            if (_isRunning && _activityState == 2 && !string.IsNullOrEmpty(_activeToolName))
            {
                string shortTool = _activeToolName;
                // Strip mcp__unityMCP__ prefix for readability
                if (shortTool.StartsWith("mcp__unityMCP__"))
                    shortTool = shortTool.Substring(15);
                GUILayout.Label(shortTool, EditorStyles.miniLabel, GUILayout.MaxWidth(160));
            }

            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(45)))
            {
                _log.Clear();
                _logText = "";
                _scrollPos = Vector2.zero;
                GUI.FocusControl(null);
                Repaint();
            }

            GUILayout.FlexibleSpace();

            if (!string.IsNullOrEmpty(_sessionId))
            {
                GUILayout.Label("Session: " + _sessionId.Substring(0, Mathf.Min(8, _sessionId.Length)) + "...",
                    EditorStyles.miniLabel, GUILayout.Width(140));
            }

            if (GUILayout.Button("Settings", EditorStyles.toolbarDropDown, GUILayout.Width(70)))
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Auto-scroll"), _autoScroll, () => _autoScroll = !_autoScroll);
                menu.AddItem(new GUIContent("New Session"), false, () => { _sessionId = ""; AddLog("Session cleared.", 3); });
                menu.AddItem(new GUIContent("Clear Log"), false, () => { _log.Clear(); _logText = ""; _scrollPos = Vector2.zero; GUI.FocusControl(null); Repaint(); });
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Configure CLI Path..."), false, ShowCLIPathDialog);
                menu.AddItem(new GUIContent("Set Max Turns..."), false, ShowMaxTurnsDialog);
                menu.AddItem(new GUIContent("Set Timeout..."), false, ShowTimeoutDialog);
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Summary/Show Cost"), EditorPrefs.GetBool(kPrefShowCost, false),
                    () => EditorPrefs.SetBool(kPrefShowCost, !EditorPrefs.GetBool(kPrefShowCost, false)));
                menu.AddItem(new GUIContent("Summary/Show Turns"), EditorPrefs.GetBool(kPrefShowTurns, true),
                    () => EditorPrefs.SetBool(kPrefShowTurns, !EditorPrefs.GetBool(kPrefShowTurns, true)));
                menu.AddItem(new GUIContent("Summary/Show Context"), EditorPrefs.GetBool(kPrefShowContext, true),
                    () => EditorPrefs.SetBool(kPrefShowContext, !EditorPrefs.GetBool(kPrefShowContext, true)));
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Auto-resume after recompile"), EditorPrefs.GetBool(kPrefAutoResume, true),
                    () => EditorPrefs.SetBool(kPrefAutoResume, !EditorPrefs.GetBool(kPrefAutoResume, true)));
                menu.ShowAsContext();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawLog()
        {
            // Guard against style not yet initialised
            GUIStyle style = _logAreaStyle ?? EditorStyles.textArea;

            // Calculate content height for the text
            float viewWidth = EditorGUIUtility.currentViewWidth - 20;
            if (viewWidth < 100) viewWidth = 100;
            var content = new GUIContent(_logText);
            float contentHeight = style.CalcHeight(content, viewWidth);
            // Ensure minimum height so the scroll area is always usable
            float areaHeight = position.height - 60; // leave room for toolbar + input
            if (areaHeight < 50) areaHeight = 50;

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos, GUILayout.Height(areaHeight));

            // Draw a selectable, non-editable text block using SelectableLabel
            // which supports mouse wheel scrolling and partial text selection
            Rect textRect = GUILayoutUtility.GetRect(viewWidth, Mathf.Max(contentHeight, areaHeight), style);
            EditorGUI.SelectableLabel(textRect, _logText, style);

            EditorGUILayout.EndScrollView();

            // Force scroll to bottom
            if (_autoScroll && Event.current.type == EventType.Repaint)
            {
                _scrollPos.y = float.MaxValue;
            }
        }

        private void DrawInput()
        {
            EditorGUILayout.BeginHorizontal();

            GUI.SetNextControlName("ClaudeInput");
            _inputText = EditorGUILayout.TextField(_inputText, GUILayout.ExpandWidth(true));

            if (_isRunning)
            {
                // Show Cancel button while a request is in flight
                var oldBg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.9f, 0.3f, 0.3f);
                if (GUILayout.Button("Cancel", GUILayout.Width(60)))
                {
                    KillProcess();
                    AddLog("[cancelled by user]", 3);
                }
                GUI.backgroundColor = oldBg;
            }
            else
            {
                // Use KeyUp - TextField consumes the first KeyDown for text commit
                bool enterPressed = Event.current.type == EventType.KeyUp &&
                                    (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter) &&
                                    GUI.GetNameOfFocusedControl() == "ClaudeInput";

                bool submit = GUILayout.Button("Send", GUILayout.Width(60)) || enterPressed;

                if (submit && !string.IsNullOrWhiteSpace(_inputText))
                {
                    SendPrompt(_inputText.Trim());
                    _inputText = "";
                    GUI.FocusControl("ClaudeInput");
                    Event.current.Use();
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        // ------------------------------------------------------------------
        // Core: launch Claude Code process
        // ------------------------------------------------------------------
        private void SendPrompt(string prompt)
        {
            AddLog("> " + prompt, 1);
            Debug.Log(string.Format("{0} SendPrompt: isRunning={1}, sessionId={2}",
                kDbg, _isRunning, string.IsNullOrEmpty(_sessionId) ? "(none)" : _sessionId));

            string projectPath = Path.GetDirectoryName(Application.dataPath);

            var args = new StringBuilder();
            args.Append("-p ");
            args.Append(QuoteArg(prompt));
            args.Append(" --output-format stream-json --verbose");
            args.AppendFormat(" --max-turns {0}", _maxTurns);
            args.Append(" --dangerously-skip-permissions");

            // Resume session if we have one
            if (!string.IsNullOrEmpty(_sessionId))
            {
                args.Append(" --resume ");
                args.Append(QuoteArg(_sessionId));
            }

            Debug.Log(string.Format("{0} CLI args: {1}", kDbg, args));

            var psi = new ProcessStartInfo
            {
                FileName = _claudePath,
                Arguments = args.ToString(),
                WorkingDirectory = projectPath,
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
                AddLog("Failed to launch Claude Code: " + ex.Message, 2);
                AddLog("Ensure 'claude' CLI is installed and on PATH, or set the path via Settings.", 2);
                _isRunning = false;
            }
        }

        // ------------------------------------------------------------------
        // Stream reading (background threads)
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
                QueueMainThread(() => AddLog("Read error: " + ex.Message, 2));
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
                        QueueMainThread(() => AddLog("[stderr] " + captured, 2));
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
        // Parse stream-json lines
        // ------------------------------------------------------------------
        private void ProcessStreamLine(string json)
        {
            _lastOutputTime = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

            try
            {
                string msgType = ExtractJsonString(json, "type");
                Debug.Log(string.Format("{0} ProcessStreamLine: type=\"{1}\" (+{2})",
                    kDbg, msgType ?? "(null)", Elapsed(_processStartTimestamp)));

                if (msgType == "system")
                {
                    string subtype = ExtractJsonString(json, "subtype");
                    Debug.Log(string.Format("{0}   system: subtype={1}", kDbg, subtype ?? "(null)"));

                    if (subtype == "init")
                    {
                        string sid = ExtractJsonString(json, "session_id");
                        Debug.Log(string.Format("{0}   system/init: session_id={1}", kDbg, sid ?? "(null)"));
                        if (!string.IsNullOrEmpty(sid))
                        {
                            // Write session_id immediately on reader thread so it's
                            // available for OnBeforeAssemblyReload even if main thread
                            // queue hasn't drained yet. String ref assignment is atomic in C#.
                            _sessionId = sid;
                            Debug.Log(string.Format("{0}   system/init: _sessionId set IMMEDIATELY (not queued)", kDbg));
                            QueueMainThread(() =>
                            {
                                AddLog("[session: " + sid.Substring(0, Mathf.Min(8, sid.Length)) + "...]", 3);
                            });
                        }
                    }
                    return;
                }

                if (msgType == "result")
                {
                    _sessionCompleted = true;
                    FlushAssistantBuffer();

                    Debug.Log(string.Format("{0}   result: session marked COMPLETED", kDbg));
                    string sid = ExtractJsonString(json, "session_id");
                    string cost = ExtractJsonString(json, "total_cost_usd");
                    string turns = ExtractJsonString(json, "num_turns");
                    string subtype = ExtractJsonString(json, "subtype");
                    string inputTokens = ExtractJsonString(json, "input_tokens");
                    string outputTokens = ExtractJsonString(json, "output_tokens");

                    Debug.Log(string.Format("{0}   result: subtype={1}, turns={2}, cost={3}",
                        kDbg, subtype, turns, cost));

                    QueueMainThread(() =>
                    {
                        if (!string.IsNullOrEmpty(sid))
                            _sessionId = sid;

                        string info = "[done";

                        if (EditorPrefs.GetBool(kPrefShowCost, false) && !string.IsNullOrEmpty(cost))
                            info += " | cost: $" + cost;

                        if (EditorPrefs.GetBool(kPrefShowTurns, true) && !string.IsNullOrEmpty(turns))
                            info += " | turns: " + turns;

                        if (EditorPrefs.GetBool(kPrefShowContext, true) &&
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

                        if (subtype == "error") info += " | ERROR";
                        info += "]";
                        AddLog(info, 3);
                    });
                    return;
                }

                if (msgType == "assistant")
                {
                    string content = ExtractContentText(json);
                    string toolName = ExtractToolUse(json);

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
                        QueueMainThread(() => AddLog("[tool: " + toolName + "]", 3));
                    }
                    return;
                }

                if (msgType == "error")
                {
                    string errMsg = ExtractJsonString(json, "error");
                    if (string.IsNullOrEmpty(errMsg)) errMsg = json;
                    string captured = errMsg;
                    Debug.LogError(string.Format("{0}   error: {1}", kDbg, captured));
                    QueueMainThread(() => AddLog("Error: " + captured, 2));
                    return;
                }

                if (msgType == "tool_result")
                {
                    bool isError = json.Contains("\"is_error\":true") || json.Contains("\"is_error\": true");
                    Debug.Log(string.Format("{0}   tool_result: is_error={1}", kDbg, isError));

                    if (isError)
                    {
                        string errContent = ExtractContentText(json);
                        if (!string.IsNullOrEmpty(errContent))
                        {
                            string captured = errContent;
                            QueueMainThread(() => AddLog("[tool error] " + captured, 2));
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
                QueueMainThread(() => AddLog("[parse error] " + ex.Message, 2));
            }
        }

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
            QueueMainThread(() => AddLog(text, 0));
        }

        // ------------------------------------------------------------------
        // Process lifecycle
        // ------------------------------------------------------------------
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
                Repaint();
            });
        }

        private void OnDestroy()
        {
            KillProcess();
        }

        private void KillProcess()
        {
            bool wasAlive = _proc != null;
            bool hadExited = true;
            try { hadExited = _proc == null || _proc.HasExited; } catch { }

            Debug.Log(string.Format("{0} KillProcess: proc={1}, hadExited={2}",
                kDbg, wasAlive ? _proc.Id.ToString() : "(null)", hadExited));

            if (_proc != null && !hadExited)
            {
                try { _proc.Kill(); Debug.Log(string.Format("{0} KillProcess: Kill() sent", kDbg)); }
                catch (Exception ex) { Debug.LogWarning(string.Format("{0} KillProcess: Kill() failed: {1}", kDbg, ex.Message)); }
            }
            _proc = null;
            _isRunning = false;
        }

        // ------------------------------------------------------------------
        // Main-thread dispatch
        // ------------------------------------------------------------------
        private void QueueMainThread(Action action)
        {
            lock (_lock)
            {
                _mainThreadQueue.Enqueue(action);
            }
        }

        private void ProcessMainThreadQueue()
        {
            lock (_lock)
            {
                while (_mainThreadQueue.Count > 0)
                {
                    var action = _mainThreadQueue.Dequeue();
                    try { action(); } catch (Exception ex) { Debug.LogException(ex); }
                }
            }
            // Keep repainting while a process is active so streaming text appears
            if (_isRunning)
                Repaint();
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------
        private void AddLog(string text, int kind)
        {
            _log.Add(new LogEntry { text = text, kind = kind });
            // Cap log size
            while (_log.Count > 2000)
                _log.RemoveAt(0);

            RebuildLogText();
            Repaint();
        }

        private void RebuildLogText()
        {
            var sb = new StringBuilder(_log.Count * 80);
            for (int i = 0; i < _log.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(_log[i].text);
            }
            _logText = sb.ToString();
        }

        private static string QuoteArg(string arg)
        {
            // Simple quoting for process arguments
            if (arg.Contains("\""))
                arg = arg.Replace("\"", "\\\"");
            return "\"" + arg + "\"";
        }

        // Minimal JSON string extraction - finds "key":"value" pairs.
        // Does not handle nested objects as values, only simple strings and numbers.
        private static string ExtractJsonString(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return null;

            int colonIdx = json.IndexOf(':', idx + pattern.Length);
            if (colonIdx < 0) return null;

            // Skip whitespace
            int valStart = colonIdx + 1;
            while (valStart < json.Length && json[valStart] == ' ') valStart++;
            if (valStart >= json.Length) return null;

            char c = json[valStart];
            if (c == '"')
            {
                // String value
                int strStart = valStart + 1;
                int strEnd = strStart;
                while (strEnd < json.Length)
                {
                    if (json[strEnd] == '\\') { strEnd += 2; continue; }
                    if (json[strEnd] == '"') break;
                    strEnd++;
                }
                return json.Substring(strStart, strEnd - strStart);
            }
            else if (c == 'n') // null
            {
                return null;
            }
            else
            {
                // Number or boolean
                int numEnd = valStart;
                while (numEnd < json.Length && json[numEnd] != ',' && json[numEnd] != '}' && json[numEnd] != ']' && json[numEnd] != ' ')
                    numEnd++;
                return json.Substring(valStart, numEnd - valStart);
            }
        }

        // Extract text content from assistant messages.
        // Looks for "type":"text" blocks within the content array and concatenates their "text" fields.
        private static string ExtractContentText(string json)
        {
            var sb = new StringBuilder();
            int searchFrom = 0;
            while (true)
            {
                // Find text blocks in content
                int typeTextIdx = json.IndexOf("\"type\":\"text\"", searchFrom, StringComparison.Ordinal);
                if (typeTextIdx < 0)
                    typeTextIdx = json.IndexOf("\"type\": \"text\"", searchFrom, StringComparison.Ordinal);
                if (typeTextIdx < 0) break;

                // Find the "text" field near this type marker
                int textFieldIdx = json.IndexOf("\"text\"", typeTextIdx + 10, StringComparison.Ordinal);
                if (textFieldIdx < 0) break;

                // Make sure we haven't jumped into a different block
                // (check there's no }, { between our type marker and text field indicating a new object)
                string between = json.Substring(typeTextIdx + 10, textFieldIdx - typeTextIdx - 10);
                if (between.Contains("},{"))
                {
                    searchFrom = typeTextIdx + 10;
                    continue;
                }

                string val = ExtractJsonString(json.Substring(typeTextIdx), "text");
                if (val != null)
                {
                    // Unescape common JSON escapes
                    val = val.Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\"", "\"").Replace("\\\\", "\\");
                    sb.Append(val);
                }
                searchFrom = textFieldIdx + 6;
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        // Extract tool name from tool_use blocks
        private static string ExtractToolUse(string json)
        {
            int idx = json.IndexOf("\"type\":\"tool_use\"", StringComparison.Ordinal);
            if (idx < 0)
                idx = json.IndexOf("\"type\": \"tool_use\"", StringComparison.Ordinal);
            if (idx < 0) return null;

            return ExtractJsonString(json.Substring(idx), "name");
        }

        // ------------------------------------------------------------------
        // Settings dialogs
        // ------------------------------------------------------------------
        private void ShowCLIPathDialog()
        {
            string result = EditorUtility.OpenFilePanel("Select Claude CLI executable", "", "");
            if (!string.IsNullOrEmpty(result))
            {
                _claudePath = result;
                AddLog("CLI path set to: " + _claudePath, 3);
            }
        }

        private void ShowMaxTurnsDialog()
        {
            // Simple inline prompt - just toggle through values
            var menu = new GenericMenu();
            int[] options = { 5, 10, 15, 25, 50, 100 };
            foreach (int opt in options)
            {
                int val = opt;
                menu.AddItem(new GUIContent(val.ToString()), _maxTurns == val, () =>
                {
                    _maxTurns = val;
                    AddLog("Max turns set to: " + val, 3);
                });
            }
            menu.ShowAsContext();
        }

        // ------------------------------------------------------------------
        // Lifecycle - domain reload resilience
        // ------------------------------------------------------------------
        private void OnEnable()
        {
            EditorApplication.update += EditorUpdate;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;

            // Check if we were interrupted by a domain reload
            if (EditorPrefs.GetBool(kPrefInterrupted, false))
            {
                EditorPrefs.DeleteKey(kPrefInterrupted);
                string savedSession = EditorPrefs.GetString(kPrefInterruptedSession, "");
                EditorPrefs.DeleteKey(kPrefInterruptedSession);

                Debug.Log(string.Format("{0} OnEnable: RELOAD RECOVERY - savedSession={1}, serializedSession={2}",
                    kDbg, string.IsNullOrEmpty(savedSession) ? "(empty)" : savedSession,
                    string.IsNullOrEmpty(_sessionId) ? "(empty)" : _sessionId));

                // Restore session id if we lost it during reload
                if (!string.IsNullOrEmpty(savedSession) && string.IsNullOrEmpty(_sessionId))
                {
                    _sessionId = savedSession;
                    Debug.Log(string.Format("{0} OnEnable: restored _sessionId from EditorPrefs", kDbg));
                }

                // Clean up stale runtime state from before reload
                _isRunning = false;
                _proc = null;

                AddLog("[session interrupted by recompile/domain reload]", 2);

                if (EditorPrefs.GetBool(kPrefAutoResume, true) && !string.IsNullOrEmpty(_sessionId))
                {
                    AddLog("[auto-resuming session...]", 3);
                    _pendingAutoResume = true;
                    Debug.Log(string.Format("{0} OnEnable: auto-resume queued for session {1}", kDbg, _sessionId));
                }
                else
                {
                    Debug.Log(string.Format("{0} OnEnable: NOT auto-resuming. autoResume={1}, hasSession={2}",
                        kDbg, EditorPrefs.GetBool(kPrefAutoResume, true), !string.IsNullOrEmpty(_sessionId)));
                }
            }
            else
            {
                Debug.Log(string.Format("{0} OnEnable: normal startup (no interrupted flag)", kDbg));
            }
        }

        private void OnDisable()
        {
            EditorApplication.update -= EditorUpdate;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
        }

        private void OnBeforeAssemblyReload()
        {
            Debug.Log(string.Format("{0} OnBeforeAssemblyReload: isRunning={1}, proc={2}, sessionId={3}, completed={4}",
                kDbg, _isRunning,
                _proc != null ? _proc.Id.ToString() : "(null)",
                string.IsNullOrEmpty(_sessionId) ? "(empty)" : _sessionId,
                _sessionCompleted));

            if (!_isRunning && _proc == null) return;

            // If the session already completed (got a "result" message),
            // don't treat this as an interruption. Just clean up.
            if (_sessionCompleted)
            {
                Debug.Log(string.Format("{0} OnBeforeAssemblyReload: session already completed, just cleaning up (no auto-resume)", kDbg));
                KillProcess();
                return;
            }

            // Persist enough state to resume after reload
            EditorPrefs.SetBool(kPrefInterrupted, true);
            if (!string.IsNullOrEmpty(_sessionId))
            {
                EditorPrefs.SetString(kPrefInterruptedSession, _sessionId);
                Debug.Log(string.Format("{0} OnBeforeAssemblyReload: saved sessionId={1} to EditorPrefs", kDbg, _sessionId));
            }
            else
            {
                Debug.LogWarning(string.Format("{0} OnBeforeAssemblyReload: NO SESSION ID to save!", kDbg));
            }

            // Kill cleanly before Unity aborts our threads
            KillProcess();
        }

        private void EditorUpdate()
        {
            if (_isRunning || _mainThreadQueue.Count > 0)
                Repaint();

            // Auto-resume after domain reload (deferred one frame so GUI is ready)
            if (_pendingAutoResume)
            {
                _pendingAutoResume = false;
                Debug.Log(string.Format("{0} EditorUpdate: firing auto-resume for session {1}", kDbg, _sessionId));
                if (!string.IsNullOrEmpty(_sessionId))
                {
                    SendPrompt("The Unity editor just performed a domain reload (script recompilation) which interrupted your previous action. Please check the current state and continue where you left off.");
                }
            }

            // Orphan detection - safety net for cases where beforeAssemblyReload
            // didn't fire or something else killed the process unexpectedly
            if (_isRunning && _proc != null)
            {
                bool procDead;
                try { procDead = _proc.HasExited; }
                catch { procDead = true; }

                bool threadDead = _readerThread == null || !_readerThread.IsAlive;

                if (procDead && threadDead)
                {
                    Debug.LogWarning(string.Format("{0} [ORPHAN] proc and reader dead but isRunning=true, cleaning up", kDbg));
                    _proc = null;
                    _isRunning = false;
                    AddLog("[session lost unexpectedly - process died]", 2);
                    return;
                }
            }

            // Heartbeat + timeout while running
            if (_isRunning && _lastOutputTime > 0)
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
                int timeout = EditorPrefs.GetInt(kPrefTimeout, kDefaultTimeout);
                if (timeout > 0 && silentFor >= timeout)
                {
                    Debug.LogError(string.Format("{0} TIMEOUT: {1:F1}s (limit={2}s), killing",
                        kDbg, silentFor, timeout));
                    KillProcess();
                    AddLog(string.Format("[timed out after {0}s with no output - killed]", timeout), 2);
                }
            }
        }

        private void ShowTimeoutDialog()
        {
            int current = EditorPrefs.GetInt(kPrefTimeout, kDefaultTimeout);
            TimeoutInputWindow.Show(current, (val) =>
            {
                EditorPrefs.SetInt(kPrefTimeout, val);
                AddLog("Timeout set to: " + (val == 0 ? "disabled" : val + "s"), 3);
                Repaint();
            });
        }
    }

    public class TimeoutInputWindow : EditorWindow
    {
        private string _value;
        private Action<int> _callback;
        private bool _focusSet;

        public static void Show(int current, Action<int> callback)
        {
            var w = CreateInstance<TimeoutInputWindow>();
            w.titleContent = new GUIContent("Set Timeout");
            w._value = current.ToString();
            w._callback = callback;
            w._focusSet = false;
            w.minSize = new Vector2(260, 80);
            w.maxSize = new Vector2(260, 80);
            w.ShowUtility();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Seconds of inactivity (0 = disabled):");
            GUI.SetNextControlName("TimeoutField");
            _value = EditorGUILayout.TextField(_value);

            if (!_focusSet)
            {
                EditorGUI.FocusTextInControl("TimeoutField");
                _focusSet = true;
            }

            bool enterPressed = Event.current.type == EventType.KeyUp &&
                                (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            bool ok = GUILayout.Button("OK", GUILayout.Width(60)) || enterPressed;
            bool cancel = GUILayout.Button("Cancel", GUILayout.Width(60));
            EditorGUILayout.EndHorizontal();

            if (ok)
            {
                int result;
                if (int.TryParse(_value, out result) && result >= 0)
                {
                    _callback?.Invoke(result);
                    Close();
                }
            }
            else if (cancel)
            {
                Close();
            }
        }
    }
}
