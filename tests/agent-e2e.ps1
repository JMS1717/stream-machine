# End-to-end test of StreamMachine.Agent driven by simulated controller presses.
# Starts the agent with --test-input --dry-run (sleep/shutdown/restart/TV/volume are logged, not done) and walks every
# overlay flow through the real gesture engine, overlay and action code. Screenshots go to -OutDir.
#   pwsh -File tests\agent-e2e.ps1 [-Exe path\StreamMachine.Agent.exe] [-OutDir dir]
param(
    [string]$Exe = '',
    [string]$OutDir = (Join-Path $PSScriptRoot 'out')
)
$ErrorActionPreference = 'Stop'
if (-not $Exe) {
    $Exe = Get-ChildItem (Join-Path $PSScriptRoot '..\src\StreamMachine.Agent\bin') -Recurse -Filter StreamMachine.Agent.exe |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
New-Item -ItemType Directory -Force $OutDir | Out-Null
$env:STREAMMACHINE_LOGDIR = Join-Path $OutDir 'logs'
Remove-Item $env:STREAMMACHINE_LOGDIR -Recurse -Force -ErrorAction SilentlyContinue

$results = [Collections.Generic.List[object]]::new()
function Check([string]$name, [scriptblock]$test, [string]$detail = '') {
    $ok = $false
    try { $ok = [bool](& $test) } catch { $detail = "$detail $($_.Exception.Message)" }
    $results.Add([pscustomobject]@{ Test = $name; Result = if ($ok) { 'PASS' } else { 'FAIL' }; Detail = $detail })
    '{0,-4} {1}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name | Write-Host
}

$proc = Start-Process $Exe -ArgumentList '--test-input', '--dry-run', '--no-stream', '--no-watchdog' -PassThru
$pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'StreamMachine.AgentTest', [IO.Pipes.PipeDirection]::InOut)
$pipe.Connect(15000)
$reader = [IO.StreamReader]::new($pipe)
$writer = [IO.StreamWriter]::new($pipe); $writer.AutoFlush = $true
function T([string]$cmd) { $writer.WriteLine($cmd); $reply = $reader.ReadLine() | ConvertFrom-Json; if (-not $reply.ok) { throw "test command '$cmd' failed: $($reply.error)" }; $reply }
function Q { (T 'query').state }
function Press([string]$buttons, [int]$times = 1) { for ($i = 0; $i -lt $times; $i++) { T "press $buttons" | Out-Null } }

Add-Type -AssemblyName System.Drawing, System.Windows.Forms
function Capture-Screen([string]$path) {
    $b = [Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = [Drawing.Bitmap]::new($b.Width, $b.Height)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [Drawing.Point]::Empty, $b.Size)
    $bmp.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

try {
    Start-Sleep -Milliseconds 800
    $s = Q
    Check 'agent starts with overlay closed' { -not $s.overlayOpen }

    # --- opening gesture ---
    T 'hold view+menu 600' | Out-Null
    Check 'View+Menu for 0.6 s does not open' { -not (Q).overlayOpen }
    T 'hold view+menu 1250' | Out-Null
    $s = Q
    Check 'View+Menu held 1 s opens the overlay on Home' { $s.overlayOpen -and $s.selected -eq 'Home' }
    Start-Sleep -Milliseconds 400
    Capture-Screen (Join-Path $OutDir '01-screen-overlay-open.png')
    T "render $(Join-Path $OutDir '02-overlay-home.png')" | Out-Null
    Check 'overlay renders to PNG' { (Get-Item (Join-Path $OutDir '02-overlay-home.png')).Length -gt 50KB }

    # --- navigation + volume slider ---
    Press 'down' 2
    Check 'D-pad down twice selects TV volume' { (Q).selected -eq 'Volume' }
    Press 'right'
    Check 'volume does nothing until the TV has reported it' { (Q).volumeTarget -eq 0 -and (Q).toast -like '*Checking*' }
    T 'fake-volume 5' | Out-Null
    Press 'right'
    $s = Q
    Check 'Right raises the volume target by one (5 -> 6)' { $s.volumeTarget -eq 6 -and $s.lastAction -eq 'volume-set-6' }
    Press 'left'; Press 'left'
    Check 'Left lowers it (6 -> 4)' { (Q).volumeTarget -eq 4 }
    Start-Sleep -Milliseconds 400
    Check 'the newest target is sent once, after the presses (debounced)' {
        $sent = @(Select-String -Path (Join-Path $env:STREAMMACHINE_LOGDIR 'agent.log') -Pattern 'dry-run: volume set (\d+)' | ForEach-Object { $_.Matches[0].Groups[1].Value })
        $sent.Count -ge 1 -and $sent[-1] -eq '4'
    }
    T "render $(Join-Path $OutDir '02b-overlay-volume.png')" | Out-Null
    foreach ($i in 1..20) { Press 'right' }
    Check 'the slider stops at its top (15)' { (Q).volumeTarget -eq 15 -and (Q).volumeMax -eq 15 }
    foreach ($i in 1..20) { Press 'left' }
    Check 'and at 1 at the bottom' { (Q).volumeTarget -eq 1 }
    Press 'a'
    Check 'A on TV volume = mute' { (Q).lastAction -eq 'volume-mute' }
    Press 'rb'
    Check 'RB anywhere = volume up' { (Q).volumeTarget -eq 2 }
    Press 'lb'
    Check 'LB anywhere = volume down' { (Q).volumeTarget -eq 1 }
    Press 'y'
    Check 'Y = mute' { (Q).lastAction -eq 'volume-mute' }
    T 'stick 0 -0.9 0 0 120' | Out-Null
    Check 'left stick down navigates too' { (Q).selected -eq 'TvOff' }
    T 'stick 0 0.9 0 0 120' | Out-Null
    Check 'left stick up navigates back' { (Q).selected -eq 'Volume' }

    # --- confirm flow: Restart ---
    Press 'up' 3
    Check 'Up from Home wraps to Sleep (power row)' { (Q).selected -eq 'Sleep' }
    Press 'right'
    Check 'Right moves to Restart' { (Q).selected -eq 'Restart' }
    Press 'a'
    $s = Q
    Check 'first A on Restart only arms it' { $s.armed -contains 'Restart' -and $s.lastAction -ne 'restart' -and $s.overlayOpen }
    T "render $(Join-Path $OutDir '03-overlay-restart-armed.png')" | Out-Null
    Press 'left'
    Check 'moving away disarms' { @((Q).armed).Count -eq 0 }
    Press 'right'
    Press 'a'; Press 'a'
    Check 'second A restarts (dry-run)' { (Q).lastAction -eq 'restart' }
    Press 'b'
    Check 'B closes the overlay' { -not (Q).overlayOpen }

    # --- Shut down ---
    T 'hold view+menu 1100' | Out-Null
    Press 'up'; Press 'left'
    Check 'Left from Sleep wraps to Shut down' { (Q).selected -eq 'Shutdown' }
    T "render $(Join-Path $OutDir '04-overlay-shutdown-selected.png')" | Out-Null
    Press 'a'; Press 'a'
    Check 'Shut down needs two A presses (dry-run)' { (Q).lastAction -eq 'shutdown' }
    Press 'b'

    # --- Sleep ---
    T 'hold view+menu 1100' | Out-Null
    Press 'up'; Press 'a'
    $s = Q
    Check 'Sleep closes the overlay and sleeps (dry-run)' { $s.lastAction -eq 'sleep' -and -not $s.overlayOpen }
    Start-Sleep -Milliseconds 700
    Check 'sleep reached SystemActions.Sleep' { Select-String -Path (Join-Path $env:STREAMMACHINE_LOGDIR 'agent.log') -Pattern 'dry-run: sleep' -Quiet }

    # --- Health page ---
    T 'hold view+menu 1100' | Out-Null
    Press 'down' 7
    Check 'Health is the 8th item' { (Q).selected -eq 'Health' }
    Press 'a'
    Check 'A opens the Health page' { (Q).page -eq 'health' }
    Start-Sleep -Seconds 10
    T "render $(Join-Path $OutDir '05-overlay-health.png')" | Out-Null
    Press 'b'
    $s = Q
    Check 'B on Health returns to the menu' { $s.page -eq 'menu' -and $s.overlayOpen }
    Press 'b'

    # --- Settings page ---
    T 'hold view+menu 1100' | Out-Null
    Press 'down' 8
    Check 'Settings is the 9th item' { (Q).selected -eq 'Settings' }
    Press 'a'
    $s = Q
    Check 'A opens the Settings page on the streaming client' { $s.page -eq 'settings' -and $s.selectedSetting -eq 'client' }
    T "render $(Join-Path $OutDir '07-overlay-settings.png')" | Out-Null
    function GoTo([string]$key) { foreach ($i in 1..40) { if ((Q).selectedSetting -eq $key) { return $true }; Press 'down' }; return $false }
    Check 'the stream-end settings are there' { (GoTo 'exit-app') -and (GoTo 'sleep-host') -and (GoTo 'host-os') }
    GoTo 'sleep-host' | Out-Null
    Press 'a'
    Check 'A turns "put the host to sleep" on' { (Q).settingValue -eq 'on' }
    Press 'a'
    Check 'and off again' { (Q).settingValue -eq 'off' }
    GoTo 'guide-hold' | Out-Null
    $h0 = (Q).guideHoldMs
    Press 'right'
    $s = Q
    Check 'Right raises the Xbox-button hold time and applies it live' { $s.guideHoldMs -gt $h0 -and $s.toast -like '*hold time*' }
    T "render $(Join-Path $OutDir '07b-overlay-settings-controller.png')" | Out-Null
    Press 'left'
    Check 'Left puts it back' { (Q).guideHoldMs -eq $h0 }
    GoTo 'guide-opens' | Out-Null
    Press 'a'
    Check 'A turns "hold Xbox button for this menu" off' { -not (Q).guideEnabled }
    Press 'a'
    Check 'and back on' { (Q).guideEnabled }
    GoTo 'tv-input' | Out-Null
    Press 'right'
    Check 'changing a TV setting goes to the service (dry-run)' { Select-String -Path (Join-Path $env:STREAMMACHINE_LOGDIR 'agent.log') -Pattern 'dry-run: service setting key=tv-input' -Quiet }
    Press 'left'
    GoTo 'tv-setup' | Out-Null
    Press 'a'
    Check 'A on "Set up TVs" opens the TV page' { (Q).page -eq 'tvs' }
    Start-Sleep -Milliseconds 600
    T "render $(Join-Path $OutDir '08-overlay-tvs.png')" | Out-Null
    Press 'b'
    Check 'B on the TV page returns to Settings' { (Q).page -eq 'settings' }
    Press 'up'
    Check 'Up from the first setting wraps to the last (skipping headers)' { (Q).selectedSetting -eq 'version' }
    Press 'b'
    $s = Q
    Check 'B on Settings returns to the menu' { $s.page -eq 'menu' -and $s.overlayOpen }
    Press 'b'

    # --- stream end: leave Big Picture on the host ---
    T 'fake-streaming on' | Out-Null
    Start-Sleep -Milliseconds 1500
    T 'fake-streaming off' | Out-Null
    Start-Sleep -Seconds 3
    Check 'a stream that just ended is given 5 s (a reconnect would cancel)' { (Q).lastAction -notlike 'stream-end*' }
    Start-Sleep -Seconds 4
    Check 'after 5 s it leaves Big Picture on the host' {
        (Q).lastAction -eq 'stream-end exit-app' -and
        (Select-String -Path (Join-Path $env:STREAMMACHINE_LOGDIR 'agent.log') -Pattern 'dry-run: stream ended: close it on the host' -Quiet)
    }
    Start-Sleep -Seconds 6
    Check 'the host is not put to sleep while that setting is off' { -not (Select-String -Path (Join-Path $env:STREAMMACHINE_LOGDIR 'agent.log') -Pattern 'stream ended: put the host to sleep' -Quiet) }
    T 'fake-streaming default' | Out-Null

    # --- Console mode toggle ---
    T 'hold view+menu 1100' | Out-Null
    Press 'down' 6
    $before = (Q).consoleMode
    Press 'a'
    Check 'A toggles console mode' { (Q).consoleMode -ne $before }
    Press 'a'
    Check 'A again restores console mode' { (Q).consoleMode -eq $before }
    Press 'b'

    # --- Mouse mode ---
    T 'hold view+menu 1100' | Out-Null
    Press 'down' 4
    Check 'Desktop mouse mode is the 5th item' { (Q).selected -eq 'MouseMode' }
    Press 'a'
    $s = Q
    Check 'A turns mouse mode on and closes the overlay' { $s.mouseMode -and -not $s.overlayOpen }
    Start-Sleep -Milliseconds 300
    Capture-Screen (Join-Path $OutDir '06-screen-mouse-mode-badge.png')
    $m0 = (Q).mouseMoves
    T 'stick 0.9 0.2 0 0 400' | Out-Null
    Check 'left stick moves the pointer in mouse mode' { (Q).mouseMoves -gt $m0 }
    $c0 = (Q).mouseClicks
    Press 'a'; Press 'b'
    Check 'A and B click in mouse mode' { (Q).mouseClicks -eq $c0 + 2 }
    T 'hold view+menu 1100' | Out-Null
    Check 'View+Menu still opens the overlay from mouse mode' { (Q).overlayOpen }
    Press 'down' 4; Press 'a'
    Check 'A on Desktop mouse mode turns it off' { -not (Q).mouseMode }

    # --- Guide ---
    T 'hold guide 650' | Out-Null
    Check 'holding Guide 0.4 s opens the overlay' { (Q).overlayOpen }
    Press 'b'
    T 'hold guide 250' | Out-Null
    Check 'a short Guide press does not' { -not (Q).overlayOpen }

    # --- Home / reconnect / TV off / keyboard (dry-run) ---
    foreach ($case in @(@{ Down = 0; Action = 'home' }, @{ Down = 1; Action = 'reconnect' }, @{ Down = 3; Action = 'tv-off' }, @{ Down = 5; Action = 'keyboard' })) {
        T 'hold view+menu 1100' | Out-Null
        if ($case.Down) { Press 'down' $case.Down }
        Press 'a'
        $s = Q
        Check "A runs '$($case.Action)' and closes the overlay" { $s.lastAction -eq $case.Action -and -not $s.overlayOpen }
    }
    Check 'Reconnect respected --no-stream' { Select-String -Path (Join-Path $env:STREAMMACHINE_LOGDIR 'agent.log') -Pattern 'stream launch suppressed' -Quiet }

    # --- wake flow ---
    T 'wake' | Out-Null
    $s = Q
    Check 'wake flow runs and suppresses the stream in test mode' { $s.wakeFlows -eq 1 -and (Select-String -Path (Join-Path $env:STREAMMACHINE_LOGDIR 'agent.log') -Pattern '\[wake\].*suppressed' -Quiet) }

    # --- keep-awake ---
    Check 'controller input reset the idle timer (SetThreadExecutionState)' { (Q).keepAwakeCalls -ge 1 }
}
finally {
    try { $pipe.Dispose() } catch {}
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
}

# --- watchdog (separate run: console mode on, no Console running -> reopened after 15 s; dry-run) ---
if (-not (Get-Process punktfunk-console, punktfunk-client, punktfunk-session -ErrorAction SilentlyContinue)) {
    $proc = Start-Process $Exe -ArgumentList '--test-input', '--dry-run', '--no-stream', '--no-real-input' -PassThru
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'StreamMachine.AgentTest', [IO.Pipes.PipeDirection]::InOut)
    $pipe.Connect(15000)
    $reader = [IO.StreamReader]::new($pipe); $writer = [IO.StreamWriter]::new($pipe); $writer.AutoFlush = $true
    try {
        T 'console-mode on' | Out-Null
        Start-Sleep -Seconds 17
        Check 'home watchdog reopens the Console after 15 s with nothing running' { (Q).homeReopens -ge 1 }
    }
    finally { try { $pipe.Dispose() } catch {}; Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
else { Write-Host 'SKIP watchdog test (Punktfunk is running)' }

''
$results | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
$failed = @($results | Where-Object Result -eq 'FAIL').Count
"{0} passed, {1} failed" -f ($results.Count - $failed), $failed | Write-Host
exit $failed
