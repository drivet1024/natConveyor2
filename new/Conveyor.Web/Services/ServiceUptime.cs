using System.Diagnostics;

namespace Conveyor.Web.Services;

public sealed class ServiceUptime
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public TimeSpan Elapsed => _clock.Elapsed;
}
