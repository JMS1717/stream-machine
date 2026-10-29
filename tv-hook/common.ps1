# Shared by install-tv-hook.ps1 / uninstall-tv-hook.ps1.
$SshOpts = @('-o', 'BatchMode=yes', '-o', 'PasswordAuthentication=no', '-o', 'KbdInteractiveAuthentication=no',
             '-o', 'ConnectTimeout=6', '-o', 'StrictHostKeyChecking=accept-new')

function Find-SshKey {
    foreach ($k in 'id_ed25519', 'id_ecdsa', 'id_rsa') {
        $p = Join-Path $HOME ".ssh\$k"
        if (Test-Path $p) { return $p }
    }
    return $null
}

function Test-TvSsh([string]$ip, [string]$key) {
    & ssh -i $key @SshOpts "root@$ip" 'echo ok' 2>$null | Select-String -Quiet '^ok$'
}

function Invoke-TvSsh([string]$ip, [string]$key, [string]$script) {
    $script -replace "`r`n", "`n" | & ssh -i $key @SshOpts "root@$ip" 'sh -s'
    if ($LASTEXITCODE) { throw "ssh to the TV failed ($LASTEXITCODE)" }
}

function Show-SshOwnerSteps([string]$ip, [string]$key) {
    Write-Warning 'No SSH key access to the TV yet. This script uses key authentication only, never a password.'
    @"
Once:
  1. On this PC: ssh-keygen -t ed25519            (accept the defaults)$(if ($key) { "   [a key already exists: $key]" })
  2. Put the public key on the TV as /home/root/.ssh/authorized_keys. With the Homebrew Channel's SSH server,
     allow password login in its settings for a moment, run
       type `$HOME\.ssh\id_ed25519.pub | ssh root@$ip "mkdir -p ~/.ssh && cat >> ~/.ssh/authorized_keys"
     (you type the TV's password yourself), then turn password login off again.
  3. Run this script again.
"@ | Write-Host
}
