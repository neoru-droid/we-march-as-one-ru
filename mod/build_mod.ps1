param(
    [Parameter(Mandatory = $true)][string]$ModSrcDir,
    [Parameter(Mandatory = $true)][string]$OrigAssemblyPath,
    [Parameter(Mandatory = $true)][string]$ManagedDir,
    [Parameter(Mandatory = $true)][string]$CecilDir,
    [Parameter(Mandatory = $true)][string]$WorkDir,
    [Parameter(Mandatory = $true)][string]$OutputAssemblyPath,
    [switch]$NoAchievements
)

$ErrorActionPreference = "Stop"

$roslynCsc = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
$netFxCsc  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

$useRoslyn = $false
if (Test-Path $roslynCsc) {
    $csc = $roslynCsc
    $useRoslyn = $true
} elseif (Test-Path $netFxCsc) {
    $csc = $netFxCsc
} else {
    throw "csc.exe not found (checked Roslyn BuildTools and .NET Framework v4.0.30319)"
}

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

$patcherExe = Join-Path $WorkDir "Patcher.exe"
$refAsmDll  = Join-Path $WorkDir "Assembly-CSharp.Ref.dll"
$runtimeDll = Join-Path $WorkDir "RuModRuntime.dll"

Write-Host "[1/4] Compiling Patcher.exe (.NET Framework 4.x)..."
$patcherArgs = @(
    "/nologo",
    "/optimize+",
    "/debug-",
    "/codepage:65001",
    "/utf8output",
    "/target:exe",
    "/out:$patcherExe",
    "/r:$(Join-Path $CecilDir 'Mono.Cecil.dll')",
    "/r:$(Join-Path $CecilDir 'Mono.Cecil.Rocks.dll')"
)
if ($useRoslyn) {
    $patcherArgs += "/deterministic+"
}
$patcherArgs += (Join-Path $ModSrcDir "Patcher.cs")

& $csc @patcherArgs
if ($LASTEXITCODE -ne 0) { throw "Failed to compile Patcher.exe" }

Write-Host "[2/4] Preparing compile-time Assembly-CSharp reference..."
Copy-Item -Force (Join-Path $CecilDir "Mono.Cecil.dll") $WorkDir
Copy-Item -Force (Join-Path $CecilDir "Mono.Cecil.Rocks.dll") $WorkDir
& $patcherExe "prep-ref" $OrigAssemblyPath $ManagedDir $refAsmDll
if ($LASTEXITCODE -ne 0) { throw "prep-ref failed" }

Write-Host "[3/4] Compiling RuModRuntime.dll..."
$runtimeRefs = @(
    "mscorlib.dll",
    "netstandard.dll",
    "System.dll",
    "System.Core.dll",
    "UnityEngine.dll",
    "UnityEngine.CoreModule.dll",
    "UnityEngine.UI.dll",
    "UnityEngine.UIModule.dll",
    "UnityEngine.TextRenderingModule.dll",
    "UnityEngine.TextCoreFontEngineModule.dll",
    "UnityEngine.TextCoreTextEngineModule.dll",
    "UnityEngine.JSONSerializeModule.dll",
    "Unity.TextMeshPro.dll",
    "Unity.InputSystem.dll",
    "com.rlabrecque.steamworks.net.dll"
)
$runtimeArgs = @(
    "/nologo",
    "/optimize+",
    "/debug-",
    "/codepage:65001",
    "/utf8output",
    "/nostdlib+",
    "/target:library",
    "/out:$runtimeDll"
)
if ($useRoslyn) {
    $runtimeArgs += "/deterministic+"
}
foreach ($dllName in $runtimeRefs) {
    $runtimeArgs += "/r:$(Join-Path $ManagedDir $dllName)"
}
$runtimeArgs += "/r:$refAsmDll"
$runtimeArgs += (Join-Path $ModSrcDir "RuModRuntime.cs")

& $csc @runtimeArgs
if ($LASTEXITCODE -ne 0) { throw "Failed to compile RuModRuntime.dll" }

Write-Host "[4/4] Merging & patching into Assembly-CSharp.dll..."
$patchCmdArgs = @("patch", $OrigAssemblyPath, $runtimeDll, $ManagedDir, $OutputAssemblyPath)
if ($NoAchievements) {
    $patchCmdArgs += "--no-achievements"
}
& $patcherExe @patchCmdArgs
if ($LASTEXITCODE -ne 0) { throw "Patcher failed" }

Write-Host "SUCCESS: Built and verified $OutputAssemblyPath"
