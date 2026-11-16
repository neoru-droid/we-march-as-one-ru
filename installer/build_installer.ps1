param(
    [Parameter(Mandatory = $true)][string]$SrcDir,
    [Parameter(Mandatory = $true)][string]$OutputExePath,
    [string]$PayloadGzPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$roslynCsc = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
$netFxCsc  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

$useRoslyn = Test-Path -LiteralPath $roslynCsc
$csc = if ($useRoslyn) { $roslynCsc } else { $netFxCsc }
if (-not (Test-Path -LiteralPath $csc)) {
    throw "csc.exe not found on this machine."
}

$outDir = Split-Path -Parent $OutputExePath
if (-not [string]::IsNullOrWhiteSpace($outDir) -and -not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}

$resolvedPayload = $PayloadGzPath
if ([string]::IsNullOrWhiteSpace($resolvedPayload)) {
    $candidateGz = Join-Path $SrcDir "wmao_ru_payload.gz"
    if (Test-Path -LiteralPath $candidateGz) {
        $resolvedPayload = $candidateGz
    }
}

$refDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$sources = @(
    "AssemblyInfo.cs",
    "BinaryUtils.cs",
    "JsonMini.cs",
    "SerializedFileV23.cs",
    "Lz4Codec.cs",
    "AssetsFontPatcher.cs",
    "BundleStringPatcher.cs",
    "VdfSchemaPatcher.cs",
    "SteamLocator.cs",
    "PayloadProvider.cs",
    "InstallerDiagnostics.cs",
    "BackupManager.cs",
    "InstallerEngine.cs",
    "SetupForm.cs",
    "Program.cs"
) | ForEach-Object { Join-Path $SrcDir $_ }

$cscArgs = @(
    "/nologo",
    "/optimize+",
    "/debug-",
    "/codepage:65001",
    "/utf8output",
    "/platform:anycpu",
    "/target:winexe",
    "/out:$OutputExePath"
)

if (-not [string]::IsNullOrWhiteSpace($resolvedPayload)) {
    if (-not (Test-Path -LiteralPath $resolvedPayload)) {
        throw "Embedded payload file not found: $resolvedPayload"
    }
    $cscArgs += "/resource:$resolvedPayload,wmao_ru_payload.gz"
}

if ($useRoslyn) {
    $cscArgs += "/deterministic+"
    $cscArgs += "/nostdlib+"
    $cscArgs += "/r:$(Join-Path $refDir 'mscorlib.dll')"
    $cscArgs += "/r:$(Join-Path $refDir 'System.dll')"
    $cscArgs += "/r:$(Join-Path $refDir 'System.Core.dll')"
    $cscArgs += "/r:$(Join-Path $refDir 'System.Drawing.dll')"
    $cscArgs += "/r:$(Join-Path $refDir 'System.Windows.Forms.dll')"
} else {
    $cscArgs += "/r:System.dll"
    $cscArgs += "/r:System.Core.dll"
    $cscArgs += "/r:System.Drawing.dll"
    $cscArgs += "/r:System.Windows.Forms.dll"
}

$cscArgs += $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) {
    throw "Compilation of WeMarchAsOne-RU-Setup.exe failed with exit code $LASTEXITCODE"
}

Write-Host "Built $OutputExePath ($((Get-Item -LiteralPath $OutputExePath).Length) bytes)"
