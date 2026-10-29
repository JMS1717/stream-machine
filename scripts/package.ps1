# Builds the release downloads into dist/:
#   StreamMachine-windows-x64.zip   (Install.cmd, install.ps1, uninstall.ps1, bin\*.exe, TV hook)
#   stream-machine-linux-x64.tar.gz (install.sh, uninstall.sh, bin/stream-machine-service, bin/streammachine)
#   install.ps1, install.sh         (the one-line installers download the archives above)
param([string]$Version = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
$dotnet = if (Test-Path "$env:ProgramFiles\dotnet\dotnet.exe") { "$env:ProgramFiles\dotnet\dotnet.exe" } else { 'dotnet' }
if (Test-Path $dist) { Get-ChildItem $dist | Remove-Item -Recurse -Force }
New-Item -ItemType Directory -Force $dist | Out-Null
$Version = $Version.TrimStart('v')
$props = @()
if ($Version) { $props = @("-p:Version=$Version") }

function Publish($project, $rid, $out) {
    & $dotnet publish (Join-Path $root "src\$project") -c Release -r $rid --self-contained true -p:PublishSingleFile=true -p:DebugType=none @props -o $out --nologo -v quiet
    if ($LASTEXITCODE) { throw "publish $project ($rid) failed" }
}

# Windows
$win = Join-Path $dist 'windows'
foreach ($p in 'StreamMachine.Service', 'StreamMachine.Agent', 'StreamMachine.Cli') { Publish $p 'win-x64' (Join-Path $win 'bin') }
foreach ($f in 'install.ps1', 'uninstall.ps1', 'Install.cmd', 'Uninstall.cmd', 'install-tv-hook.ps1', 'uninstall-tv-hook.ps1', 'README.md', 'LICENSE') { Copy-Item (Join-Path $root $f) $win }
Copy-Item (Join-Path $root 'tv-hook') $win -Recurse
Compress-Archive -Path (Join-Path $win '*') -DestinationPath (Join-Path $dist 'StreamMachine-windows-x64.zip') -Force

# Linux
$lin = Join-Path $dist 'linux'
Publish 'StreamMachine.Service' 'linux-x64' (Join-Path $lin 'pub')
Publish 'StreamMachine.Cli' 'linux-x64' (Join-Path $lin 'pub')
New-Item -ItemType Directory -Force (Join-Path $lin 'pkg\bin') | Out-Null
Copy-Item (Join-Path $lin 'pub\StreamMachine.Service') (Join-Path $lin 'pkg\bin\stream-machine-service')
Copy-Item (Join-Path $lin 'pub\streammachine') (Join-Path $lin 'pkg\bin\streammachine')
foreach ($f in 'install.sh', 'uninstall.sh', 'README.md', 'LICENSE') {
    # LF line endings for the shell scripts whatever git did on checkout
    [IO.File]::WriteAllText((Join-Path $lin "pkg\$f"), ((Get-Content (Join-Path $root $f) -Raw) -replace "`r`n", "`n"))
}
tar -czf (Join-Path $dist 'stream-machine-linux-x64.tar.gz') -C (Join-Path $lin 'pkg') .
if ($LASTEXITCODE) { throw 'tar failed' }

Copy-Item (Join-Path $root 'install.ps1') $dist
[IO.File]::WriteAllText((Join-Path $dist 'install.sh'), ((Get-Content (Join-Path $root 'install.sh') -Raw) -replace "`r`n", "`n"))
Get-ChildItem $dist -File | ForEach-Object { '{0,-40} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
