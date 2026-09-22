using Conveyor.Web.Options;
using Conveyor.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace Conveyor.Web.Tests;

public sealed class PlcConfigurationTests
{
    [Theory]
    [InlineData("Dde")]
    [InlineData("OpcDa")]
    [InlineData("Tcp")]
    [InlineData("opcda")]
    public void ValidProtocolsAreAccepted(string protocol) => PlcConfiguration.Validate(new() { Protocol = protocol });

    [Fact]
    public void InvalidOpcSettingsAndUnknownProtocolsAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => PlcConfiguration.Validate(new() { Protocol = "OpcUa" }));
        Assert.Throws<InvalidOperationException>(() => PlcConfiguration.Validate(new() { Protocol = "OpcDa", OpcProgId = " " }));
        Assert.Throws<InvalidOperationException>(() => PlcConfiguration.Validate(new() { Protocol = "OpcDa", OpcUpdateRateMs = 0 }));
        Assert.Throws<InvalidOperationException>(() => PlcConfiguration.Validate(new() { Protocol = "OpcDa", OpcTopic = "[NATIONEX]" }));
    }

    [Fact]
    public async Task OpcConfigurationRoundTripsAndPreservesDdeSettingsAndBothLinesTags()
    {
        var directory = Path.Combine(Path.GetTempPath(), "conveyor-opc-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var options = new ConveyorOptions
            {
                Lines = [
                    new() { Id = 0, CameraPort = 5001, ScalePort = 5002, DimensionPort = 5003,
                        Plc = new() { DdeTopic = "ORIGINAL", ChuteTag = "COLISDDE_M30", TransferTag = "TRANSFER_M30" } },
                    new() { Id = 1, CameraPort = 5101, ScalePort = 5102, DimensionPort = 5103,
                        Plc = new() { ChuteTag = "COLISDDE_M31", TransferTag = "TRANSFER_M31" } }
                ]
            };
            options.ApplyGlobalSorting();
            var editor = new ConfigurationEditor(Microsoft.Extensions.Options.Options.Create(options),
                new TestEnvironment { ContentRootPath = directory });
            var edited = editor.GetEditableCopy();
            var plc = edited.Lines[0].Plc;
            plc.Protocol = "OpcDa";
            plc.OpcProgId = "RSLinx OPC Server";
            plc.OpcHost = "CONV-QC";
            plc.OpcTopic = "NATIONEX";
            plc.OpcUpdateRateMs = 250;
            plc.CloseChute39Tag = "CLOSE_CHUTE_39_CUSTOM";
            await editor.SaveAsync(edited, null);
            var config = new ConfigurationBuilder().AddJsonFile(editor.FilePath).Build();
            var restored = config.GetSection("Conveyor").Get<ConveyorOptions>()!;
            var saved = restored.Lines[0].Plc;
            Assert.Equal("OpcDa", saved.Protocol);
            Assert.Equal("RSLinx OPC Server", saved.OpcProgId);
            Assert.Equal("CONV-QC", saved.OpcHost);
            Assert.Equal("NATIONEX", saved.OpcTopic);
            Assert.Equal(250, saved.OpcUpdateRateMs);
            Assert.Equal("CLOSE_CHUTE_39_CUSTOM", saved.CloseChute39Tag);
            Assert.Equal("ORIGINAL", saved.DdeTopic);
            Assert.Equal("COLISDDE_M30", saved.ChuteTag);
            Assert.Equal("TRANSFER_M31", restored.Lines[1].Plc.TransferTag);
            Assert.Equal("Dde", options.Lines[0].Plc.Protocol);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Test";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
