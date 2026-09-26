using Conveyor.Web.Components.Layout;
using Conveyor.Web.Components.Pages;
using Conveyor.Web.Services;
using Conveyor.Web.Domain;
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
        public List<int> RequestedShifts { get; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_CurrentShiftId" => 1,
            "get_IsSimulation" => false,
            "GetShiftsAsync" => Task.FromResult<IReadOnlyList<ConveyorShift>>([new(1, "Jour"), new(2, "Soir")]),
            "GetChuteDestinationsAsync" => GetDestinations((int)args![0]!),
            _ => throw new NotSupportedException(method?.Name)
        };

        private Task<IReadOnlyDictionary<int, string>> GetDestinations(int shift)
        {
            RequestedShifts.Add(shift);
            return Task.FromResult<IReadOnlyDictionary<int, string>>(new Dictionary<int, string> { [23] = shift == 1 ? "BLV" : "QC" });
        }
    }

    [Fact]
    public async Task DisplayShiftChangesDestinationWithoutChangingSortingShift()
    {
        var repository = DispatchProxy.Create<IConveyorRepository, MapDependencyProxy>();
        var supervisor = DispatchProxy.Create<IConveyorSupervisor, MapDependencyProxy>();
        var activator = new MapActivator();
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton(repository).AddSingleton(supervisor)
            .AddSingleton<IComponentActivator>(activator).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<ConveyorMap>();
            Assert.Contains("Soir", output.ToHtmlString());
            var map = Assert.IsType<ConveyorMap>(activator.Map);
            await ((IHandleEvent)map).HandleEventAsync(new EventCallbackWorkItem((Func<Task>)(() => map.ChangeDisplayShift(2))), null);
            var html = System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
            Assert.Contains("Chute 23 — QC", html);
            Assert.DoesNotContain("Chute 23 — BLV", html);
            Assert.Equal(1, supervisor.CurrentShiftId);
            Assert.Equal(new[] { 1, 2 }, ((MapDependencyProxy)(object)repository).RequestedShifts);
        });
    }

    private sealed class MapActivator : IComponentActivator
    {
        public ConveyorMap? Map { get; private set; }
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is ConveyorMap map) Map = map;
            return component;
        }
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
