# Stream Machine verification (section 7 of HANDOFF.md). Run as yourself (not elevated) after install.ps1.
#   .\verify.ps1                  unit tests, simulated-controller end-to-end test, service/pipe, XInput read,
#                                 remote access, status
#   .\verify.ps1 -SleepTest       + a real sleep/wake cycle through the agent's own sleep path (simulated
#                                 controller: View+Menu, Sleep, A). A wake timer brings the PC back after 3 minutes.
#                                 The stream launch is suppressed so the host isn't disturbed.
#   .\verify.ps1 -RestartTest     + restarts the PC now (shutdown /r /t 0). Afterwards run .\verify.ps1 -AfterRestart
#   .\verify.ps1 -AfterRestart    checks the service logged "restart, TV left on" for that restart
# A report is written to tests\out\verify-<time>.txt.
[CmdletBinding()]
param([switch]$SleepTest, [switch]$RestartTest, [switch]$AfterRestart, [switch]$SkipUnit)
$ErrorActionPreference = 'Stop'
$Repo = $PSScriptRoot
$Dest = Join-Path $env:ProgramFiles 'StreamMachine'
$Agent = Join-Path $Dest 'StreamMachine.Agent.exe'
$ServiceLog = Join-Path $env:ProgramData 'StreamMachine\logs\service.log'
$Out = Join-Path $Repo 'tests\out'
New-Item -ItemType Directory -Force $Out | Out-Null
$Report = Join-Path $Out ("verify-{0:yyyyMMdd-HHmmss}.txt" -f (Get-Date))
$Marker = Join-Path $Out 'restart-test-started.txt'
$results = [Collections.Generic.List[object]]::new()

function Check([string]$name, [scriptblock]$test, [string]$evidence = '') {
    $ok = $false; $detail = ''
    try { $ok = [bool](& $test) } catch { $detail = $_.Exception.Message }
    if (-not $detail -and $evidence) { try { $detail = (& ([scriptblock]::Create($evidence))) -join ' | ' } catch {} }
    $results.Add([pscustomobject]@{ Check = $name; Result = if ($ok) { 'PASS' } else { 'FAIL' }; Evidence = $detail })
    Write-Host ('{0,-4} {1}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
}
function LogSince([string]$path, [datetime]$since, [string]$pattern) {
    if (-not (Test-Path $path)) { return @() }
    Get-Content $path -Tail 3000 | Where-Object {
        $_ -match '^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)' -and [datetime]$Matches[1] -ge $since.AddSeconds(-1) -and $_ -match $pattern
    }
}

if ($AfterRestart) {
    $since = if (Test-Path $Marker) { [datetime](Get-Content $Marker -Raw).Trim() } else { (Get-Date).AddHours(-1) }
    $lines = LogSince $ServiceLog $since 'restart, TV left on|TV off for shutdown|\[service\] start'
    $lines | ForEach-Object { "  $_" }
    Check 'restart: service logged "restart, TV left on"' { @($lines | Where-Object { $_ -match 'restart, TV left on' }).Count -ge 1 }
    Check 'restart: TV was not turned off' { @($lines | Where-Object { $_ -match 'TV off for shutdown' }).Count -eq 0 }
    Check 'restart: service started again after the restart' { @($lines | Where-Object { $_ -match '\[service\] start' }).Count -ge 1 }
    Remove-Item $Marker -ErrorAction SilentlyContinue
}
else {
    # ------------------------------------------------------------ unit tests
    if (-not $SkipUnit) {
        $t = & (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe') test (Join-Path $Repo 'src\StreamMachine.Tests') --nologo 2>&1 | Out-String
        $summary = ($t -split "`n" | Where-Object { $_ -match 'Passed!|Failed!' }) -join ' '
        Check 'unit tests (SSAP, discovery, TV controller vs fake TV, preshutdown/suspend handlers, pipe, gestures)' { $t -match 'Passed!' -and $t -notmatch 'Failed!' } "'$summary'"
    }

    # ------------------------------------------------------------ installed pieces
    Check 'service StreamMachine running (LocalSystem, automatic)' {
        $s = Get-CimInstance Win32_Service -Filter "Name='StreamMachine'"
        $s.State -eq 'Running' -and $s.StartName -eq 'LocalSystem' -and $s.StartMode -eq 'Auto'
    }
    # The agent is a GUI-subsystem exe: PowerShell only waits for and captures its output when it is piped.
    Check 'service answers on \\.\pipe\StreamMachine' { (& $Agent ping | Out-String) -match 'pong' }
    Check 'agent running in this session, not elevated' {
        $p = Get-CimInstance Win32_Process -Filter "Name='StreamMachine.Agent.exe'" | Where-Object { $_.CommandLine -notmatch 'test-input' }
        $p -and ((Invoke-CimMethod -InputObject @($p)[0] -MethodName GetOwner).User -eq $env:USERNAME)
    }
    Check 'preshutdown timeout configured (10 s)' { (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\StreamMachine').PreshutdownTimeout -eq 10000 }
    Check 'SSH server running' { (Get-Service sshd).Status -eq 'Running' }
    Check 'RustDesk running' { (Get-Service RustDesk).Status -eq 'Running' }

    # ------------------------------------------------------------ simulated controller end-to-end + render test
    $e2e = & pwsh -NoProfile -File (Join-Path $Repo 'tests\agent-e2e.ps1') -Exe $Agent -OutDir (Join-Path $Out 'e2e') 2>&1 | Out-String
    $line = ($e2e -split "`n" | Where-Object { $_ -match '^\d+ passed, \d+ failed' }) -join ''
    Check 'simulated controller end-to-end (overlay, every menu action, mouse mode, Guide, wake flow, watchdog)' { $line -match ' 0 failed' } "'$line'"
    Check 'overlay render test (tests\out\e2e\*.png)' { (Get-ChildItem (Join-Path $Out 'e2e') -Filter '0*-overlay-*.png').Count -ge 4 }

    # ------------------------------------------------------------ real controller
    Write-Host 'Turn your controller on and press a few buttons (5 s)…' -ForegroundColor Yellow
    $pads = & $Agent pads 5 | Out-String
    Check 'XInput read test (controller on)' { $pads -match 'controller on xinput' } "'$($pads.Trim())'"

    # ------------------------------------------------------------ sleep / wake
    if ($SleepTest) {
        $taskName = 'Stream Machine wake test'
        $wakeAt = (Get-Date).AddMinutes(3)
        Register-ScheduledTask -TaskName $taskName -Force -Action (New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/c exit 0') `
            -Trigger (New-ScheduledTaskTrigger -Once -At $wakeAt) `
            -Settings (New-ScheduledTaskSettingsSet -WakeToRun -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries) | Out-Null
        $flagDir = Join-Path $env:LOCALAPPDATA 'StreamMachine'
        New-Item -ItemType Directory -Force $flagDir | Out-Null
        # The real agent is paused for the test; a test instance (same sleep path, stream launch suppressed) drives it.
        Stop-ScheduledTask -TaskName 'Stream Machine Agent' -ErrorAction SilentlyContinue
        Get-Process StreamMachine.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
        $env:STREAMMACHINE_LOGDIR = Join-Path $Out 'sleep-test-logs'
        Remove-Item $env:STREAMMACHINE_LOGDIR -Recurse -Force -ErrorAction SilentlyContinue
        $proc = Start-Process $Agent -ArgumentList '--test-input', '--no-stream' -PassThru
        $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'StreamMachine.AgentTest', [IO.Pipes.PipeDirection]::InOut)
        $pipe.Connect(15000)
        $w = [IO.StreamWriter]::new($pipe); $w.AutoFlush = $true; $r = [IO.StreamReader]::new($pipe)
        $start = Get-Date
        Write-Host "Sleeping through the agent (View+Menu, Sleep, A). Wake timer at $($wakeAt.ToString('HH:mm:ss'))…" -ForegroundColor Yellow
        foreach ($c in 'hold view+menu 1200', 'press up', 'press a') { $w.WriteLine($c); [void]$r.ReadLine() }
        # Wait until the PC has been asleep and come back (the clock jumps), then let wake work finish.
        $deadline = $wakeAt.AddMinutes(4)
        while ((Get-Date) -lt $deadline -and -not (LogSince $ServiceLog $start '\[power\] resume')) { Start-Sleep -Seconds 2 }
        Start-Sleep -Seconds 45
        try { $pipe.Dispose() } catch {}
        $agentLog = Join-Path $env:STREAMMACHINE_LOGDIR 'agent.log'

        $suspend = LogSince $ServiceLog $start '\[power\] suspend handled'
        $off = LogSince $ServiceLog $start '\[tv\] (off|off: )'
        $hdmi = LogSince $ServiceLog $start '\[tv\] HDMI 1'
        Check 'sleep: the agent''s sleep path suspended the PC' { LogSince $ServiceLog $start '\[power\] suspend$' } "LogSince '$ServiceLog' ([datetime]'$start') '\[power\] suspend'"
        Check 'sleep: TV off within 2 s of suspend' { $suspend -and ([int]([regex]::Match(($suspend | Select-Object -Last 1), 'ms=(\d+)').Groups[1].Value) -lt 2000) -and ($suspend -match 'tv_off=done') } "LogSince '$ServiceLog' ([datetime]'$start') 'suspend handled|\[tv\] off'"
        Check 'wake: TV on + HDMI 1' { $hdmi -and ($hdmi -notmatch 'error') } "LogSince '$ServiceLog' ([datetime]'$start') '\[tv\] HDMI 1|turn on result'"
        Check 'wake: agent wake flow ran, stream suppressed (test flag)' { (Select-String $agentLog -Pattern '\[wake\] start' -Quiet) -and (Select-String $agentLog -Pattern '\[wake\].*suppressed' -Quiet) }
        Check 'wake: Punktfunk Console running' { Get-Process punktfunk-console, punktfunk-client -ErrorAction SilentlyContinue }

        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
        Remove-Item Env:STREAMMACHINE_LOGDIR
        Start-ScheduledTask -TaskName 'Stream Machine Agent'
    }

    # ------------------------------------------------------------ status
    $status = & $Agent status | Out-String
    $status | Set-Content (Join-Path $Out 'status.txt')
    Check 'status: all green' { $status -match 'All green\.' } "'see tests\out\status.txt'"
}

''
$results | Format-Table -AutoSize -Wrap | Out-String -Width 220 | Tee-Object -FilePath $Report | Write-Host
$failed = @($results | Where-Object Result -eq 'FAIL').Count
"{0} passed, {1} failed. Report: {2}" -f ($results.Count - $failed), $failed, $Report | Tee-Object -FilePath $Report -Append | Write-Host

if ($RestartTest -and -not $AfterRestart) {
    (Get-Date).ToString('s') | Set-Content $Marker
    Write-Host 'Restarting now. After sign-in run: .\verify.ps1 -AfterRestart' -ForegroundColor Yellow
    shutdown.exe /r /t 0
}
exit $failed
