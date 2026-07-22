# HgVs

HgVs is a Visual Studio 2022 extension that adds practical Mercurial tools directly to the IDE.

## Features

- Browse modified, added, removed, and untracked files.
- Compare uncommitted changes, the current branch, the parent branch, or a custom branch.
- Open files normally or open their Mercurial diff.
- Navigate TODO comments in changed files, including unsaved editor contents.
- Inspect Mercurial file history and open historical revisions.
- Revert selected changes.
- Export open editor files to a directory or Total Commander.

## Requirements

- Visual Studio 2022 (17.x).
- .NET Framework 4.7.2.
- Mercurial command-line client available as `hg` on `PATH`.

## Build

Restore packages and build the solution with Visual Studio 2022 MSBuild:

```powershell
msbuild HgVs.sln /t:Restore
msbuild HgVs.sln /t:Build /p:Configuration=Debug /p:Platform="Any CPU" /p:DeployExtension=false
```

The resulting extension package is written to:

```text
HgVs\bin\Debug\HgVs.vsix
```

## Installation

Close all Visual Studio instances, run the generated `HgVs.vsix`, and follow the VSIX Installer prompts. If the older FileCopyExtension build is installed, uninstall it to avoid duplicate commands.
