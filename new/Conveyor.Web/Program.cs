using Conveyor.Web.Components;
using Conveyor.Web.Infrastructure;
using Conveyor.Web.Options;
using Conveyor.Web.Services;
using Microsoft.Extensions.Options;

if (int.TryParse(Environment.GetEnvironmentVariable("CONVEYOR_RESTART_WAIT_PID"), out var previousProcessId))
{
    try
    {
        using var previousProcess = System.Diagnostics.Process.GetProcessById(previousProcessId);
        await previousProcess.WaitForExitAsync();
    }
    catch (ArgumentException) { }
    Environment.SetEnvironmentVariable("CONVEYOR_RESTART_WAIT_PID", null);
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(new ServiceUptime());
builder.Configuration.AddJsonFile("conveyor.settings.json", optional: true, reloadOnChange: false);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ");
var logStore = new InMemoryLogStore();
builder.Logging.AddProvider(logStore);
builder.Services.AddSingleton<ILogStore>(logStore);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddOptions<ConveyorOptions>()
    .Bind(builder.Configuration.GetSection(ConveyorOptions.SectionName))
    .PostConfigure(options => options.ApplyGlobalSorting())
    .ValidateDataAnnotations()
    .Validate(options => options.Lines.Select(line => line.Id).Distinct().Count() == options.Lines.Count, "Les identifiants de ligne doivent être uniques.")
    .Validate(options => options.LineCount <= options.Lines.Count, "Paramètres manquants pour le nombre de lignes choisi.")
    .Validate(options =>
    {
        var ports = options.GetConfiguredLines().Where(line => line.Enabled).SelectMany(line => new[]
        {
            (line.CameraPort, line.CameraConnectMode), (line.ScalePort, line.ScaleConnectMode),
            (line.DimensionPort, line.DimensionConnectMode)
        }).Where(device => !device.Item2).Select(device => device.Item1).ToArray();
        return ports.Distinct().Count() == ports.Length;
    }, "Chaque source TCP en mode serveur doit utiliser un port distinct.")
    .ValidateOnStart();

builder.Services.AddSingleton<IConveyorRepository>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IOptions<ConveyorOptions>>().Value.Database.ConnectionString)
        ? new SimulationConveyorRepository()
        : ActivatorUtilities.CreateInstance<MySqlConveyorRepository>(services));
builder.Services.AddSingleton<SortEngine>();
builder.Services.AddSingleton<IConfigurationEditor, ConfigurationEditor>();
builder.Services.AddSingleton<ConverterResetService>();
builder.Services.AddSingleton<IApplicationRestartService, ApplicationRestartService>();
builder.Services.AddSingleton<DatabaseMetricsService>();
builder.Services.AddSingleton<IDatabaseMetricsService>(services => services.GetRequiredService<DatabaseMetricsService>());
builder.Services.AddHostedService(services => services.GetRequiredService<DatabaseMetricsService>());
builder.Services.AddSingleton<ConveyorSupervisor>();
builder.Services.AddSingleton<IConveyorSupervisor>(services => services.GetRequiredService<ConveyorSupervisor>());
builder.Services.AddHostedService(services => services.GetRequiredService<ConveyorSupervisor>());

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/api/lines", (IConveyorSupervisor supervisor) => supervisor.GetSnapshots());
app.MapGet("/api/logs", (ILogStore logs) => logs.GetRecent());
app.MapPost("/api/lines/{lineId:int}/start", async (int lineId, IConveyorSupervisor supervisor) => { await supervisor.StartLineAsync(lineId); return Results.NoContent(); });
app.MapPost("/api/lines/{lineId:int}/restart", async (int lineId, IConveyorSupervisor supervisor) => { await supervisor.RestartLineAsync(lineId); return Results.NoContent(); });
app.MapPost("/api/lines/{lineId:int}/stop", async (int lineId, IConveyorSupervisor supervisor) => { await supervisor.StopLineAsync(lineId); return Results.NoContent(); });
app.MapPost("/api/lines/{lineId:int}/reset", (int lineId, IConveyorSupervisor supervisor) => { supervisor.ResetCounters(lineId); return Results.NoContent(); });

app.Run();
