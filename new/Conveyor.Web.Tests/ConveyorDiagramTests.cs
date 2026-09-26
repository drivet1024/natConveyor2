using Conveyor.Web.Components.Layout;
using Conveyor.Web.Components.Pages;
using Conveyor.Web.Services;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using System.Xml.Linq;

namespace Conveyor.Web.Tests;

public sealed class ConveyorDiagramTests
{
    [Fact]
    public async Task MapPagePassesLoadedDepotCodeInsteadOfLiteralStatusVariable()
    {
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton(DispatchProxy.Create<IConveyorRepository, MapDependencyProxy>())
            .AddSingleton(DispatchProxy.Create<IConveyorSupervisor, MapDependencyProxy>())
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<ConveyorMap>()).ToHtmlString());
        Assert.DoesNotContain("_destinationStatus", html);
        Assert.Contains("Chute 23 — BLV", System.Net.WebUtility.HtmlDecode(html));
    }

    public class MapDependencyProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_CurrentShiftId" => 1,
            "get_IsSimulation" => false,
            "GetChuteDestinationsAsync" => Task.FromResult<IReadOnlyDictionary<int, string>>(new Dictionary<int, string> { [23] = "BLV" }),
            _ => throw new NotSupportedException(method?.Name)
        };
    }

    [Fact]
    public async Task NumberTooltipsIncludeDestinationsOnRepeatedLabelsAndEscapeDepotNames()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<ConveyorDiagram>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(ConveyorDiagram.Destinations)] = new Dictionary<int, string> { [38] = "QC / STH & <Haut>", [16] = "TOR" }
            }));
            return output.ToHtmlString();
        });
        var svg = XDocument.Parse(html);
        XNamespace ns = "http://www.w3.org/2000/svg";
        var titles = svg.Descendants(ns + "title").Select(element => element.Value).ToArray();
        Assert.Equal(2, titles.Count(title => title == "Chute 38 — QC / STH & <Haut>"));
        Assert.Contains("Chute 16 — TOR", titles);
        Assert.Contains("Chute 1 — Aucun dépôt de destination associé pour ce shift", titles);
        Assert.DoesNotContain("<Haut>", html);
    }
}
