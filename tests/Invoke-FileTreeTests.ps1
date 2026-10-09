param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml
$repoDirectory = Split-Path -Parent $PSScriptRoot
$assemblyPath = Join-Path $repoDirectory "HgVs\bin\$Configuration\HgVs.dll"
[void][Reflection.Assembly]::LoadFrom($assemblyPath)
$references = @($assemblyPath, 'System.Core', 'PresentationFramework', 'PresentationCore', 'WindowsBase', 'System.Xaml')
Add-Type -Path (Join-Path $PSScriptRoot 'HgFileTreeTests.cs') -ReferencedAssemblies $references
[HgFileTreeTests]::Run()
