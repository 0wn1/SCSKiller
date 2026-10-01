<#
.SYNOPSIS
Builds dist\SCSKiller\ and dist\SCSKiller.zip:

  SCSKiller.exe            WinUI app, self-contained (.NET + Windows App SDK), + oodle-data-shared.dll, zlib-ng2.dll
  cli\scskiller.exe        command line, self-contained, same Core project and commit, + the two codec DLLs
  cli\scskillerw.exe       the same CLI flagged as a GUI-subsystem exe: what the scheduled task runs (no console window)
  native\                  scskiller_warm.exe + the proxy d3d12.dll (the app looks in native\, the CLI in ..\native\)
                           + amd_ags_x64.dll (AMD AGS, downloaded by the proxy's CMake configure)
                           + segheap\scskiller_warm.exe (the same on the segment heap: NVIDIA's warm)
  THIRD-PARTY-NOTICES.md   every third-party component and its licence; notices\ holds the Microsoft packages' own notices

The CLI can't sit next to the app: scskiller.exe and SCSKiller.exe are the same file name on NTFS.

-NoOodle leaves oodle-data-shared.dll out (CI and anything published: Oodle is proprietary, redistribution not
established, THIRD-PARTY-NOTICES.md); UnrealReader and the FromSoft reader download it through CUE4Parse on first use.

-Version X.Y.Z[-pre] (a leading v is dropped) stamps every exe and DLL with the same ProductVersion (SignPath).
Without it: the v* tag at HEAD if there is exactly one, else the dev default 0.0.0-internal.0+<sha>.
#>
param([string]$Configuration = "Release", [switch]$NoZip, [switch]$NoOodle, [string]$Version)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent

# 0. version: -Version, else the one v* tag at HEAD (promotions tag one commit several times: then say which)
if (-not $Version) {
    $tags = @(git -C $repo tag --points-at HEAD -l "v*")
    if ($tags.Count -gt 1) { throw "HEAD has several tags ($($tags -join ', ')): pass -Version" }
    if ($tags.Count -eq 1) { $Version = $tags[0] }
}
$Version = $Version -replace '^v', ''
if ($Version -and $Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$') { throw "not a SemVer X.Y.Z[-pre]: $Version" }
$versionArgs = if ($Version) { @("-p:Version=$Version", "-p:IncludeSourceRevisionInInformationalVersion=false") } else { @() }
Write-Host "version: $(if ($Version) { $Version } else { '0.0.0-internal.0 (dev)' })"
$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"   # .NET 10 SDK is per user; PATH may have an older one
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
$dist = Join-Path $repo "dist"
$out = Join-Path $dist "SCSKiller"
$cli = Join-Path $out "cli"
$native = Join-Path $out "native"

function Run([string]$exe, [string[]]$argv) {
    & $exe @argv
    if ($LASTEXITCODE -ne 0) { throw "$exe $argv failed with exit code $LASTEXITCODE" }
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

# 1. native tools from the proxy CMake build
$build = Join-Path $repo "proxy\build"
$scskVersion = if ($Version) { $Version } else { "0.0.0-internal.0" }   # every time: a cached version must not carry over
Run cmake @("-S", (Join-Path $repo "proxy"), "-B", $build, "-A", "x64", "-DSCSK_VERSION=$scskVersion")
Run cmake @("--build", $build, "--config", "Release")
New-Item -ItemType Directory -Force $native | Out-Null
Copy-Item (Join-Path $build "Release\scskiller_warm.exe"), (Join-Path $build "Release\d3d12.dll"), (Join-Path $build "Release\amd_ags_x64.dll") $native
New-Item -ItemType Directory -Force (Join-Path $native "segheap") | Out-Null
Copy-Item (Join-Path $build "Release\segheap\scskiller_warm.exe") (Join-Path $native "segheap")

# 2. app + CLI
Run $dotnet (@("publish", (Join-Path $repo "src\SCSKiller.App\SCSKiller.App.csproj"), "-c", $Configuration, "-r", "win-x64",
    "--self-contained", "true", "-p:Platform=x64", "-p:EnableMsixTooling=true", "-o", $out) + $versionArgs)   # MSIX tooling: else the app's .pri/.xbf aren't published and XAML crashes at start
Run $dotnet (@("publish", (Join-Path $repo "src\SCSKiller.Cli\SCSKiller.Cli.csproj"), "-c", $Configuration, "-r", "win-x64",
    "--self-contained", "true", "-o", $cli) + $versionArgs)

# 3. scskillerw.exe: optional header Subsystem (e_lfanew + 24 + 68) 3 = console -> 2 = GUI, as the SDK does for WinExe.
#    The apphost still runs scskiller.dll from the same folder.
$bytes = [IO.File]::ReadAllBytes((Join-Path $cli "scskiller.exe"))
$subsystem = [BitConverter]::ToInt32($bytes, 0x3C) + 24 + 68
if ($bytes[$subsystem] -ne 3) { throw "scskiller.exe: unexpected PE subsystem $($bytes[$subsystem])" }
$bytes[$subsystem] = 2
[IO.File]::WriteAllBytes((Join-Path $cli "scskillerw.exe"), $bytes)

# 4. codecs next to both executables (UnrealReader loads them from AppContext.BaseDirectory): local copies first
#    (this checkout, then the main checkout when run from a git worktree), else CUE4Parse downloads them.
$codecs = @(if (-not $NoOodle) { "oodle-data-shared.dll" }) + "zlib-ng2.dll"
$roots = @($repo)
try { $common = git -C $repo rev-parse --path-format=absolute --git-common-dir } catch { $common = $null }
if ($common) { $roots += Split-Path $common -Parent }
$dirs = $roots | Select-Object -Unique | ForEach-Object { Join-Path $_ "tools\ueshaders"; Join-Path $_ "tools\ueshaders\bin\Release\net10.0" }
foreach ($c in $codecs) {
    $found = $dirs | ForEach-Object { Join-Path $_ $c } | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($found) { Copy-Item $found $cli; Write-Host "$c <- $found" }
}
if ($codecs | Where-Object { -not (Test-Path (Join-Path $cli $_)) }) { Run (Join-Path $cli "scskiller.exe") @("fetch-codecs") }
if ($NoOodle) { Remove-Item (Join-Path $cli "oodle-data-shared.dll") -ErrorAction SilentlyContinue }   # fetch-codecs fetches both
foreach ($c in $codecs) { Copy-Item (Join-Path $cli $c) $out }

# 4b. licences: our notices and licence (GPL-3.0-or-later + the section 7 permission), plus the large notice files of the Microsoft packages as shipped
Copy-Item (Join-Path $repo "THIRD-PARTY-NOTICES.md") $out
foreach ($l in "LICENSE", "LICENSE-EXCEPTION.txt") { Copy-Item (Join-Path $repo $l) $out }
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget\packages" }
New-Item -ItemType Directory -Force (Join-Path $out "notices") | Out-Null
# versions come from what was actually published (any SDK/runtime of this major or newer builds it)
$runtime = ((Get-Content (Join-Path $out "SCSKiller.runtimeconfig.json") -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks |
    Where-Object name -eq "Microsoft.NETCore.App").version
$libs = (Get-Content (Join-Path $repo "src\SCSKiller.App\obj\project.assets.json") -Raw | ConvertFrom-Json).libraries.PSObject.Properties.Name   # the restore that built it
function PackageVersion($id) {
    $v = $libs | Where-Object { $_ -like "$id/*" } | Select-Object -First 1
    if (-not $v) { throw "$id is not among the app's restored packages" }
    $v.Split("/")[1]
}
foreach ($n in @(
        @("microsoft.netcore.app.runtime.win-x64\$runtime\THIRD-PARTY-NOTICES.TXT", "dotnet-THIRD-PARTY-NOTICES.txt"),
        @("microsoft.windowsappsdk.runtime\$(PackageVersion 'Microsoft.WindowsAppSDK.Runtime')\NOTICE.txt", "WindowsAppSDK-NOTICE.txt"),
        @("microsoft.windowsappsdk.winui\$(PackageVersion 'Microsoft.WindowsAppSDK.WinUI')\NOTICE.txt", "WinUI-NOTICE.txt"),
        @("microsoft.web.webview2\$(PackageVersion 'Microsoft.Web.WebView2')\NOTICE.txt", "WebView2-NOTICE.txt"),
        @("communitytoolkit.highperformance\$(PackageVersion 'CommunityToolkit.HighPerformance')\ThirdPartyNotices.txt", "CommunityToolkit-ThirdPartyNotices.txt"))) {
    Copy-Item (Join-Path $nuget $n[0].ToLowerInvariant()) (Join-Path $out "notices\$($n[1])")   # THIRD-PARTY-NOTICES.md lists the versions shipped
}

# 5. layout check, including self-contained .NET (no "install .NET" prompt): the runtime next to each exe and
#    runtimeconfig.json listing includedFrameworks rather than a shared framework
$expected = "SCSKiller.exe", "oodle-data-shared.dll", "zlib-ng2.dll", "cli\scskiller.exe", "cli\scskillerw.exe",
    "cli\oodle-data-shared.dll", "cli\zlib-ng2.dll", "native\scskiller_warm.exe", "native\segheap\scskiller_warm.exe", "native\d3d12.dll", "native\amd_ags_x64.dll",
    "hostfxr.dll", "coreclr.dll", "cli\hostfxr.dll", "cli\coreclr.dll", "Microsoft.WindowsAppRuntime.dll", "SCSKiller.pri",
    "THIRD-PARTY-NOTICES.md", "LICENSE", "LICENSE-EXCEPTION.txt", "notices\dotnet-THIRD-PARTY-NOTICES.txt"
if ($NoOodle) { $expected = $expected | Where-Object { $_ -notlike "*oodle-data-shared.dll" } }
$missing = $expected | Where-Object { -not (Test-Path (Join-Path $out $_)) }
if ($missing) { throw "dist\SCSKiller is missing: $($missing -join ', ')" }
if ($NoOodle -and (Get-ChildItem $out -Recurse -Filter "oodle-data-shared.dll")) { throw "-NoOodle, but oodle-data-shared.dll is in dist\SCSKiller" }
foreach ($rc in "SCSKiller.runtimeconfig.json", "cli\scskiller.runtimeconfig.json") {
    if ((Get-Content (Join-Path $out $rc) -Raw) -notmatch '"includedFrameworks"') { throw "$rc is framework-dependent" }
}

# 6. zip (with the SCSKiller\ folder at its root)
if (-not $NoZip) {
    $zip = Join-Path $dist "SCSKiller.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($out, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
}
$msg = "dist\SCSKiller: {0:N0} MB" -f ((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
if (-not $NoZip) { $msg += ", dist\SCSKiller.zip: {0:N0} MB" -f ((Get-Item $zip).Length / 1MB) }
Write-Host $msg
