using System.Diagnostics;
using System.Reflection;

namespace Conveyor.Web.Services;

public interface IApplicationRestartService
{
    bool RestartScheduled { get; }
    void ScheduleRestart();
}

public sealed class ApplicationRestartService(
    IHostApplicationLifetime lifetime,
    IWebHostEnvironment environment,
    ILogger<ApplicationRestartService> logger) : IApplicationRestartService
{
    private int _scheduled;
    public bool RestartScheduled => Volatile.Read(ref _scheduled) == 1;

    public void ScheduleRestart()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(750);
            try
            {
                var startInfo = CreateStartInfo();
                var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Le processus de remplacement n'a pas démarré.");
                logger.LogWarning("Redémarrage demandé. Nouvelle instance {ProcessId} en attente de la fermeture.", process.Id);
                await Task.Delay(250);
                lifetime.StopApplication();
            }
            catch (Exception exception)
            {
                Interlocked.Exchange(ref _scheduled, 0);
                logger.LogError(exception, "Impossible de redémarrer l'application");
            }
        });
    }

    private ProcessStartInfo CreateStartInfo()
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Chemin du processus courant introuvable.");
        var entryAssembly = Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("Assembly principal introuvable.");
        var currentArguments = Environment.GetCommandLineArgs();
        var runningThroughDotnet = string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            WorkingDirectory = environment.ContentRootPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (runningThroughDotnet) startInfo.ArgumentList.Add(entryAssembly);
        foreach (var argument in currentArguments.Skip(1)) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["CONVEYOR_RESTART_WAIT_PID"] = Environment.ProcessId.ToString();
        return startInfo;
    }
}
