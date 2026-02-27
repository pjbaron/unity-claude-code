# Unity Project - Claude Code Instructions

## MCP Tools

This project uses unity-mcp (MCP for Unity). Always prefer MCP tools over file-based guesswork when interacting with the Unity Editor.

## Efficiency

- Do not read the entire codebase before acting. If the user asks for a specific change, make that change.
- Initially, only read scripts that are directly relevant to the task.
- Prefer MCP tools to query scene state over reading source files to infer it.
- If the user's request can be fulfilled purely through MCP tools, do not read or write code files.
- When creating GameObjects or modifying the scene, use MCP tools directly rather than writing scripts to do it.

## Serialized Field Changes

When changing default values of serialized fields ([SerializeField], public fields) in C# scripts, also use the manage_components MCP tool to update the same value on all existing scene instances of that component. Unity does not propagate script default changes to already-serialized instances. Use find_gameobjects to locate all GameObjects with the relevant component first.

## Script Workflow

- After creating or editing any C# script, call refresh_unity to trigger recompilation.
- After refresh, use read_console to check for compilation errors before proceeding.
- Use validate_script before writing a script to catch errors early.
- Do not assume a script compiled successfully without checking.

## Scene Awareness

- Before making changes, use manage_scene with get_hierarchy to understand the current scene structure.
- Use find_gameobjects to locate objects rather than guessing names or paths.
- Use editor_selection resource to understand what the user currently has selected when context is ambiguous.

## Batch Operations

- Use batch_execute when performing multiple MCP operations in sequence. It is significantly faster than individual calls.
- Group related operations: e.g. create object + add components + set properties in one batch.

## Edit Consequences

- After editing any C# script, DO NOT call refresh_unity. Unity auto-detects file changes and recompiles.
- After a script edit, wait at least 30 seconds before calling any MCP tools. Unity domain reload will temporarily disconnect the MCP bridge.
- If an MCP tool call fails, retry it after a short pause with exponential fallback.

## Component Properties

- When setting component properties via manage_components, use the exact C# field name, not the Inspector display name.
- Check component exists on the target object before attempting to set properties.

## Prefab Awareness

- Check if a GameObject is a prefab instance before editing. Changes to prefab instances may need to be applied to the prefab via manage_prefabs.
- If the user wants a change to affect all instances, modify the prefab asset, not individual scene instances.

## Bug Tracking

- When investigating any bug or unexpected behaviour, ALWAYS start by calling read_console to check the Unity logs before doing anything else. The logs almost always contain relevant errors, warnings, or stack traces.
- Only skip log checking if the user explicitly says to ignore them for that query.
- After reading logs, use the information to guide your investigation rather than guessing at causes.

## Error Handling

- If a script edit causes compilation errors (check read_console after refresh_unity), fix the errors immediately before doing anything else.
- Do not stack further changes on top of broken compilation state.

## General

- This is a Unity 6+ project.
- Target platform is Windows standalone unless otherwise stated.
- Prefer clear, readable C# over clever abstractions.
- Do not add packages or dependencies without asking first.
