using System.Diagnostics;
using System.Text;
using Conveyor.Web.Options;

namespace Conveyor.Web.Services;

public interface IRslinxRestarter
{
    Task RestartAsync(RslinxRestartOptions options, CancellationToken token);
}

public sealed class RslinxRestarter(ILogger<RslinxRestarter> logger) : IRslinxRestarter
{
    public async Task RestartAsync(RslinxRestartOptions options, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Le redémarrage RSLinx exige Windows.");
        if (options.ValidationError() is { } error) throw new InvalidOperationException(error);
        using var process = new Process { StartInfo = CreateStartInfo(options) };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(); // Stop only our helper, never its newly launched RSLinx child.
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Délai de redémarrage RSLinx dépassé. Vérifier son état sur le serveur.");
        }
        var detail = (await errors).Trim();
        await output;
        if (process.ExitCode != 0)
        {
            logger.LogError("Redémarrage RSLinx échoué : {Detail}", detail);
            throw new InvalidOperationException("Redémarrage RSLinx échoué. Vérifier les paramètres, les droits Windows et les journaux.");
        }
        logger.LogInformation("RSLinx relancé en mode {Mode}", options.Mode);
    }

    internal static ProcessStartInfo CreateStartInfo(RslinxRestartOptions options)
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)) })
            info.ArgumentList.Add(argument);
        // Values are data, never interpolated into PowerShell source.
        info.Environment["CONVEYOR_RSLINX_MODE"] = options.Mode;
        info.Environment["CONVEYOR_RSLINX_SERVICE"] = options.ServiceName.Trim();
        info.Environment["CONVEYOR_RSLINX_EXE"] = options.ExecutablePath;
        return info;
    }

    private const string Script = """
        $ErrorActionPreference = 'Stop'
        try {
            if ($env:CONVEYOR_RSLINX_MODE -eq 'Service') {
                $service = Get-Service -Name $env:CONVEYOR_RSLINX_SERVICE -ErrorAction Stop
                if ($service.Status -ne 'Stopped') {
                    Stop-Service -InputObject $service -ErrorAction Stop
                    $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
                }
                Start-Service -InputObject $service -ErrorAction Stop
                $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
            } elseif ($env:CONVEYOR_RSLINX_MODE -eq 'Application') {
                $session = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
                if ($session -eq 0) { throw 'RSLinx application exige que Conveyor.Web tourne dans la session Windows interactive.' }
                $exe = (Get-Item -LiteralPath $env:CONVEYOR_RSLINX_EXE -ErrorAction Stop).FullName
                if ([IO.Path]::GetFileName($exe) -ine 'RSLINX.exe') { throw 'Executable RSLINX.exe requis.' }
                $service = Get-Service -Name $env:CONVEYOR_RSLINX_SERVICE -ErrorAction SilentlyContinue
                if ($service -and $service.Status -ne 'Stopped') { throw 'RSLinx tourne comme service. Choisir le mode Service Windows.' }
                $targets = @([System.Diagnostics.Process]::GetProcessesByName('RSLINX'))
                foreach ($target in $targets) {
                    if ($target.SessionId -ne $session -or $target.MainModule.FileName -ine $exe) {
                        throw 'Une autre instance RSLinx utilise une autre session ou un autre chemin. Aucun processus arrete.'
                    }
                }
                foreach ($target in $targets) {
                    if ($target.HasExited) { continue }
                    $closed = $target.CloseMainWindow()
                    if (-not $closed -or -not $target.WaitForExit(10000)) { $target.Kill() }
                    if (-not $target.WaitForExit(10000)) { throw 'Arret RSLinx non confirme.' }
                }
                $startInfo = New-Object System.Diagnostics.ProcessStartInfo
                $startInfo.FileName = $exe
                $startInfo.WorkingDirectory = [IO.Path]::GetDirectoryName($exe)
                $startInfo.UseShellExecute = $true
                $startInfo.CreateNoWindow = $false
                $startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Normal
                $started = [System.Diagnostics.Process]::Start($startInfo)
                if ($started.WaitForExit(3000)) { throw 'RSLinx quitte immediatement apres son lancement.' }
            } else { throw 'Mode de redemarrage inconnu.' }
            exit 0
        } catch {
            [Console]::Error.WriteLine($_.Exception.Message)
            exit 1
        }
        """;
}
