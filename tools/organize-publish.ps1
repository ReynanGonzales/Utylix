# Puts the libraries of the published program folder into subfolders, so the folder isn't 300 files long:
#   dotnet\  the .NET runtime's libraries      wpf\  Windows' window libraries (WPF, Windows Forms)      libs\  Utylix's own libraries (PDF, torrents, ...)
# Native libraries (coreclr, pdfium, onnxruntime, WPF's native parts ...) and a few core libraries stay beside Utylix.exe.
#
# .NET only looks for a library next to the exe, so the moved ones are taken out of Utylix.deps.json and the program finds them itself
# (app\AppFolders.cs, a module initializer). Afterwards "Utylix.exe --selftest" must pass (it loads every library and opens a window); if it
# doesn't, everything is put back as it was and the folder stays flat. Usage:  organize-publish.ps1 [-Dir app\publish]
param([string]$Dir = (Join-Path $PSScriptRoot "..\app\publish"), [switch]$KeepOnFail)
$ErrorActionPreference = "Stop"
$Dir = (Resolve-Path $Dir).Path
$depsFile = Join-Path $Dir "Utylix.deps.json"
if (Test-Path (Join-Path $Dir "dotnet")) { Write-Host "organize-publish: already organized"; exit 0 }

# libraries that must stay beside the exe: the runtime needs them before the program can look in the folders
$stay = @(
    "Utylix.dll", "System.Private.CoreLib.dll", "System.Runtime.dll", "System.Runtime.Loader.dll", "System.Collections.dll", "System.Threading.dll"
)

Add-Type -AssemblyName System.Web.Extensions
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = [int]::MaxValue; $ser.RecursionLimit = 200
$original = Get-Content $depsFile -Raw
$json = $ser.DeserializeObject($original)

$moves = @{}                                                   # file name -> folder
foreach ($target in $json["targets"].Values) {
    foreach ($libName in @($target.Keys)) {
        $lib = $target[$libName]
        if (-not $lib.ContainsKey("runtime")) { continue }
        $folder = if ($libName -like "runtimepack.Microsoft.NETCore.App*") { "dotnet" }
                  elseif ($libName -like "runtimepack.Microsoft.WindowsDesktop.App*") { "wpf" }
                  else { "libs" }
        $runtime = $lib["runtime"]
        foreach ($asset in @($runtime.Keys)) {
            $leaf = $asset.Split("/")[-1]
            if ($stay -contains $leaf -or -not (Test-Path (Join-Path $Dir $leaf))) { continue }
            $moves[$leaf] = $folder
            $runtime.Remove($asset) | Out-Null
        }
        if ($runtime.Count -eq 0) { $lib.Remove("runtime") | Out-Null }
    }
}

# do it, with a way back
$backup = Join-Path $Dir "Utylix.deps.json.flat"
Copy-Item $depsFile $backup -Force
[IO.File]::WriteAllText($depsFile, $ser.Serialize($json), (New-Object Text.UTF8Encoding $false))
foreach ($leaf in $moves.Keys) {
    $to = Join-Path $Dir $moves[$leaf]
    New-Item -ItemType Directory -Force $to | Out-Null
    Move-Item (Join-Path $Dir $leaf) (Join-Path $to $leaf)
}
Write-Host ("organize-publish: moved {0} libraries into {1}" -f $moves.Count, (($moves.Values | Sort-Object -Unique) -join ", "))

# does it start?
$report = Join-Path ([IO.Path]::GetTempPath()) "utylix-selftest.txt"
Remove-Item $report -ErrorAction SilentlyContinue
$env:UTYLIX_FOLDERS_LOG = Join-Path ([IO.Path]::GetTempPath()) "utylix-folders.log"; Remove-Item $env:UTYLIX_FOLDERS_LOG -ErrorAction SilentlyContinue
$p = Start-Process (Join-Path $Dir "Utylix.exe") -ArgumentList "--selftest", "--no-register" -PassThru
$finished = $p.WaitForExit(90000)
$text = if (Test-Path $report) { Get-Content $report -Raw } else { "(no report)" }
if ($finished -and $text -match "0 problems") {
    Remove-Item $backup -Force
    Write-Host ("organize-publish: self-test passed. " + ($text -split "`n")[0].Trim())
    exit 0
}
if (-not $finished) { try { $p.Kill() } catch { } }
Write-Warning "organize-publish: self-test FAILED. Report: $text"
if ($KeepOnFail) { exit 1 }
foreach ($leaf in $moves.Keys) { Move-Item (Join-Path (Join-Path $Dir $moves[$leaf]) $leaf) (Join-Path $Dir $leaf) -Force }
foreach ($f in ($moves.Values | Sort-Object -Unique)) { Remove-Item (Join-Path $Dir $f) -Recurse -Force -ErrorAction SilentlyContinue }
Move-Item $backup $depsFile -Force
exit 1
