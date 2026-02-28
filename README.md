# Claude Code Terminal for Unity

MIT licensed.


A Unity Editor tool that embeds a Claude Code interface directly inside the Unity Editor. Type natural language commands in Unity and Claude Code executes them, with full access to your project files and the Unity Editor via MCP (Model Context Protocol).

Claude Code can read your scene hierarchy, create and edit scripts, manage GameObjects and components, read the console, and perform virtually any editor operation -- all from a text prompt inside Unity.

## Prerequisites

Before starting, make sure you have the following installed:

1. **Unity 6.3+** (6000.0.x or later)
   - Download from https://unity.com/releases/editor/archive

2. **Claude Code CLI**
   - Requires Node.js 18+
   - Install: `npm install -g @anthropic-ai/claude-code`
   - Verify: open a terminal and run `claude --version`
   - You need an active **Claude Max** or **Claude Pro** subscription, or an Anthropic API key

3. **Python 3.10+ and uv**
   - uv is required by the MCP for Unity server
   - Install uv: https://docs.astral.sh/uv/getting-started/installation/
   - Verify: `uv --version`

4. **Git** (for installing the Unity package via git URL)


## Step 1: Install MCP for Unity (per Unity project)

This provides the bridge between Claude Code and the Unity Editor.

1. Open your Unity project
2. Go to **Window > Package Manager**
3. Click the **+** button in the top-left
4. Select **Add package from git URL...**
5. Paste the following and click Add:

```
https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#main
```

Wait for the package to import.


## Step 2: Start the MCP Server (per editing session)

1. In Unity, go to **Window > MCP for Unity > Toggle MCP Window**
2. In the MCP panel that opens, click **Start Server**
3. A terminal window will open running the MCP HTTP server
4. Confirm the panel shows **Session Active** or a green connected indicator

The server runs on `http://127.0.0.1:8080` by default. Leave the terminal window open.


## Step 3: Register the MCP Server with Claude Code (once only)

Open a PowerShell or terminal window (not inside Claude Code) and run:

```
claude mcp add-json unityMCP '{"type":"http","url":"http://localhost:8080/mcp"}' --scope user
```

This registers the Unity MCP server globally so Claude Code can access it from any project. The tools will only work when the MCP server is actually running in a Unity project.

To register for a single project only, use `--scope local` instead and run the command from your Unity project root directory.


## Step 4: Authenticate Claude Code (check per session, to avoid API charges)

If you have not already authenticated Claude Code:

```
claude auth login
```

This opens a browser window for OAuth login. Sign in with your Anthropic account. After login, verify your subscription is active by running `claude` interactively and checking the status line shows **Claude Max** (or your plan) rather than **Claude API**.

If you see "Claude API" instead of your subscription plan, you may have an `ANTHROPIC_API_KEY` environment variable set that is taking priority. Remove it and re-authenticate:

```
# PowerShell - remove for current session
$env:ANTHROPIC_API_KEY = $null

# PowerShell - remove permanently
[System.Environment]::SetEnvironmentVariable('ANTHROPIC_API_KEY', $null, 'User')
```


## Step 5: Install the Editor Tool (once per project)

1. In your Unity project, create a folder: `Assets/Editor/` (if it does not already exist)
2. Copy `ClaudeCodeTerminal.cs` into `Assets/Editor/`
3. Wait for Unity to compile
4. Open the tool via **Window > Claude Code Terminal**


## Step 6: Add the CLAUDE.md File (once per project)

Copy the provided `CLAUDE.md` file into your Unity project root directory (the folder containing `Assets/`, `ProjectSettings/`, etc.).

This file contains project-level instructions that Claude Code reads automatically at the start of every prompt. It teaches Claude Code how to work effectively with Unity, including refreshing after script edits, checking the console for errors, updating serialized fields on scene instances, and other Unity-specific workflows.

You can edit this file at any time to add project-specific instructions.


## Usage (every session)

1. Start the MCP server running in Unity (Step 2 of Prerequisites)
2. Open the Claude Code Terminal via Unity menus **Window > Claude Code Terminal**
3. Type a command in the input field at the bottom and press **Enter** (twice) or click **Send**

Example commands:

```
read the unity console and tell me about any errors
```

```
create a new empty GameObject called SpawnPoint at position (0, 5, 0)
```

```
add a Rigidbody component to the Player object with mass 2 and drag 0.5
```

```
create a script called EnemyAI that patrols between waypoints
```

```
list all GameObjects tagged as Enemy in the scene
```

```
the player falls through the floor, investigate and fix it
```

Claude Code will use the MCP tools to interact with the Unity Editor, read and write scripts, and execute editor operations. Results stream into the terminal window in real time.


## Features

- **Session persistence**: Conversations maintain context across multiple prompts within a session
- **Streaming output**: See Claude Code's responses and tool calls as they happen
- **Full MCP access**: All unity-mcp tools are available (scene management, scripting, assets, materials, animation, prefabs, testing, console access)
- **No permission prompts**: The tool runs with `--dangerously-skip-permissions` so operations execute without interruption
- **Configurable**: Adjust max turns and CLI path via the Settings dropdown


## Toolbar

- **Status indicator**: Shows IDLE (green) or WORKING (yellow)
- **Clear**: Clears the output log
- **Session ID**: Shows the current conversation session
- **Settings dropdown**:
  - **Auto-scroll**: Toggle automatic scroll-to-bottom
  - **New Session**: Start a fresh conversation (clears session context)
  - **Clear Log**: Clear the output display
  - **Configure CLI Path**: Set a custom path to the Claude Code executable
  - **Set Max Turns**: Limit how many agentic turns Claude Code can take per prompt (default: 25)


## Security Note

The editor tool launches Claude Code with `--dangerously-skip-permissions`, which allows it to read, write, and execute files without prompting for approval on each action. This is necessary because headless mode cannot display interactive permission prompts.

This means Claude Code can modify any file in your project and run shell commands without confirmation. This is appropriate for local development but you should be aware of it. If you prefer more control, you can edit `ClaudeCodeTerminal.cs` and replace `--dangerously-skip-permissions` with specific tool allowlists, for example:

```
--permission-mode acceptEdits --allowedTools "mcp__unityMCP"
```

This would auto-approve MCP tool calls and file edits but block other operations like arbitrary shell commands.


## Troubleshooting

### "Failed to launch Claude Code"
- Verify `claude` is on your system PATH: `claude --version`
- Or set the full path via Settings > Configure CLI Path

### MCP tools not available
- Check the MCP server is running: the MCP for Unity panel should show Session Active
- Verify registration: `claude mcp list` should show `unityMCP`
- Click New Session in the editor window to pick up config changes

### Permission errors on tool calls
- The tool uses `--dangerously-skip-permissions` which should bypass all prompts
- If you still see permission issues, check that your Claude Code version is up to date: `npm update -g @anthropic-ai/claude-code`

### Stale MCP servers from previous tools
- If you previously used Coplay, Vibe Unity, or other MCP integrations, remove them:
  ```
  claude mcp list
  claude mcp remove <server-name> --scope user
  ```
- Also check for a `.mcp.json` in your project root that may contain old entries

### Text colour is too dark in the output window
- The tool sets text colour to #D0D0D0. If your Unity Editor theme overrides this, adjust the colour values in the `EnsureStyles()` method in `ClaudeCodeTerminal.cs`

### MCP server won't start
- Verify Python and uv are installed: `python --version` and `uv --version`
- Check the terminal window that opens for error messages
- Try restarting Unity and starting the server again


## Files

| File | Location | Purpose |
|------|----------|---------|
| `ClaudeCodeTerminal.cs` | `Assets/Editor/` | The Unity Editor window tool |
| `CLAUDE.md` | Project root | Instructions for Claude Code on how to work with Unity |


## Subsequent New Projects

After you've installed everything and validated it's working with your first project, you might want to build something new.

- Create the unity project from the Hub
- Use Window|Install TextMeshPro Essential Resources (to avoid problems later, the dialog can lock up an otherwise perfect build)
- Install the Unity MCP server from PackageManager: 
```
https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#main
```
- Start the MCP server with Window|MCP For Unity|Toggle MCP Window, then press the 'Start Server' button
- Start the Claude Code Terminal with Window|Claude Code Terminal
- Type in your prototype design prompt!


## How It Works

The editor tool spawns a `claude -p` (headless mode) process for each prompt, with the working directory set to your Unity project root. Claude Code picks up the `.mcp.json` or user-level MCP config and gains access to all unity-mcp tools. Responses stream back via `--output-format stream-json` and are displayed in the editor window. Session IDs are captured and reused via `--resume` to maintain conversational context across multiple prompts.
WARNING: the claude instance is started with highly permissive settings to avoid permission requests in the Claude terminal (which will not be reflected inside Unity, causing an unexplained pause until you find the cause). Beware! External materials (text files, images, etc) may try to abuse these permissions to hijack your machine.

The MCP for Unity package runs an HTTP server inside the Unity Editor process that exposes editor operations as MCP tools. Claude Code calls these tools over HTTP to read and modify the scene, create scripts, manage components, and perform other editor actions.

Notes:
- Don't change any source files manually whilst running: the editor will rebuild and that will trigger a restart of the Terminal which can interupt the current process.
