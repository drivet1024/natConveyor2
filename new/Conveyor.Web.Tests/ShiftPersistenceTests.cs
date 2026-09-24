using Conveyor.Web.Options;
using Conveyor.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace Conveyor.Web.Tests;

public sealed class ShiftPersistenceTests
{
    [Fact]
    public async Task MaintenanceModeSurvivesRestartAndShiftChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), "conveyor-mode-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var options = new ConveyorOptions { General = new() { Name = "Gilmore", DepotId = 28, ShiftId = 1 } };
            var editor = new ConfigurationEditor(Microsoft.Extensions.Options.Options.Create(options), new TestEnvironment { ContentRootPath = directory });
            await editor.SaveMaintenanceAsync(true);
            await editor.SaveShiftAsync(2);
            var configuration = new ConfigurationBuilder().AddJsonFile(editor.FilePath).Build();
            var restored = configuration.GetSection("Conveyor").Get<ConveyorOptions>()!;
            Assert.True(restored.General!.Maintenance);
            Assert.Equal(2, restored.General.ShiftId);
            Assert.Equal(28, restored.General.DepotId);
            await editor.SaveMaintenanceAsync(false);
            configuration.Reload();
            Assert.False(configuration.GetValue<bool>("Conveyor:General:Maintenance"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedShiftIsLoadedAfterRestartAndPreservesOtherSettings(bool existing)
    {
        var directory = Path.Combine(Path.GetTempPath(), "conveyor-shift-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var options = new ConveyorOptions { General = new() { Name = "QC", DepotId = 2, ShiftId = 1 }, Lines = [new() { Id = 0 }, new() { Id = 1 }] };
            var editor = new ConfigurationEditor(Microsoft.Extensions.Options.Options.Create(options), new TestEnvironment { ContentRootPath = directory });
            if (existing)
                await File.WriteAllTextAsync(editor.FilePath, """
                    {"Conveyor":{"general":{"name":"Saved QC","depotId":2,"shiftId":1},"CustomSetting":"preserved"}}
                    """);
            await editor.SaveShiftAsync(2);
            var config = new ConfigurationBuilder().AddJsonFile(editor.FilePath).Build();
            var restored = config.GetSection("Conveyor").Get<ConveyorOptions>()!;
            restored.Lines = options.Lines;
            restored.ApplyGlobalSorting();
            Assert.Equal(2, restored.General!.ShiftId);
            Assert.Equal(2, restored.General.DepotId);
            Assert.Equal(existing ? "Saved QC" : "QC", restored.General.Name);
            Assert.All(restored.Lines, line => Assert.Equal(2, line.ShiftId));
            if (existing) Assert.Equal("preserved", config["Conveyor:CustomSetting"]);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Test";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public string WebRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
