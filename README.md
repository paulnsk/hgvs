# HgVs

HgVs is a Visual Studio 2022 and Visual Studio 2026 extension that adds practical Mercurial tools directly to the IDE.

## Features

- Browse modified, added, removed, and untracked files.
- Switch between a flat list and a compact folder tree; the choice is remembered.
- In Tree mode, expand folders with matching files and collapse the rest by a rule (for example, hide unit tests or show only not-viewed files). Rules live in `%APPDATA%\HgVs\collapse-rules.txt`; the button menu edits them and can add default rules.
- Compare uncommitted changes, the current branch, the parent branch, or a custom branch.
- Open files normally or open their Mercurial diff; opening the same diff again activates the existing window.
- Files that differ from the last commit show a bar above the editor with a View Diff button that opens the diff at the current line. The bar can be turned off with the Diff Bar checkbox.
- Recognize renamed files and compare them with their original paths.
- Track viewed and not-viewed changed files persistently across branches and projects.
- Navigate TODO comments in changed files, including unsaved editor contents.
- Inspect Mercurial file history and open historical revisions.
- Follow renamed files when opening or comparing historical revisions.
- Revert selected changes.
- Export open editor files to a directory or Total Commander.

## Modified-files controls

- Press `Enter` to open the selected file, or `Ctrl+Enter` to open its diff when a base version exists.
- Press `Space`, or click the Review cell, to toggle the viewed state.
- Use the context menu to open files or diffs and update review state, including from a pasted list of paths or masks.
- In Tree view, use `Left`/`Right` to collapse or expand folders. TODO mode keeps its flat list.

## Keyboard shortcut

Press `Alt+Shift+K` to locate the active editor file in the HG Modified Files window. In TODO mode, HgVs selects the TODO item nearest to the editor caret.

The Visual Studio command is `HgVs.LocateInHgModifiedFiles`. To change its shortcut, open `Tools > Options > Environment > Keyboard` and search for that command name.

## Requirements

- Visual Studio 2022 (17.x) or Visual Studio 2026 (18.x).
- .NET Framework 4.7.2.
- Mercurial command-line client available as `hg` on `PATH`.

## Build

Restore packages and build the solution with Visual Studio 2022 MSBuild:

```powershell
msbuild HgVs.sln /t:Restore
msbuild HgVs.sln /t:Build /p:Configuration=Release /p:Platform="Any CPU" /p:DeployExtension=false
```

The resulting extension package is written to:

```text
HgVs\bin\Release\HgVs.vsix
```
