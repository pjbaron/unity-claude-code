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

        private const string kPrefTimeout = "ClaudeCodeTerminal_TimeoutSec";
        private const int kDefaultTimeout = 120;

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
            string status = _isRunning ? "  WORKING..." : "  IDLE";
            Color col = _isRunning ? Color.yellow : Color.green;
            var oldCol = GUI.contentColor;
            GUI.contentColor = col;
            GUILayout.Label(status, EditorStyles.toolbarButton, GUILayout.Width(100));
            GUI.contentColor = oldCol;

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

            // Inherit environment (picks up PATH, ANTHROPIC_API_KEY, etc.)
            // Ensure MCP config is found from project root
            try
            {
                _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _proc.Exited += OnProcessExited;
                _proc.Start();
                _isRunning = true;
                _lastOutputTime = EditorApplication.timeSinceStartup;
                _currentAssistantMsg.Clear();

                _readerThread = new Thread(ReadOutputStream) { IsBackground = true };
                _readerThread.Start();

                // Capture stderr on another thread
                var errThread = new Thread(ReadErrorStream) { IsBackground = true };
                errThread.Start();
            }
            catch (Exception ex)
            {
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
            try
            {
                using (var reader = _proc.StandardOutput)
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        ProcessStreamLine(line);
                    }
                }
            }
            catch (Exception ex)
            {
                QueueMainThread(() => AddLog("Read error: " + ex.Message, 2));
            }
        }

        private void ReadErrorStream()
        {
            try
            {
                using (var reader = _proc.StandardError)
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        _lastOutputTime = EditorApplication.timeSinceStartup;
                        string captured = line;
                        QueueMainThread(() => AddLog("[stderr] " + captured, 2));
                    }
                }
            }
            catch { /* swallow */ }
        }

        // ------------------------------------------------------------------
        // Parse stream-json lines
        // ------------------------------------------------------------------
        private void ProcessStreamLine(string json)
        {
            // Any output from the process resets the timeout clock
            _lastOutputTime = EditorApplication.timeSinceStartup;

            // stream-json emits newline-delimited JSON objects.
            // Key message types:
            //   {"type":"init", ...}                        - session start
            //   {"type":"assistant","message":{...}}        - assistant content
            //   {"type":"result","subtype":"success",...}   - final result with session_id
            //   {"type":"error",...}                        - error

            try
            {
                // Lightweight JSON field extraction without pulling in a JSON library.
                // Unity ships with JsonUtility but it can't handle arbitrary JSON well.
                // We do simple string matching -- robust enough for the known schema.

                string msgType = ExtractJsonString(json, "type");

                if (msgType == "init")
                {
                    string sid = ExtractJsonString(json, "session_id");
                    if (!string.IsNullOrEmpty(sid))
                    {
                        QueueMainThread(() =>
                        {
                            _sessionId = sid;
                            AddLog("[session: " + sid.Substring(0, Mathf.Min(8, sid.Length)) + "...]", 3);
                        });
                    }
                    return;
                }

                if (msgType == "result")
                {
                    // Flush any accumulated assistant text
                    FlushAssistantBuffer();

                    string sid = ExtractJsonString(json, "session_id");
                    string cost = ExtractJsonString(json, "total_cost_usd");
                    string turns = ExtractJsonString(json, "num_turns");
                    string subtype = ExtractJsonString(json, "subtype");

                    // Context window usage - available in the result JSON under usage
                    string inputTokens = ExtractJsonString(json, "input_tokens");
                    string outputTokens = ExtractJsonString(json, "output_tokens");

                    QueueMainThread(() =>
                    {
                        if (!string.IsNullOrEmpty(sid))
                            _sessionId = sid;

                        string info = "[done";

                        if (EditorPrefs.GetBool(kPrefShowCost, false) && !string.IsNullOrEmpty(cost))
                            info += " | cost: $" + cost;

                        if (EditorPrefs.GetBool(kPrefShowTurns, true) && !string.IsNullOrEmpty(turns))
                            info += " | turns: " + turns;

                        // Report context usage if token counts are available
                        if (EditorPrefs.GetBool(kPrefShowContext, true) &&
                            !string.IsNullOrEmpty(inputTokens) && !string.IsNullOrEmpty(outputTokens))
                        {
                            long inTok, outTok;
                            if (long.TryParse(inputTokens, out inTok) && long.TryParse(outputTokens, out outTok))
                            {
                                long used = inTok + outTok;
                                // Claude Code models: Sonnet/Opus = 200k context window
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
                    // Extract text content from the nested message.
                    // The content array has blocks; we want type=text blocks.
                    // Also detect tool_use blocks to show what Claude Code is doing.
                    string content = ExtractContentText(json);
                    if (!string.IsNullOrEmpty(content))
                    {
                        lock (_lock)
                        {
                            _currentAssistantMsg.Append(content);
                        }
                        // Flush periodically so the user sees streaming output
                        FlushAssistantBuffer();
                    }

                    // Show tool use as status
                    string toolName = ExtractToolUse(json);
                    if (!string.IsNullOrEmpty(toolName))
                    {
                        QueueMainThread(() => AddLog("[tool: " + toolName + "]", 3));
                    }
                    return;
                }

                if (msgType == "error")
                {
                    string errMsg = ExtractJsonString(json, "error");
                    if (string.IsNullOrEmpty(errMsg)) errMsg = json;
                    string captured = errMsg;
                    QueueMainThread(() => AddLog("Error: " + captured, 2));
                    return;
                }

                // For other message types (user, tool_result, etc.) we mostly ignore,
                // but show tool_result errors if present.
                if (msgType == "tool_result")
                {
                    if (json.Contains("\"is_error\":true") || json.Contains("\"is_error\": true"))
                    {
                        string errContent = ExtractContentText(json);
                        if (!string.IsNullOrEmpty(errContent))
                        {
                            string captured = errContent;
                            QueueMainThread(() => AddLog("[tool error] " + captured, 2));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
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
            QueueMainThread(() => AddLog(text, 0));
        }

        // ------------------------------------------------------------------
        // Process lifecycle
        // ------------------------------------------------------------------
        private void OnProcessExited(object sender, EventArgs e)
        {
            FlushAssistantBuffer();
            QueueMainThread(() =>
            {
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
            if (_proc != null && !_proc.HasExited)
            {
                try { _proc.Kill(); } catch { /* best effort */ }
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
        // Periodic update to pump the main-thread queue even when
        // OnGUI isn't firing (e.g. window not focused)
        // ------------------------------------------------------------------
        private void OnEnable()
        {
            EditorApplication.update += EditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= EditorUpdate;
        }

        private void EditorUpdate()
        {
            if (_isRunning || _mainThreadQueue.Count > 0)
                Repaint();

            // Timeout check
            if (_isRunning && _lastOutputTime > 0)
            {
                int timeout = EditorPrefs.GetInt(kPrefTimeout, kDefaultTimeout);
                if (timeout > 0)
                {
                    double elapsed = EditorApplication.timeSinceStartup - _lastOutputTime;
                    if (elapsed >= timeout)
                    {
                        KillProcess();
                        AddLog(string.Format("[timed out after {0}s with no output - killed]", timeout), 2);
                    }
                }
            }
        }

        private void ShowTimeoutDialog()
        {
            int current = EditorPrefs.GetInt(kPrefTimeout, kDefaultTimeout);
            var menu = new GenericMenu();
            int[] options = { 0, 30, 60, 120, 180, 300 };
            foreach (int opt in options)
            {
                int val = opt;
                string label = val == 0 ? "Disabled" : val + "s";
                menu.AddItem(new GUIContent(label), current == val, () =>
                {
                    EditorPrefs.SetInt(kPrefTimeout, val);
                    AddLog("Timeout set to: " + (val == 0 ? "disabled" : val + "s"), 3);
                });
            }
            menu.ShowAsContext();
        }
    }
}
