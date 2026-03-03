// ClaudeCodePlanner.cs
// Plan & Execute feature: rewrites user prompts into a planning request,
// parses the resulting plan file, and sequences tasks automatically.
// Part of the ClaudeCodeBridge Phase 2 implementation.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ClaudeCodeBridge
{
    internal enum PlannerState
    {
        Idle,
        WaitingForPlan,
        ExecutingTask,
        Paused,
    }

    internal class PlanTask
    {
        public int Index;           // 1-based
        public string Title;
        public string Description;
    }

    internal class ClaudeCodePlanner
    {
        // ------------------------------------------------------------------
        // EditorPrefs keys for domain reload persistence
        // ------------------------------------------------------------------
        private const string kPrefPlanActive = "CCT_PlannerActive";
        private const string kPrefPlanTaskIndex = "CCT_PlannerTaskIndex";
        private const string kPrefPlanFilePath = "CCT_PlannerFilePath";

        // ------------------------------------------------------------------
        // State
        // ------------------------------------------------------------------
        private PlannerState _state = PlannerState.Idle;
        private List<PlanTask> _tasks = new List<PlanTask>();
        private int _currentTaskIndex; // 0-based index into _tasks
        private string _planFilePath;
        private string _projectRoot;

        // ------------------------------------------------------------------
        // Public properties
        // ------------------------------------------------------------------
        public PlannerState State => _state;
        public string PlanFilePath => _planFilePath;
        public int CurrentTaskIndex => _currentTaskIndex + 1;    // 1-based for display
        public int TotalTasks => _tasks.Count;
        public string CurrentTaskDescription =>
            (_currentTaskIndex >= 0 && _currentTaskIndex < _tasks.Count)
                ? _tasks[_currentTaskIndex].Description
                : "";

        // ------------------------------------------------------------------
        // Events
        // ------------------------------------------------------------------
        /// <summary>Fires log messages; terminal subscribes to route them to AddLog.</summary>
        public event Action<string, LogType> OnLogEntry;

        // ------------------------------------------------------------------
        // Initialise (called once when plan mode is triggered)
        // ------------------------------------------------------------------
        public void Init(string projectRoot)
        {
            _projectRoot = projectRoot;
            _planFilePath = Path.Combine(projectRoot, "CCT_PLAN.md");
        }

        // ------------------------------------------------------------------
        // CreatePlanningPrompt
        // Wraps the user's original prompt with instructions for Claude to
        // write CCT_PLAN.md before doing any implementation work.
        // ------------------------------------------------------------------
        public string CreatePlanningPrompt(string userPrompt)
        {
            _state = PlannerState.WaitingForPlan;
            _tasks.Clear();
            _currentTaskIndex = 0;

            return
                "IMPORTANT: Before doing ANY implementation work, you must first create a\n" +
                "detailed plan document.\n\n" +
                "Write a plan file to CCT_PLAN.md in the project root with this exact format:\n\n" +
                "# Plan: {brief title}\n\n" +
                "## Overview\n" +
                "{1-2 sentence summary of what will be built}\n\n" +
                "## Tasks\n" +
                "### Task 1: {title}\n" +
                "{Description of what to do. Be specific about files to create/modify.}\n\n" +
                "### Task 2: {title}\n" +
                "{Description}\n\n" +
                "... (continue for all tasks needed)\n\n" +
                "Rules for the plan:\n" +
                "- Each task should be completable in a single Claude Code session (under 25 turns)\n" +
                "- Tasks should be ordered so each builds on the previous\n" +
                "- Each task should result in a compilable/runnable state\n" +
                "- Keep tasks focused: one major feature or system per task\n" +
                "- Include a final \"Polish & Test\" task\n\n" +
                "The user's request:\n" +
                userPrompt + "\n\n" +
                "Write ONLY the plan file. Do NOT start implementing anything yet.";
        }

        // ------------------------------------------------------------------
        // TryParsePlan
        // Reads CCT_PLAN.md and extracts ### Task N: headers + descriptions.
        // Returns false if file is missing or no tasks are found.
        // ------------------------------------------------------------------
        public bool TryParsePlan(string planFilePath)
        {
            if (!File.Exists(planFilePath))
            {
                Log("[plan] error: plan file not found at " + planFilePath, LogType.Error);
                return false;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(planFilePath);
            }
            catch (Exception ex)
            {
                Log("[plan] error: failed to read plan file: " + ex.Message, LogType.Error);
                return false;
            }

            _tasks.Clear();
            PlanTask current = null;
            var descLines = new System.Text.StringBuilder();

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                // Match "### Task N: title" (case-insensitive prefix check)
                if (line.StartsWith("### Task ", StringComparison.OrdinalIgnoreCase))
                {
                    // Flush previous task
                    if (current != null)
                    {
                        current.Description = descLines.ToString().Trim();
                        _tasks.Add(current);
                    }

                    // Parse "### Task N: title"
                    // Strip the "### Task " prefix
                    string rest = line.Substring(9).Trim(); // "N: title" or "N. title"
                    int colonIdx = rest.IndexOf(':');
                    int dotIdx = rest.IndexOf('.');
                    int sepIdx = -1;
                    if (colonIdx >= 0 && (dotIdx < 0 || colonIdx < dotIdx)) sepIdx = colonIdx;
                    else if (dotIdx >= 0) sepIdx = dotIdx;

                    string title;
                    int taskNum;
                    if (sepIdx > 0 && int.TryParse(rest.Substring(0, sepIdx).Trim(), out taskNum))
                    {
                        title = rest.Substring(sepIdx + 1).Trim();
                    }
                    else
                    {
                        taskNum = _tasks.Count + 1;
                        title = rest;
                    }

                    current = new PlanTask { Index = taskNum, Title = title };
                    descLines.Clear();
                    continue;
                }

                if (current != null)
                {
                    if (descLines.Length > 0) descLines.Append('\n');
                    descLines.Append(line);
                }
            }

            // Flush last task
            if (current != null)
            {
                current.Description = descLines.ToString().Trim();
                _tasks.Add(current);
            }

            if (_tasks.Count == 0)
            {
                Log("[plan] error: no tasks found in plan file", LogType.Error);
                return false;
            }

            return true;
        }

        // ------------------------------------------------------------------
        // OnTaskCompleted
        // Called by the terminal when a process finishes and plan mode is active.
        // Returns the next prompt to send, or null when done/paused/errored.
        // ------------------------------------------------------------------
        public string OnTaskCompleted()
        {
            if (_state == PlannerState.Paused)
                return null;

            if (_state == PlannerState.WaitingForPlan)
            {
                // The planning prompt just completed; try to parse the plan file.
                if (!TryParsePlan(_planFilePath))
                {
                    _state = PlannerState.Idle;
                    return null;
                }

                // Log the task list
                var sb = new System.Text.StringBuilder("[plan] Found " + _tasks.Count + " task");
                if (_tasks.Count != 1) sb.Append('s');
                sb.Append(": ");
                for (int i = 0; i < _tasks.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(i + 1).Append(". ").Append(_tasks[i].Title);
                }
                Log(sb.ToString(), LogType.Log);

                _currentTaskIndex = 0;
                _state = PlannerState.ExecutingTask;
                return BuildTaskPrompt(_currentTaskIndex);
            }

            if (_state == PlannerState.ExecutingTask)
            {
                int completedIndex = _currentTaskIndex;
                int completedHuman = completedIndex + 1;
                string completedTitle = _tasks[completedIndex].Title;

                Log(string.Format("[plan] Task {0}/{1} complete: {2}",
                    completedHuman, _tasks.Count, completedTitle), LogType.Log);

                // Git auto-commit after each completed task (async to avoid blocking main thread)
                if (!string.IsNullOrEmpty(_projectRoot))
                {
                    string root = _projectRoot;
                    string commitMsg = string.Format("[CCT] task {0}/{1}: {2}",
                        completedHuman, _tasks.Count, completedTitle);
                    new System.Threading.Thread(() =>
                    {
                        try
                        {
                            if (ClaudeCodeGit.IsGitRepo(root) && ClaudeCodeGit.HasChanges(root))
                            {
                                bool ok = ClaudeCodeGit.Commit(root, commitMsg);
                                UnityEngine.Debug.Log(ok
                                    ? "[CCT-git] committed: " + commitMsg
                                    : "[CCT-git] commit failed");
                            }
                        }
                        catch (Exception ex)
                        {
                            UnityEngine.Debug.LogWarning("[CCT-git] exception: " + ex.Message);
                        }
                    }) { IsBackground = true, Name = "CCT-GitCommit" }.Start();
                }

                _currentTaskIndex++;

                if (_currentTaskIndex >= _tasks.Count)
                {
                    Log("[plan] All tasks complete", LogType.Log);
                    _state = PlannerState.Idle;
                    return null;
                }

                Log(string.Format("[plan] Starting task {0}/{1}: {2}",
                    _currentTaskIndex + 1, _tasks.Count, _tasks[_currentTaskIndex].Title), LogType.Log);

                return BuildTaskPrompt(_currentTaskIndex);
            }

            return null;
        }

        // ------------------------------------------------------------------
        // Domain reload persistence
        // ------------------------------------------------------------------

        /// <summary>
        /// Saves planner state to EditorPrefs before a domain reload.
        /// Call from OnBeforeAssemblyReload.
        /// </summary>
        public void SaveStateForReload()
        {
            bool planActive = _state == PlannerState.ExecutingTask ||
                              _state == PlannerState.WaitingForPlan ||
                              _state == PlannerState.Paused;
            EditorPrefs.SetBool(kPrefPlanActive, planActive);
            if (planActive)
            {
                EditorPrefs.SetInt(kPrefPlanTaskIndex, _currentTaskIndex);
                EditorPrefs.SetString(kPrefPlanFilePath, _planFilePath ?? "");
            }
        }

        /// <summary>
        /// Restores planner state from EditorPrefs after a domain reload.
        /// Returns true if a plan was in progress and was successfully restored.
        /// Call from OnEnable after re-subscribing to events.
        /// </summary>
        public bool RestoreAfterReload()
        {
            if (!EditorPrefs.GetBool(kPrefPlanActive, false))
                return false;

            EditorPrefs.DeleteKey(kPrefPlanActive);
            _currentTaskIndex = EditorPrefs.GetInt(kPrefPlanTaskIndex, 0);
            _planFilePath = EditorPrefs.GetString(kPrefPlanFilePath, "");

            EditorPrefs.DeleteKey(kPrefPlanTaskIndex);
            EditorPrefs.DeleteKey(kPrefPlanFilePath);

            if (!string.IsNullOrEmpty(_planFilePath))
                _projectRoot = Path.GetDirectoryName(_planFilePath);

            if (!string.IsNullOrEmpty(_planFilePath) && TryParsePlan(_planFilePath))
            {
                _state = PlannerState.ExecutingTask;
                Log(string.Format("[plan] Restored after domain reload. Resuming at task {0}/{1}.",
                    _currentTaskIndex + 1, _tasks.Count), LogType.Log);
                return true;
            }

            Log("[plan] Could not restore plan after domain reload (file missing or unreadable).", LogType.Warning);
            _state = PlannerState.Idle;
            return false;
        }

        // ------------------------------------------------------------------
        // StartFromParsedPlan
        // Called after manually loading and parsing an existing plan file,
        // bypassing the WaitingForPlan phase entirely.
        // Returns the first task prompt, or null if no tasks were parsed.
        // ------------------------------------------------------------------
        public string StartFromParsedPlan()
        {
            if (_tasks.Count == 0) return null;

            _currentTaskIndex = 0;
            _state = PlannerState.ExecutingTask;

            var sb = new System.Text.StringBuilder("[plan] Imported ");
            sb.Append(_tasks.Count).Append(" task");
            if (_tasks.Count != 1) sb.Append('s');
            sb.Append(": ");
            for (int i = 0; i < _tasks.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(i + 1).Append(". ").Append(_tasks[i].Title);
            }
            Log(sb.ToString(), LogType.Log);

            Log(string.Format("[plan] Starting task 1/{0}: {1}", _tasks.Count, _tasks[0].Title), LogType.Log);
            return BuildTaskPrompt(0);
        }

        // ------------------------------------------------------------------
        // Pause / Resume / Cancel
        // ------------------------------------------------------------------
        public void Pause()
        {
            if (_state == PlannerState.ExecutingTask)
                _state = PlannerState.Paused;
        }

        /// <summary>
        /// Returns the prompt for the current task (to restart after a pause),
        /// or null if not in a resumable state.
        /// </summary>
        public string Resume()
        {
            if (_state != PlannerState.Paused) return null;
            _state = PlannerState.ExecutingTask;
            if (_currentTaskIndex < _tasks.Count)
                return BuildTaskPrompt(_currentTaskIndex);
            return null;
        }

        public void Cancel()
        {
            _state = PlannerState.Idle;
            _tasks.Clear();
            _currentTaskIndex = 0;
        }

        // ------------------------------------------------------------------
        // Private helpers
        // ------------------------------------------------------------------
        private string BuildTaskPrompt(int index)
        {
            var task = _tasks[index];
            int humanIndex = index + 1;
            int total = _tasks.Count;

            return string.Format(
                "You are executing Task {0} of {1} from the project plan in CCT_PLAN.md.\n\n" +
                "Read CCT_PLAN.md first to understand the full plan, then execute ONLY Task {0}:\n" +
                "### Task {0}: {2}\n" +
                "{3}\n\n" +
                "Rules:\n" +
                "- Complete ONLY this task, do not work on other tasks\n" +
                "- Ensure the project compiles and runs after this task\n" +
                "- If you encounter issues with previous tasks, fix them as part of this task",
                humanIndex, total, task.Title, task.Description);
        }

        private void Log(string text, LogType logType)
        {
            OnLogEntry?.Invoke(text, logType);
        }
    }
}
