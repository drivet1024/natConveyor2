[CmdletBinding()]
param(
    [string]$DeployPath = 'C:\natconveyor2-dev',
    [string]$TaskName = 'Conveyor.Web',
    [string]$UserId = [Security.Principal.WindowsIdentity]::GetCurrent().Name,
    [uri]$Url = 'http://localhost:5164'
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathFullyQualified($DeployPath)) { throw 'Le dossier doit être absolu.' }
$destination = [IO.Path]::GetFullPath($DeployPath).TrimEnd('\')
if (-not $Url.IsAbsoluteUri -or $Url.Scheme -notin @('http', 'https')) { throw 'URL HTTP(S) attendue.' }
if (Get-ScheduledTask -TaskName $TaskName -TaskPath '\' -ErrorAction SilentlyContinue) {
    throw 'Cette tâche existe déjà. Modifier sa configuration dans le Planificateur Windows.'
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$action = New-ScheduledTaskAction -Execute (Join-Path $destination 'Conveyor.Web.exe') `
    -Argument "--urls `"$($Url.AbsoluteUri.TrimEnd('/'))`" --environment Production" -WorkingDirectory $destination
$principal = New-ScheduledTaskPrincipal -UserId $UserId -LogonType Interactive -RunLevel Limited
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $UserId
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $TaskName -TaskPath '\' -Action $action -Principal $principal `
    -Trigger $trigger -Settings $settings | Out-Null
Write-Host 'Tâche enregistrée. Elle sera démarrée au déploiement ; la session de cet utilisateur doit rester ouverte.'
