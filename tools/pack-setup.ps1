# Makes Utylix-Setup.exe: the small setup starter (setup\SetupStub.cs, compiled with Windows' own C# compiler, so it runs on any
# Windows 10 / 11 PC with nothing installed) with the Utylix program folder attached as a ZIP, then its length and "UTYLIXPK".
param(
    [string]$Program = (Join-Path $PSScriptRoot "..\app\publish"),
    [string]$Out = (Join-Path $PSScriptRoot "..\Utylix-Setup.exe"),
    # OFFLINE setup: the tools folder (the programs Utylix normally downloads: yt-dlp, ffmpeg, the video player engine, the background remover model,
    # the JavaScript helper) to carry inside the setup file, as "Utylix-tools" - normally the tools folder of a PC that has them
    [string]$Tools = ""
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")

# the program folder's libraries go into subfolders (dotnet\, wpf\, libs\); the script checks that the result starts and otherwise leaves the folder flat
powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "organize-publish.ps1") -Dir $Program
if ($LASTEXITCODE -ne 0) { Write-Warning "The program folder could not be organized into subfolders: it stays flat (it works the same)." }
# UtylixVid.exe (the video editor): a copy of the launcher with its own icon, same Utylix.dll
powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "make-vid-exe.ps1") -Dir $Program
if ($LASTEXITCODE -ne 0) { Write-Warning "UtylixVid.exe could not be made: the video editor is left out of this build." }
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

# offline: the tools go into the same ZIP, in a folder "Utylix-tools" (the setup wizard finds it next to Utylix.exe after unpacking and uses it by itself)
if ($Tools) {
    $toolsRoot = Resolve-Path $Tools
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Update)
    try {
        foreach ($name in "yt-dlp.exe", "ffmpeg", "vlc", "models", "deno") {
            $path = Join-Path $toolsRoot $name
            if (-not (Test-Path $path)) { Write-Warning "Tools part missing, left out: $name"; continue }
            $files = if ((Get-Item $path).PSIsContainer) { Get-ChildItem $path -Recurse -File } else { Get-Item $path }
            foreach ($file in $files) {
                $relative = $file.FullName.Substring($toolsRoot.Path.Length).TrimStart([char]92).Replace([char]92, [char]47)
                $level = if ($file.Extension -in ".onnx", ".zip") { [IO.Compression.CompressionLevel]::NoCompression } else { [IO.Compression.CompressionLevel]::Optimal }
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, "Utylix-tools/" + $relative, $level)
            }
            Write-Host "  tools: $name"
        }
    } finally { $archive.Dispose() }
}

# starter + ZIP + length + mark
$o = [IO.File]::Create($Out)
try {
    foreach ($part in $stub, $zip) { $in = [IO.File]::OpenRead($part); try { $in.CopyTo($o) } finally { $in.Dispose() } }
    $o.Write([BitConverter]::GetBytes([long](Get-Item $zip).Length), 0, 8)
    $mark = [Text.Encoding]::ASCII.GetBytes("UTYLIXPK"); $o.Write($mark, 0, 8)
} finally { $o.Dispose() }
[IO.File]::Delete($zip)
"{0}: {1:0.0} MB" -f (Split-Path $Out -Leaf), ((Get-Item $Out).Length / 1MB)
