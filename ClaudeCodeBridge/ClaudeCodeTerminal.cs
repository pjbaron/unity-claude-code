// ClaudeCodeTerminal.cs (ClaudeCodeBridge version)
// Slimmed EditorWindow shell. Process management is delegated to ClaudeCodeProcess.
// Plan & Execute support is delegated to ClaudeCodePlanner.
// Part of the ClaudeCodeBridge refactor of the original ClaudeCodeTerminal.cs.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
        [SerializeField] private string _logText = "";
        [SerializeField] private bool _planMode;

        // ------------------------------------------------------------------
        // Runtime (not serialised)
        // ------------------------------------------------------------------
        private ClaudeCodeProcess _process;
        private ClaudeCodePlanner _planner;
        private bool _pendingPlanStep;
        private bool _scrollToBottom;
        private GUIStyle _logAreaStyle;
        private bool _stylesReady;

        // Debug prefix
        private const string kDbg = "[CCT-DBG]";

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
            if (!_process.IsRunning)
            {
                status = "  IDLE";
                col = Color.green;
            }
            else
            {
                switch (_process.ActivityState)
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
            if (_process.IsRunning && _process.ActivityState == 2 && !string.IsNullOrEmpty(_process.ActiveToolName))
            {
                string shortTool = _process.ActiveToolName;
                // Strip mcp__unityMCP__ prefix for readability
                if (shortTool.StartsWith("mcp__unityMCP__"))
                    shortTool = shortTool.Substring(15);
                GUILayout.Label(shortTool, EditorStyles.toolbarButton, GUILayout.MaxWidth(160));
            }

            // Plan progress label
            if (_planner != null && _planMode &&
                (_planner.State == PlannerState.ExecutingTask || _planner.State == PlannerState.WaitingForPlan || _planner.State == PlannerState.Paused))
            {
                string planLabel;
                if (_planner.State == PlannerState.WaitingForPlan)
                    planLabel = "  PLAN (writing...)";
                else if (_planner.State == PlannerState.Paused)
                    planLabel = string.Format("  PLAN {0}/{1} (paused)", _planner.CurrentTaskIndex, _planner.TotalTasks);
                else
                    planLabel = string.Format("  PLAN {0}/{1}", _planner.CurrentTaskIndex, _planner.TotalTasks);

                var oldBg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.0f, 0.7f, 0.7f); // teal
                GUILayout.Label(planLabel, EditorStyles.toolbarButton, GUILayout.Width(140));
                GUI.backgroundColor = oldBg;
            }

            // Pause/Resume/Cancel for plan execution
            if (_planner != null && _planMode &&
                (_planner.State == PlannerState.ExecutingTask || _planner.State == PlannerState.Paused))
            {
                if (_planner.State == PlannerState.ExecutingTask)
                {
                    if (GUILayout.Button("Pause", EditorStyles.toolbarButton, GUILayout.Width(48)))
                        _planner.Pause();
                }
                else
                {
                    if (GUILayout.Button("Resume", EditorStyles.toolbarButton, GUILayout.Width(56)))
                    {
                        string resumePrompt = _planner.Resume();
                        if (resumePrompt != null)
                        {
                            _sessionId = "";
                            SendPrompt(resumePrompt);
                        }
                    }
                }

                var oldBgCancel = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.9f, 0.3f, 0.3f);
                if (GUILayout.Button("Cancel", EditorStyles.toolbarButton, GUILayout.Width(52)))
                {
                    _process.Kill();
                    _planner.Cancel();
                    _planMode = false;
                    _pendingPlanStep = false;
                    AddLog("[plan] Cancelled by user.", 3);
                }
                GUI.backgroundColor = oldBgCancel;
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
                menu.AddItem(new GUIContent("Summary/Show Cost"), EditorPrefs.GetBool(ClaudeCodeSettings.kPrefShowCost, false),
                    () => EditorPrefs.SetBool(ClaudeCodeSettings.kPrefShowCost, !EditorPrefs.GetBool(ClaudeCodeSettings.kPrefShowCost, false)));
                menu.AddItem(new GUIContent("Summary/Show Turns"), EditorPrefs.GetBool(ClaudeCodeSettings.kPrefShowTurns, true),
                    () => EditorPrefs.SetBool(ClaudeCodeSettings.kPrefShowTurns, !EditorPrefs.GetBool(ClaudeCodeSettings.kPrefShowTurns, true)));
                menu.AddItem(new GUIContent("Summary/Show Context"), EditorPrefs.GetBool(ClaudeCodeSettings.kPrefShowContext, true),
                    () => EditorPrefs.SetBool(ClaudeCodeSettings.kPrefShowContext, !EditorPrefs.GetBool(ClaudeCodeSettings.kPrefShowContext, true)));
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Auto-resume after recompile"), EditorPrefs.GetBool(ClaudeCodeSettings.kPrefAutoResume, true),
                    () => EditorPrefs.SetBool(ClaudeCodeSettings.kPrefAutoResume, !EditorPrefs.GetBool(ClaudeCodeSettings.kPrefAutoResume, true)));
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Plan/Import Plan..."), false, ImportPlan);
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Git/Show Status"), false, () =>
                {
                    string p = Path.GetDirectoryName(Application.dataPath);
                    if (ClaudeCodeGit.IsGitRepo(p))
                    {
                        string s = ClaudeCodeGit.Status(p);
                        AddLog("[git] " + (string.IsNullOrEmpty(s) ? "(clean)" : s), 3);
                    }
                    else
                    {
                        AddLog("[git] no git repository found", 2);
                    }
                });
                menu.AddItem(new GUIContent("Git/Show Recent Commits"), false, () =>
                {
                    string p = Path.GetDirectoryName(Application.dataPath);
                    if (!ClaudeCodeGit.IsGitRepo(p))
                    {
                        AddLog("[git] no git repository found", 2);
                        return;
                    }
                    var commits = ClaudeCodeGit.Log(p, 10);
                    if (commits.Count == 0)
                    {
                        AddLog("[git] no commits found", 3);
                        return;
                    }
                    AddLog("[git] recent commits:", 3);
                    foreach (var c in commits)
                        AddLog(string.Format("  {0} {1} ({2})", c.Hash, c.Message, c.Date), 3);
                });
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
            float areaHeight = position.height - 22f - CalcInputHeight() - 6f;
            if (areaHeight < 50) areaHeight = 50;

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos, GUILayout.Height(areaHeight));

            // Draw a selectable, non-editable text block using SelectableLabel
            // which supports mouse wheel scrolling and partial text selection
            Rect textRect = GUILayoutUtility.GetRect(viewWidth, Mathf.Max(contentHeight, areaHeight), style);
            EditorGUI.SelectableLabel(textRect, _logText, style);

            EditorGUILayout.EndScrollView();

            // Snap to bottom only when new content has been added, not every repaint
            if (_autoScroll && _scrollToBottom && Event.current.type == EventType.Repaint)
            {
                _scrollToBottom = false;
                _scrollPos.y = float.MaxValue;
            }
        }

        private void DrawInput()
        {
            // Intercept Ctrl+Enter before the TextArea consumes it
            bool ctrlEnter = false;
            if (!_process.IsRunning &&
                Event.current.type == EventType.KeyDown &&
                (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter) &&
                (Event.current.control || Event.current.command) &&
                GUI.GetNameOfFocusedControl() == "ClaudeInput")
            {
                ctrlEnter = true;
                Event.current.Use();
            }

            float inputH = CalcInputHeight();

            EditorGUILayout.BeginHorizontal();

            GUI.SetNextControlName("ClaudeInput");
            _inputText = EditorGUILayout.TextArea(_inputText, GUILayout.ExpandWidth(true), GUILayout.Height(inputH));

            // Button column aligned to the bottom of the text area
            EditorGUILayout.BeginVertical(GUILayout.Width(136));
            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginHorizontal();
            var oldBg = GUI.backgroundColor;

            if (_process.IsRunning)
            {
                GUI.backgroundColor = new Color(0.9f, 0.3f, 0.3f);
                if (GUILayout.Button("Cancel", GUILayout.Width(56)))
                {
                    _process.Kill();
                    if (_planMode && _planner != null)
                        _planner.Cancel();
                    _planMode = false;
                    _pendingPlanStep = false;
                    AddLog("[cancelled by user]", 3);
                }
                GUI.backgroundColor = oldBg;
            }
            else
            {
                bool submit = GUILayout.Button("Begin", GUILayout.Width(52)) || ctrlEnter;

                if (submit && !string.IsNullOrWhiteSpace(_inputText))
                {
                    string userPrompt = _inputText.Trim();

                    // Detect "!plan" prefix: activates plan mode and strips the prefix
                    if (userPrompt.StartsWith("!plan", StringComparison.Ordinal))
                    {
                        _planMode = true;
                        userPrompt = userPrompt.Substring(5).Trim();
                    }

                    if (_planMode && _planner.State == PlannerState.Idle)
                    {
                        string projectPath = Path.GetDirectoryName(Application.dataPath);
                        _planner.Init(projectPath);
                        string planPrompt = _planner.CreatePlanningPrompt(userPrompt);
                        AddLog("[plan] Planning: " + userPrompt, 3);
                        _sessionId = "";
                        int timeout = EditorPrefs.GetInt(ClaudeCodeSettings.kPrefTimeout, 600);
                        _process.Start(_claudePath, projectPath, planPrompt, _sessionId, _maxTurns, timeout);
                        _inputText = "";
                        GUI.FocusControl("ClaudeInput");
                    }
                    else
                    {
                        SendPrompt(userPrompt);
                        _inputText = "";
                        GUI.FocusControl("ClaudeInput");
                    }
                }
            }

            // Mode dropdown: Command / Plan
            int modeIndex = _planMode ? 1 : 0;
            GUI.enabled = !_process.IsRunning;
            int newModeIndex = EditorGUILayout.Popup(modeIndex, new string[] { "Command", "Plan" }, GUILayout.Width(76));
            GUI.enabled = true;
            if (newModeIndex != modeIndex)
            {
                _planMode = newModeIndex == 1;
                if (!_planMode && _planner != null)
                    _planner.Cancel();
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        private float CalcInputHeight()
        {
            float lineH = EditorGUIUtility.singleLineHeight + 2f;
            float minH = lineH + 4f;
            float maxH = lineH * 5f + 4f;
            if (string.IsNullOrEmpty(_inputText))
                return minH;
            float w = Mathf.Max(50f, EditorGUIUtility.currentViewWidth - 160f);
            float h = EditorStyles.textArea.CalcHeight(new GUIContent(_inputText), w);
            return Mathf.Clamp(h, minH, maxH);
        }

        // ------------------------------------------------------------------
        // Core: launch Claude Code process
        // ------------------------------------------------------------------
        private void SendPrompt(string prompt)
        {
            AddLog("> " + prompt, 1);
            string projectPath = Path.GetDirectoryName(Application.dataPath);
            int timeout = EditorPrefs.GetInt(ClaudeCodeSettings.kPrefTimeout, 600);
            _process.Start(_claudePath, projectPath, prompt, _sessionId, _maxTurns, timeout);
        }

        // ------------------------------------------------------------------
        // Event handlers from ClaudeCodeProcess
        // ------------------------------------------------------------------
        private void OnProcessLogEntry(string text, LogType logType)
        {
            int kind = logType == LogType.Error ? 2 : 0;
            AddLog(text, kind);
        }

        private void OnProcessCompleted(string sessionId)
        {
            _sessionId = sessionId;
            // Signal planner step if plan mode is active
            if (_planMode && _planner != null && _planner.State != PlannerState.Idle)
                _pendingPlanStep = true;
        }

        private void OnProcessDiedHandler()
        {
            Repaint();
        }

        // ------------------------------------------------------------------
        // Event handler from ClaudeCodePlanner
        // ------------------------------------------------------------------
        private void OnPlannerLogEntry(string text, LogType logType)
        {
            int kind = logType == LogType.Error ? 2 : 3;
            AddLog(text, kind);
        }

        private void OnProcessErrorDuringExecution()
        {
            if (_planMode && _planner != null &&
                _planner.State == PlannerState.ExecutingTask)
            {
                _planner.Pause();
                AddLog("[plan] Paused: error detected during task execution.", 2);
            }
        }

        // ------------------------------------------------------------------
        // Lifecycle - domain reload resilience
        // ------------------------------------------------------------------
        private void OnEnable()
        {
            if (_process == null)
                _process = new ClaudeCodeProcess();

            _process.OnLogEntry += OnProcessLogEntry;
            _process.OnCompleted += OnProcessCompleted;
            _process.OnProcessDied += OnProcessDiedHandler;
            _process.OnErrorDuringExecution += OnProcessErrorDuringExecution;

            if (_planner == null)
                _planner = new ClaudeCodePlanner();
            _planner.OnLogEntry += OnPlannerLogEntry;
            Debug.Log("Planner wired into terminal OK");

            EditorApplication.update += EditorUpdate;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;

            bool wasInterrupted = _process.RestoreAfterReload();
            if (wasInterrupted && !string.IsNullOrEmpty(_process.SessionId))
                _sessionId = _process.SessionId;

            if (_planner.RestoreAfterReload())
                _planMode = true;
        }

        private void OnDisable()
        {
            EditorApplication.update -= EditorUpdate;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;

            if (_process != null)
            {
                _process.OnLogEntry -= OnProcessLogEntry;
                _process.OnCompleted -= OnProcessCompleted;
                _process.OnProcessDied -= OnProcessDiedHandler;
                _process.OnErrorDuringExecution -= OnProcessErrorDuringExecution;
            }

            if (_planner != null)
            {
                _planner.OnLogEntry -= OnPlannerLogEntry;
                _planner.Cancel();
            }
        }

        private void OnDestroy()
        {
            if (_process != null)
                _process.Kill();
        }

        private void OnBeforeAssemblyReload()
        {
            _process.SaveStateForReload();
            _planner?.SaveStateForReload();
        }

        private void EditorUpdate()
        {
            _process.DrainMainThreadQueue();

            if (_process.IsRunning || _process.MainThreadQueueCount > 0)
                Repaint();

            // Auto-resume after domain reload (deferred one frame so GUI is ready)
            if (_process.PendingAutoResume)
            {
                _process.PendingAutoResume = false;
                _sessionId = _process.SessionId;
                Debug.Log(string.Format("{0} EditorUpdate: firing auto-resume for session {1}", kDbg, _sessionId));
                if (!string.IsNullOrEmpty(_sessionId))
                {
                    SendPrompt("The Unity editor just performed a domain reload (script recompilation) which interrupted your previous action. Please check the current state and continue where you left off.");
                }
            }

            // Planner auto-dispatch: start next task when current process finishes
            if (_pendingPlanStep && !_process.IsRunning)
            {
                _pendingPlanStep = false;
                string nextPrompt = _planner.OnTaskCompleted();
                if (nextPrompt != null)
                {
                    // Each task runs in a new session
                    _sessionId = "";
                    SendPrompt(nextPrompt);
                }
                else if (_planner.State == PlannerState.Idle)
                {
                    // Plan completed or aborted
                    _planMode = false;
                    Repaint();
                }
            }

            _process.UpdateProcessState();
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
            _scrollToBottom = true;
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

        // ------------------------------------------------------------------
        // Settings dialogs
        // ------------------------------------------------------------------
        private void ImportPlan()
        {
            string sourcePath = EditorUtility.OpenFilePanel("Import Plan File", "", "md,txt");
            if (string.IsNullOrEmpty(sourcePath)) return;

            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string destPath = Path.Combine(projectRoot, "CCT_PLAN.md");

            try
            {
                File.Copy(sourcePath, destPath, overwrite: true);
            }
            catch (Exception ex)
            {
                AddLog("[plan] Failed to copy plan file: " + ex.Message, 2);
                return;
            }

            _planner.Init(projectRoot);
            if (!_planner.TryParsePlan(destPath))
            {
                AddLog("[plan] Failed to parse plan. Ensure tasks use '### Task N: title' headers.", 2);
                return;
            }

            string firstPrompt = _planner.StartFromParsedPlan();
            if (firstPrompt == null)
            {
                AddLog("[plan] Plan parsed but contained no tasks.", 2);
                return;
            }

            _planMode = true;
            _sessionId = "";
            SendPrompt(firstPrompt);
        }

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

        private void ShowTimeoutDialog()
        {
            int current = EditorPrefs.GetInt(ClaudeCodeSettings.kPrefTimeout, 600);
            TimeoutInputWindow.Show(current, (val) =>
            {
                EditorPrefs.SetInt(ClaudeCodeSettings.kPrefTimeout, val);
                AddLog("Timeout set to: " + (val == 0 ? "disabled" : val + "s"), 3);
                Repaint();
            });
        }
    }
}
