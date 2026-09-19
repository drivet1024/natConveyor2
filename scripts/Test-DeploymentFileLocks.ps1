$ErrorActionPreference = 'Stop'
# Load only the retry function, without invoking deployment or scheduled tasks.
$parseTokens = $null
$parseErrors = $null
$tree = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'Deploy-Conveyor.ps1'), [ref]$parseTokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$function = $tree.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Invoke-ConveyorFileOperation'
}, $true)
. ([scriptblock]::Create($function.Extent.Text))

Add-Type -TypeDefinition @'
using System.IO;
using System.Threading.Tasks;
public static class ConveyorLockTest {
    public static async Task HoldAsync(string path) {
        using (var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            await Task.Delay(1200);
        }
    }
}
'@
$testFile = [IO.Path]::GetTempFileName()
try {
    $lock = [ConveyorLockTest]::HoldAsync($testFile)
    Invoke-ConveyorFileOperation -TargetPath $testFile -TimeoutSeconds 5 -Operation {
        [IO.File]::WriteAllText($testFile, 'retry succeeded')
    }
    $null = $lock.GetAwaiter().GetResult()
    if ([IO.File]::ReadAllText($testFile) -ne 'retry succeeded') { throw 'Retry failed.' }

    $handle = [IO.File]::Open($testFile, 'Open', 'ReadWrite', 'None')
    try {
        $failed = $false
        try {
            Invoke-ConveyorFileOperation -TargetPath $testFile -TimeoutSeconds 0 -Operation {
                [IO.File]::WriteAllText($testFile, 'must not be written')
            }
        }
        catch {
            if ($_.Exception.Message -notlike '*toujours verrouillé*') { throw }
            $failed = $true
        }
        if (-not $failed) { throw 'Persistent lock was ignored.' }
    }
    finally { $handle.Dispose() }
    if ([IO.File]::ReadAllText($testFile) -ne 'retry succeeded') { throw 'Locked file was modified.' }

    $failed = $false
    try {
        Invoke-ConveyorFileOperation -TargetPath $testFile -Operation {
            throw [UnauthorizedAccessException]::new('Access denied test')
        }
    }
    catch [UnauthorizedAccessException] { $failed = $true }
    if (-not $failed) { throw 'Access denied was ignored.' }
    Write-Host 'PASS: temporary lock, persistent lock, unchanged file, access denied.'
}
finally { Remove-Item -LiteralPath $testFile -Force }
