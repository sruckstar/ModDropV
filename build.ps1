<#
  build.ps1 — publish ModDrop V as a self-contained Windows x64 folder
  (no .NET install needed on the user's machine), optionally zipped. The app ships
  as that open folder / zip — there is no installer.

    .\build.ps1                 # tests + publish to .\publish\ModDropV
    .\build.ps1 -Zip            # ... and ModDropV-<ver>-win-x64.zip
    .\build.ps1 -SkipTests
#>
param(
    [switch]$Zip,
    [switch]$SkipTests,
    [switch]$Release,
    [string]$HotLoad,
    [string]$Runtime = "win-x64"
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$out = Join-Path $PSScriptRoot "publish\ModDropV"
[xml]$props = Get-Content (Join-Path $PSScriptRoot "Directory.Build.props")
$version = $props.Project.PropertyGroup.Version

# CodeWalker.Core (gen9 conversion, game archive keys) comes in as a git submodule
if (-not (Test-Path external\CodeWalker\CodeWalker.Core\CodeWalker.Core.csproj)) {
    git submodule update --init --recursive
    if ($LASTEXITCODE -ne 0) { throw "could not fetch the CodeWalker submodule" }
}

# tests\ is developer-only and not in the repository — skip when absent
if (-not $SkipTests -and (Test-Path tests\Mdv.Tests)) {
    dotnet test tests\Mdv.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw "tests failed" }
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$common = @("-c", "Release", "-r", $Runtime, "--self-contained", "true", "-o", $out,
            "-p:DebugType=none", "-p:PublishReadyToRun=true")
dotnet publish src\Mdv.App @common
if ($LASTEXITCODE -ne 0) { throw "publish (app) failed" }
dotnet publish src\Mdv.Cli @common
if ($LASTEXITCODE -ne 0) { throw "publish (cli) failed" }

# native debug symbols shipped inside the SkiaSharp/HarfBuzz packages (~100 MB) are useless to users
Get-ChildItem $out -Recurse -Filter *.pdb | Remove-Item -Force

# the build's file list for the self-updater (Mdv.Core/AppUpdate.cs), with the early-access loader protocol it speaks
$manifest = "ModDropV.files.txt"
$protocol = [regex]::Match((Get-Content src\Mdv.Core\HotLoad.cs -Raw), 'const int Protocol = (\d+);').Groups[1].Value
if (-not $protocol) { throw "no HotLoad.Protocol in src\Mdv.Core\HotLoad.cs" }
$files = Get-ChildItem $out -Recurse -File | ForEach-Object { $_.FullName.Substring($out.Length + 1) } | Sort-Object
$lines = @("# ModDrop V $version", "# hotload-protocol $protocol") + $files + @($manifest)
[IO.File]::WriteAllLines((Join-Path $out $manifest), [string[]]$lines, (New-Object Text.UTF8Encoding $false))

$mb = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Published {0} -> {1} ({2:N0} MB)" -f $version, $out, $mb)

if ($Zip) {
    $zipPath = Join-Path $PSScriptRoot "publish\ModDropV-$version-$Runtime.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    Compress-Archive -Path "$out\*" -DestinationPath $zipPath
    $sha = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$zipPath.sha256", "$sha  $(Split-Path $zipPath -Leaf)`n")
    Write-Host "Zip -> $zipPath (SHA-256 $sha)"
}

if ($Release) {
    if (-not $Zip) { throw "-Release needs -Zip" }
    # release notes: this version's section of the 5mods changelog, HTML turned into Markdown
    $text = Get-Content (Join-Path $PSScriptRoot "5mods-description.txt") -Raw -Encoding UTF8
    $m = [regex]::Match($text, "<b>$([regex]::Escape($version))</b>\s*<ul>(.*?)</ul>", "Singleline")
    if (-not $m.Success) { throw "no changelog section for $version in 5mods-description.txt" }
    $notes = $m.Groups[1].Value -replace '\s*<li>', "`n- " -replace '</li>', '' `
             -replace '<a href="([^"]+)">([^<]+)</a>', '[$2]($1)' -replace '</?b>', '**' -replace '<[^>]+>', ''
    $notesFile = Join-Path $PSScriptRoot "publishelease-notes-$version.md"
    [IO.File]::WriteAllText($notesFile, $notes.Trim() + "`n", (New-Object Text.UTF8Encoding $false))
    gh release create "v$version" $zipPath "$zipPath.sha256" --repo sruckstar/ModDropV --title "ModDrop V $version" --notes-file $notesFile
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
    Write-Host "Released v$version"
}

if ($HotLoad) {
    if (-not (Test-Path $HotLoad)) { throw "no early-access loader at $HotLoad" }
    $early = Join-Path $PSScriptRoot "publish\ModDropV-early-access"
    if (Test-Path $early) { Remove-Item $early -Recurse -Force }
    Copy-Item $out $early -Recurse
    Copy-Item $HotLoad (Join-Path $early "ModDropV.HotLoad.dll")
    $earlyZip = Join-Path $PSScriptRoot "publish\ModDropV-$version-early-access-$Runtime.zip"
    if (Test-Path $earlyZip) { Remove-Item $earlyZip }
    Compress-Archive -Path "$early\*" -DestinationPath $earlyZip
    Remove-Item $early -Recurse -Force
    Write-Host "Early access zip -> $earlyZip"
}
