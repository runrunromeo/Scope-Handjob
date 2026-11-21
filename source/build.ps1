# Builds ScopeHandjob.dll from this folder.
#   ./build.ps1 -GameDir "C:\path\to\SPT"
# Needs a .NET SDK (any recent one; it builds for .NET Framework 4.7.2). Output: bin\Release\ScopeHandjob.dll
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string]$DotNetPath = 'dotnet',
    [string]$FrameworkPath
)
$ErrorActionPreference = 'Stop'
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
$arguments = @('build', (Join-Path $PSScriptRoot 'ScopeHandjob.csproj'), '-c', 'Release', "-p:GameDir=$GameDir", '--nologo')
# Without the .NET Framework 4.7.2 targeting pack installed, point this at a folder of reference assemblies
# (for example Unity's MonoBleedingEdge\lib\mono\4.7.2-api).
if ($FrameworkPath) { $arguments += "-p:FrameworkPathOverride=$FrameworkPath" }
& $DotNetPath @arguments
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
Write-Output (Join-Path $PSScriptRoot 'bin\Release\ScopeHandjob.dll')
