using System.Text.Json;
using Conveyor.Web.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Conveyor.Web.Tests;

public sealed class GlobalSortingTests
{
    [Fact]
    public void GeneralSettingsMigrateFromPrimaryLineAndPreserveIndividualPlcTags()
    {
        var options = new ConveyorOptions
        {
            Lines = [new() { Id = 1, Name = "Secondaire", DepotId = 99, Plc = new() { ChuteTag = "SECOND", TransferTag = "READ_SECOND", ScaleFaultTag = "FAUTE_M30" } },
                new() { Id = 0, Name = "Site", DepotId = 7, ShiftId = 3, Plc = new() { ChuteTag = "MAIN", TransferTag = "READ_MAIN", ScaleFaultTag = "FAUTE_M31" } }]
        };
        options.ApplyGlobalSorting();
        Assert.Equal("Site", options.General!.Name);
        Assert.Equal(7, options.General.DepotId);
        Assert.Equal(3, options.General.ShiftId);
        options.General.Name = "Québec";
        options.General.DepotId = 8;
        options.General.ShiftId = 4;
        var restored = JsonSerializer.Deserialize<ConveyorOptions>(JsonSerializer.Serialize(options))!;
        restored.ApplyGlobalSorting();
        Assert.All(restored.Lines, line =>
        {
            Assert.Equal("Québec", line.Name);
            Assert.Equal(8, line.DepotId);
            Assert.Equal(4, line.ShiftId);
        });
        Assert.Equal("SECOND", restored.Lines[0].Plc.ChuteTag);
        Assert.Equal("MAIN", restored.Lines[1].Plc.ChuteTag);
        Assert.Equal("READ_SECOND", restored.Lines[0].Plc.TransferTag);
        Assert.Equal("READ_MAIN", restored.Lines[1].Plc.TransferTag);
        Assert.Equal("FAUTE_M30", restored.Lines[0].Plc.ScaleFaultTag);
        Assert.Equal("FAUTE_M31", restored.Lines[1].Plc.ScaleFaultTag);
    }

    [Fact]
    public void LegacyConfigurationUsesPrimaryLineAndPreservesDeviceSettings()
    {
        var options = new ConveyorOptions
        {
            Lines = [new() { Id = 1, RejectedChute = 40, CameraPort = 5100 },
                new() { Id = 0, RejectedChute = 12, NoReadChute = 3, CameraPort = 5000,
                    CorrelationDelayMs = 200, CorrelationWindowMs = 2500,
                    MaximumWeight = 90, MaximumDimension = 80,
                    PostalCodeSort = false, ValidateDimensionsAndWeight = false, EnableCode86 = true }]
        };
        options.ApplyGlobalSorting();
        Assert.All(options.Lines, line =>
        {
            Assert.Equal(12, line.RejectedChute);
            Assert.Equal(3, line.NoReadChute);
            Assert.Equal(200, line.CorrelationDelayMs);
            Assert.Equal(2500, line.CorrelationWindowMs);
            Assert.Equal(90, line.MaximumWeight);
            Assert.Equal(80, line.MaximumDimension);
            Assert.False(line.PostalCodeSort);
            Assert.False(line.ValidateDimensionsAndWeight);
            Assert.True(line.EnableCode86);
        });
        Assert.Equal(5100, options.Lines[0].CameraPort);
        Assert.Equal(5000, options.Lines[1].CameraPort);
    }

    [Fact]
    public void GlobalSettingsOverrideOldLineValuesAfterConfigurationBinding()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Conveyor:Sorting:RejectedChute"] = "22",
            ["Conveyor:Sorting:PostalCodeSort"] = "false",
            ["Conveyor:Lines:0:Id"] = "0",
            ["Conveyor:Lines:0:RejectedChute"] = "10",
            ["Conveyor:Lines:1:Id"] = "1",
            ["Conveyor:Lines:1:RejectedChute"] = "11"
        }).Build();
        var services = new ServiceCollection();
        services.AddOptions<ConveyorOptions>().Bind(configuration.GetSection("Conveyor"))
            .PostConfigure(options => options.ApplyGlobalSorting());
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ConveyorOptions>>().Value;
        Assert.All(options.Lines, line =>
        {
            Assert.Equal(22, line.RejectedChute);
            Assert.False(line.PostalCodeSort);
        });
    }

    [Fact]
    public void EditedGlobalSettingsSurviveSerializationAndApplyToBothLines()
    {
        var options = new ConveyorOptions { Lines = [new() { Id = 0 }, new() { Id = 1 }] };
        options.ApplyGlobalSorting();
        options.Sorting!.MaximumWeight = 75;
        options.Sorting.EnableCode86 = true;
        var restored = JsonSerializer.Deserialize<ConveyorOptions>(JsonSerializer.Serialize(options))!;
        restored.ApplyGlobalSorting();
        Assert.All(restored.Lines, line =>
        {
            Assert.Equal(75, line.MaximumWeight);
            Assert.True(line.EnableCode86);
        });
    }
}
