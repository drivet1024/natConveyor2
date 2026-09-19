[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourcePath,
    [Parameter(Mandatory)][string]$DeployPath,
    [Parameter(Mandatory)][string]$TaskName,
    [Parameter(Mandatory)][uri]$HealthUrl
)

$ErrorActionPreference = 'Stop'
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

Stop-ScheduledTask -TaskName $TaskName -TaskPath '\'
# The interface can restart the application outside the original task process.
Get-CimInstance Win32_Process -Filter "Name = 'Conveyor.Web.exe'" | Where-Object {
    $_.ExecutablePath -eq $executable
} | ForEach-Object {
    Stop-Process -Id $_.ProcessId -Force
    Wait-Process -Id $_.ProcessId -Timeout 20 -ErrorAction SilentlyContinue
}
foreach ($file in $files) {
    $target = Join-Path $destination ([IO.Path]::GetRelativePath($source, $file.FullName))
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target -Force
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
