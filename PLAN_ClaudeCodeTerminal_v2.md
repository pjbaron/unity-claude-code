# Claude Code Terminal v2 - Plan & Execute Feature
## Architecture Plan for Claude Code Implementation

This document describes the refactoring and new features for the ClaudeCodeTerminal
Unity editor tooling. Claude Code should implement this plan in phases, committing
between each phase. All files go in Assets/Editor/ClaudeCodeBridge/.

IMPORTANT: All .cs files must use the ClaudeCodeBridge namespace.
IMPORTANT: Do NOT use unicode symbols anywhere in source files.
IMPORTANT: Preserve ALL [CCT-DBG] debug logging from ClaudeCodeTerminal.cs.


## Current State

Single file: ClaudeCodeTerminal.cs (~1073 lines) in Assets/Editor/.
It handles everything: GUI, process management, stream-json parsing, domain reload
recovery, settings, heartbeat monitoring, and activity state tracking.

The file works well but is too large for adding plan-execute and git features.


## Target Architecture

```
Assets/Editor/ClaudeCodeBridge/
  ClaudeCodeTerminal.cs    - EditorWindow: GUI, input, log display, toolbar
  ClaudeCodeProcess.cs     - Process lifecycle: launch, kill, stream parsing, 
                             domain reload, heartbeat, callbacks
  ClaudeCodePlanner.cs     - Plan mode: prompt rewriting, plan file parsing,
                             task sequencing, auto-advance loop
  ClaudeCodeGit.cs         - Git helpers: init, commit, log, status, checkout
  ClaudeCodeSettings.cs    - EditorPrefs constants, settings menu builder
  ClaudeCodeHelpers.cs     - Static utilities: QuoteArg, JSON extraction,
                             ExtractContentText, ExtractToolUse
```


## Phase 1: Refactor into Multiple Files (No New Features)

Goal: Split ClaudeCodeTerminal.cs into the architecture above with zero
behaviour change. Everything should work identically after this phase.

### 1a: Create ClaudeCodeHelpers.cs

Extract these static methods:
- QuoteArg(string) -> string
- ExtractJsonString(string json, string key) -> string  
- ExtractContentText(string json) -> string
- ExtractToolUse(string json) -> string

Make them internal static in a static class ClaudeCodeHelpers.
Update ClaudeCodeTerminal.cs to call ClaudeCodeHelpers.ExtractJsonString() etc.

### 1b: Create ClaudeCodeSettings.cs

Extract:
- All EditorPrefs key constants (kPrefTimeout, kPrefShowCost, kPrefShowTurns,
  kPrefShowContext, kPrefAutoResume, kPrefInterrupted, kPrefInterruptedSession)
- Default values (kDefaultTimeout)
- The TimeoutInputWindow class
- Add new constants for plan-mode prefs (kPrefPlanFirst)

Make it a static class for the constants, keep TimeoutInputWindow as its own class
in the same file.

### 1c: Create ClaudeCodeProcess.cs

This is the big extraction. This class manages a single Claude Code CLI process.
It is NOT an EditorWindow. It is a plain C# class.

Extract:
- Process _proc, Thread _readerThread, object _lock, Queue<Action> _mainThreadQueue
- _isRunning, _sessionId, _sessionCompleted, _activityState, _activeToolName
- All debug instrumentation fields and Elapsed()
- SendPrompt(string prompt) -- rename to Start(string prompt, string sessionId)
- ReadOutputStream(), ReadErrorStream()
- ProcessStreamLine(string json)
- FlushAssistantBuffer()
- OnProcessExited()
- KillProcess() -- make public
- ProcessMainThreadQueue() -- make public, called by terminal's OnGUI

Public interface:
```
class ClaudeCodeProcess
{
    // State (read-only properties)
    bool IsRunning { get; }
    bool IsCompleted { get; }
    string SessionId { get; }
    int ActivityState { get; }      // 0=idle, 1=thinking, 2=tool, 3=starting
    string ActiveToolName { get; }

    // Events
    event Action<string, int> OnLogEntry;   // (text, kind) - terminal subscribes
    event Action OnCompleted;                // fires when result message received
    event Action OnProcessDied;              // fires when process exits

    // Control
    void Start(string prompt, string sessionId, string claudePath, int maxTurns);
    void Kill();
    void DrainMainThreadQueue();  // must be called from OnGUI/EditorUpdate

    // Domain reload
    void SaveStateForReload();    // called from OnBeforeAssemblyReload
    bool RestoreAfterReload();    // returns true if there was an interrupted session
    string InterruptedSessionId { get; }
}
```

The terminal subscribes to OnLogEntry to populate its log.
The terminal subscribes to OnCompleted to know when a task finishes.

Domain reload: The process class can't use [SerializeField] (it's not a
UnityEngine.Object). Instead it reads/writes EditorPrefs directly in
SaveStateForReload() and RestoreAfterReload(). The terminal calls these
from its own OnBeforeAssemblyReload and OnEnable hooks.

### 1d: Slim down ClaudeCodeTerminal.cs

What remains:
- EditorWindow subclass with [MenuItem]
- Serialised GUI state: _inputText, _log, _scrollPos, _autoScroll, _logText,
  _claudePath, _maxTurns
- LogEntry class
- OnGUI, DrawToolbar, DrawLog, DrawInput
- OnEnable/OnDisable/OnDestroy (lifecycle, delegates to process class)
- OnBeforeAssemblyReload (delegates to process class)
- EditorUpdate (delegates heartbeat check to process, handles auto-resume)
- AddLog, RebuildLogText
- Settings dialog methods (ShowCLIPathDialog, ShowMaxTurnsDialog, ShowTimeoutDialog)
- EnsureStyles

A private ClaudeCodeProcess _process field manages the actual CLI interaction.
Terminal creates it in OnEnable if null.

### 1e: Verify

After refactoring, test: send a prompt, verify stream parsing works, verify
domain reload recovery works, verify cancel works, verify settings work.
All [CCT-DBG] logging must still appear in Unity console.

COMMIT after Phase 1 with message: "[CCT] refactor: split into multiple files"


## Phase 2: Plan Mode

Goal: Add ability to force Claude to write a plan before executing.

### 2a: Add Plan Mode UI to ClaudeCodeTerminal

In DrawInput(), add a toggle button between the text field and Send button:
- "Plan" button/toggle, visually distinct (e.g. cyan/teal background)
- When active, the button stays highlighted
- State stored in a serialised bool _planMode
- Also detect keyword "!plan" at start of prompt (strip it before sending)

### 2b: Create ClaudeCodePlanner.cs

```
class ClaudeCodePlanner
{
    enum PlannerState { Idle, WaitingForPlan, ExecutingTask, Paused }

    // The planner rewrites the user's prompt to instruct Claude to write a plan
    // file, then parses the plan and executes tasks sequentially.

    string PlanFilePath { get; }      // {projectRoot}/CCT_PLAN.md
    PlannerState State { get; }
    int CurrentTaskIndex { get; }
    int TotalTasks { get; }
    string CurrentTaskDescription { get; }

    // Called by terminal when plan mode is active and user submits prompt
    string CreatePlanningPrompt(string userPrompt);
    
    // Called by terminal when process completes (subscribes to OnCompleted)
    // Returns the next prompt to send, or null if done/paused
    string OnTaskCompleted();
    
    // Parse the plan file and extract numbered tasks
    bool TryParsePlan(string planFilePath);
    
    // Manual control
    void Pause();
    void Resume();    // returns next task prompt
    void Cancel();
}
```

### 2c: Planning Prompt Template

When plan mode is active, CreatePlanningPrompt() wraps the user prompt:

```
IMPORTANT: Before doing ANY implementation work, you must first create a
detailed plan document.

Write a plan file to CCT_PLAN.md in the project root with this exact format:

# Plan: {brief title}

## Overview
{1-2 sentence summary of what will be built}

## Tasks
### Task 1: {title}
{Description of what to do. Be specific about files to create/modify.}

### Task 2: {title}
{Description}

... (continue for all tasks needed)

Rules for the plan:
- Each task should be completable in a single Claude Code session (under 25 turns)
- Tasks should be ordered so each builds on the previous
- Each task should result in a compilable/runnable state
- Keep tasks focused: one major feature or system per task
- Include a final "Polish & Test" task

The user's request:
{original prompt}

Write ONLY the plan file. Do NOT start implementing anything yet.
```

### 2d: Plan File Parser

TryParsePlan() reads CCT_PLAN.md and extracts tasks:
- Look for "### Task N:" headers
- Extract title and description body for each
- Store as List<PlanTask> where PlanTask has: index, title, description

### 2e: Task Execution Loop

When planning prompt completes (OnCompleted fires):
1. Planner checks if CCT_PLAN.md exists
2. Parses it with TryParsePlan()
3. Logs task list to terminal: "[plan] Found N tasks: 1. Title, 2. Title, ..."
4. If git is available, calls ClaudeCodeGit to commit before starting
5. Constructs first task prompt and returns it

Task prompt template:
```
You are executing Task {N} of {Total} from the project plan in CCT_PLAN.md.

Read CCT_PLAN.md first to understand the full plan, then execute ONLY Task {N}:
### Task {N}: {title}
{description}

Rules:
- Complete ONLY this task, do not work on other tasks
- Ensure the project compiles and runs after this task
- If you encounter issues with previous tasks, fix them as part of this task
```

When each task completes:
1. Git auto-commit: "[CCT] task {N}/{Total}: {title}"
2. Log to terminal: "[plan] Task {N}/{Total} complete: {title}"
3. Advance to next task, or finish if all done
4. Log: "[plan] All tasks complete" or "[plan] Starting task {N+1}: {title}"

### 2f: Plan Mode Integration in Terminal

Terminal's EditorUpdate handles the planner loop:
- When process completes and planner is active, call planner.OnTaskCompleted()
- If it returns a prompt, start a new process with it (new session for each task)
- If it returns null, plan is finished

Terminal toolbar shows plan progress when active:
- "PLAN 3/7" or similar, next to the activity indicator

COMMIT: "[CCT] feat: plan mode with task sequencing"


## Phase 3: Git Integration

Goal: Automatic git commits between plan tasks for rollback capability.

### 3a: Create ClaudeCodeGit.cs

```
static class ClaudeCodeGit
{
    // All methods run git via System.Diagnostics.Process synchronously
    // (they're fast operations, fine on main thread)

    static bool IsGitRepo(string projectRoot);
    static bool Init(string projectRoot);    // git init + create .gitignore
    static string Status(string projectRoot); // short status
    static bool HasChanges(string projectRoot);
    static bool Commit(string projectRoot, string message);  // add all + commit
    static List<GitCommit> Log(string projectRoot, int maxCount);
    static bool Checkout(string projectRoot, string commitHash);
    static string CurrentBranch(string projectRoot);
    static bool CreateBranch(string projectRoot, string branchName);
}

class GitCommit
{
    string Hash;        // short hash
    string FullHash;
    string Message;
    string Date;        // ISO format
}
```

### 3b: Git Setup Check

When plan mode starts (before first task):
1. Check IsGitRepo()
2. If no repo: ask user via EditorUtility.DisplayDialog:
   "Plan mode uses git commits between tasks for easy rollback.
    This project has no git repo. Initialize one?"
   [Initialize] [Continue Without Git] [Cancel]
3. If initializing: Init() creates repo with sensible .gitignore:
   ```
   Library/
   Temp/
   Logs/
   UserSettings/
   obj/
   Build/
   Builds/
   *.csproj
   *.sln
   .vs/
   .idea/
   ```
4. Initial commit: "[CCT] initial commit before plan execution"

### 3c: Auto-Commit Between Tasks

In the planner's OnTaskCompleted():
1. After task N completes, check HasChanges()
2. If changes: Commit() with message "[CCT] task {N}/{Total}: {title}"
3. Log to terminal: "[git] committed: task {N}/{Total}: {title}"
4. Then proceed to next task

### 3d: Git Info in Settings Menu

Add to settings menu:
- "Git/Show Status" -- logs current git status to terminal
- "Git/Show Recent Commits" -- logs last 10 commits to terminal

COMMIT: "[CCT] feat: git integration with auto-commit between plan tasks"


## Phase 4: Polish & Testing

### 4a: Error Handling

- Plan file doesn't exist after planning prompt: log error, abort plan
- Plan file has no parseable tasks: log error, abort plan
- Git commit fails: log warning, continue (don't block task execution)
- Task prompt gets error_during_execution: log error, pause planner,
  let user decide (retry/skip/abort via buttons in toolbar)
- Domain reload during plan execution: save planner state to EditorPrefs
  (current task index, plan mode active flag), restore on reload, continue

### 4b: Planner State in Toolbar

When planner is active, the toolbar should show:
- Plan progress: "Task 3/7" label
- Pause/Resume button
- Cancel Plan button

### 4c: Domain Reload for Planner

The planner needs to survive domain reloads during task execution:
- Save to EditorPrefs: _planActive, _currentTaskIndex, _planFilePath
- On restore: re-parse plan file, resume from saved task index
- If a task was interrupted mid-execution, the domain reload recovery
  in ClaudeCodeProcess handles that (auto-resume the current task)

### 4d: Test Scenarios

1. Simple prompt (no plan mode): should work exactly as before
2. Plan mode with small task (2-3 tasks): verify plan created, tasks
   executed in order, git commits between each
3. Domain reload during plan execution: verify recovery and continuation
4. Cancel mid-plan: verify clean stop
5. Error during task: verify pause and user control
6. Large prompt that previously hit 32k token limit: verify planning
   breaks it into manageable tasks

COMMIT: "[CCT] feat: polish plan mode, error handling, domain reload"


## Notes for Implementation

- The refactoring in Phase 1 is the most critical and most risky. Take care
  to preserve all existing behaviour. Test after each sub-phase.
- Use internal access modifier for classes that only talk to each other
  within the ClaudeCodeBridge namespace.
- ClaudeCodeProcess should NOT depend on UnityEditor GUI classes. It should
  only use System.*, UnityEngine.Debug for logging, and EditorPrefs for
  domain reload persistence.
- The planner starts a NEW Claude Code session for each task (no --resume).
  Each task gets fresh context. The plan file on disk IS the shared state.
- Git operations are synchronous because they're fast (<100ms typically).
  If this becomes a problem with large repos, they can be moved to a thread
  later.
- The plan file format uses markdown headers that are easy to parse with
  string operations. No need for a markdown parser library.
