[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourcePath,
    [Parameter(Mandatory)][string]$DeployPath,
    [Parameter(Mandatory)][string]$TaskName,
    [Parameter(Mandatory)][uri]$HealthUrl
)

$ErrorActionPreference = 'Stop'

function Invoke-ConveyorFileOperation {
    param([Parameter(Mandatory)][scriptblock]$Operation,
          [Parameter(Mandatory)][string]$TargetPath,
          [int]$TimeoutSeconds = 30)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        try { & $Operation; return }
        catch [IO.IOException] {
            $cause = $_.Exception
            while ($cause.InnerException) { $cause = $cause.InnerException }
            $code = $cause.HResult -band 0xffff
            if ($code -notin @(32, 33)) { throw }
            if ($watch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                throw "Fichier toujours verrouillé : $TargetPath. Vérifier les autres instances de Conveyor et les droits du compte runner. Aucun autre programme n’a été arrêté."
            }
            Start-Sleep -Milliseconds 500
        }
    }
}

function Get-ConveyorDeploymentProcess {
    param([string]$Executable)
    foreach ($candidate in Get-CimInstance Win32_Process -Filter "Name = 'Conveyor.Web.exe'") {
        if ([string]::IsNullOrWhiteSpace($candidate.ExecutablePath)) {
            throw "Impossible de vérifier le chemin du processus Conveyor PID $($candidate.ProcessId). Donner au compte runner les droits de lecture et d’arrêt sur la session Conveyor."
        }
        if ($candidate.ExecutablePath -eq $Executable) {
            Get-Process -Id $candidate.ProcessId -ErrorAction SilentlyContinue
        }
    }
}
if ([string]::IsNullOrWhiteSpace($TaskName)) { throw 'CONVEYOR_TASK_NAME est obligatoire.' }
if (-not $HealthUrl.IsAbsoluteUri -or $HealthUrl.Scheme -notin @('http', 'https')) {
    throw 'CONVEYOR_HEALTH_URL doit être une URL HTTP(S) absolue.'
}
$source = (Resolve-Path -LiteralPath $SourcePath).Path.TrimEnd('\')
if (-not [IO.Path]::IsPathFullyQualified($DeployPath)) { throw 'Le dossier de déploiement doit être absolu.' }
$destination = [IO.Path]::GetFullPath($DeployPath).TrimEnd('\')
if ($destination -eq [IO.Path]::GetPathRoot($destination).TrimEnd('\')) { throw 'La racine du disque est interdite.' }
if ($source -eq $destination -or $source.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $destination.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Les dossiers source et destination doivent être séparés.'
}
if (-not (Test-Path -LiteralPath (Join-Path $source 'Conveyor.Web.exe'))) { throw 'Publication Conveyor.Web.exe introuvable.' }
$task = Get-ScheduledTask -TaskName $TaskName -TaskPath '\'
$executable = Join-Path $destination 'Conveyor.Web.exe'
if ($task.Actions.Count -ne 1 -or $task.Actions[0].Execute.Trim('"') -ne $executable -or
    $task.Actions[0].WorkingDirectory -ne $destination) {
    throw 'La tâche doit lancer Conveyor.Web.exe avec le dossier de déploiement comme répertoire de travail.'
}
if (-not (Test-Path -LiteralPath (Join-Path $destination 'appsettings.json'))) {
    throw 'Installer et vérifier appsettings.json dans le dossier cible avant le premier déploiement.'
}

# Preserve local settings, including credentials and the interface overrides.
$files = @(Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object {
    $_.Name -notlike 'appsettings*.json' -and $_.Name -notlike 'conveyor.settings.json*'
})
foreach ($file in $files) {
    $target = Join-Path $destination ([IO.Path]::GetRelativePath($source, $file.FullName))
    if (-not [IO.Path]::GetFullPath($target).StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Un fichier sort du dossier de déploiement.'
    }
}

if (-not $task.Settings.Enabled) { throw 'La tâche Conveyor est désactivée. La réactiver avant le déploiement.' }
# Capture handles before stopping the task: a terminating process can disappear
# from CIM while its loaded DLLs are still in use.
$processes = @(Get-ConveyorDeploymentProcess -Executable $executable)
Disable-ScheduledTask -TaskName $TaskName -TaskPath '\' | Out-Null
try {
    Stop-ScheduledTask -TaskName $TaskName -TaskPath '\'
    $stopWatch = [Diagnostics.Stopwatch]::StartNew()
    do {
        foreach ($process in $processes) {
            if (-not $process.HasExited) {
                try { $process.Kill() }
                catch { if (-not $process.HasExited) { throw } }
                if (-not $process.WaitForExit(20000)) {
                    throw "Conveyor PID $($process.Id) ne s’arrête pas. Aucun fichier n’a été remplacé."
                }
            }
            $process.Dispose()
        }
        Start-Sleep -Milliseconds 500
        # Include replacement instances started by the application's restart button.
        $processes = @(Get-ConveyorDeploymentProcess -Executable $executable)
        $taskRunning = (Get-ScheduledTask -TaskName $TaskName -TaskPath '\').State -eq 'Running'
        if (($processes.Count -gt 0 -or $taskRunning) -and $stopWatch.Elapsed.TotalSeconds -ge 30) {
            throw "Conveyor redémarre ou reste actif. Aucun fichier n’a été remplacé."
        }
    } while ($processes.Count -gt 0 -or $taskRunning)

    # Check all existing targets before modifying any file.
    foreach ($file in $files) {
        $target = Join-Path $destination ([IO.Path]::GetRelativePath($source, $file.FullName))
        if (Test-Path -LiteralPath $target) {
            Invoke-ConveyorFileOperation -TargetPath $target -Operation {
                $handle = [IO.File]::Open($target, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                $handle.Dispose()
            }
        }
    }
    foreach ($file in $files) {
        $target = Join-Path $destination ([IO.Path]::GetRelativePath($source, $file.FullName))
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Invoke-ConveyorFileOperation -TargetPath $target -Operation {
            [IO.File]::Copy($file.FullName, $target, $true)
        }
    }
}
finally {
    Enable-ScheduledTask -TaskName $TaskName -TaskPath '\' | Out-Null
}
Start-ScheduledTask -TaskName $TaskName -TaskPath '\'
$deadline = [DateTime]::UtcNow.AddSeconds(60)
do {
    Start-Sleep -Seconds 2
    try {
        $process = Get-CimInstance Win32_Process -Filter "Name = 'Conveyor.Web.exe'" | Where-Object { $_.ExecutablePath -eq $executable }
        $response = Invoke-WebRequest -Uri $HealthUrl -TimeoutSec 5 -UseBasicParsing
        if ($process -and $response.StatusCode -eq 200) {
            Write-Host 'Conveyor déployé et accessible.'
            exit 0
        }
    }
    catch { Write-Verbose $_.Exception.Message }
} while ([DateTime]::UtcNow -lt $deadline)
throw 'Conveyor ne répond pas après le déploiement. Vérifier la tâche planifiée et les paramètres locaux.'
