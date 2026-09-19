using Conveyor.Web.Options;
using Conveyor.Web.Services;
namespace Conveyor.Web.Tests;
internal sealed class TestConfigurationEditor : IConfigurationEditor
{
    public string FilePath => "";
    public ConveyorOptions GetEditableCopy() => throw new NotSupportedException();
    public Task SaveAsync(ConveyorOptions options, string? connection, CancellationToken token = default) => throw new NotSupportedException();
    public Task SaveShiftAsync(int shiftId) => Task.CompletedTask;
}
