# Makes Utylix-Setup.exe: the small setup starter (setup\SetupStub.cs, compiled with Windows' own C# compiler, so it runs on any
# Windows 10 / 11 PC with nothing installed) with the Utylix program folder attached as a ZIP, then its length and "UTYLIXPK".
param(
    [string]$Program = (Join-Path $PSScriptRoot "..\app\publish"),
    [string]$Out = (Join-Path $PSScriptRoot "..\Utylix-Setup.exe")
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$fw = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
$obj = Join-Path $root "setup\obj"
New-Item -ItemType Directory -Force $obj | Out-Null

# the starter
$stub = Join-Path $obj "stub.exe"
& (Join-Path $fw "csc.exe") /nologo /target:winexe /optimize+ "/out:$stub" "/win32icon:$(Join-Path $root 'app\app.ico')" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll "/r:$(Join-Path $fw 'System.IO.Compression.dll')" (Join-Path $root "setup\SetupStub.cs")
if ($LASTEXITCODE -ne 0) { throw "The setup starter did not compile." }

# the program folder as a ZIP
$program = Resolve-Path $Program
if (-not (Test-Path (Join-Path $program "Utylix.exe"))) { throw "No Utylix program folder at $program (build it first)." }
$zip = Join-Path $obj "program.zip"
if (Test-Path $zip) { [IO.File]::Delete($zip) }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($program, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

# starter + ZIP + length + mark
$o = [IO.File]::Create($Out)
try {
    foreach ($part in $stub, $zip) { $in = [IO.File]::OpenRead($part); try { $in.CopyTo($o) } finally { $in.Dispose() } }
    $o.Write([BitConverter]::GetBytes([long](Get-Item $zip).Length), 0, 8)
    $mark = [Text.Encoding]::ASCII.GetBytes("UTYLIXPK"); $o.Write($mark, 0, 8)
} finally { $o.Dispose() }
[IO.File]::Delete($zip)
"Utylix-Setup.exe: {0:0.0} MB" -f ((Get-Item $Out).Length / 1MB)
