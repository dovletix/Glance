param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [switch]$Off
)
$ErrorActionPreference = "Stop"
$log = Join-Path $env:LOCALAPPDATA "Glance\task-setup.txt"
try {
    if ($Off) {
        Unregister-ScheduledTask -TaskName "Glance" -Confirm:$false -ErrorAction SilentlyContinue
        Set-Content -Path $log -Value "off" -Encoding UTF8
        exit 0
    }
    $who = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $action = New-ScheduledTaskAction -Execute $Exe
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $who
    $trigger.Delay = "PT5S"
    $principal = New-ScheduledTaskPrincipal -UserId $who -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero)
    Register-ScheduledTask -TaskName "Glance" -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    Set-Content -Path $log -Value "ok $who" -Encoding UTF8
    exit 0
}
catch {
    Set-Content -Path $log -Value ($_ | Out-String) -Encoding UTF8
    exit 1
}
