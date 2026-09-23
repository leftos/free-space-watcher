using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Service;
using FreeSpaceWatcher.Service.Alerts;
using FreeSpaceWatcher.Service.Config;
using FreeSpaceWatcher.Service.Etw;
using FreeSpaceWatcher.Service.Native;
using FreeSpaceWatcher.Service.Pipe;
using FreeSpaceWatcher.Service.Processes;
using FreeSpaceWatcher.Service.Sampling;
using FreeSpaceWatcher.Service.Status;
using FreeSpaceWatcher.Service.Writes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
var paths = ServicePaths.Resolve(builder.Configuration[ServicePaths.DataDirOption]);

builder.Services.AddWindowsService(options => options.ServiceName = "FreeSpaceWatcher");
builder.Logging.AddEventLog(settings =>
{
    settings.SourceName = "FreeSpaceWatcher";
    settings.LogName = "Application";
});
builder.Logging.AddFilter<EventLogLoggerProvider>(level => level >= LogLevel.Warning);

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<ConfigService>();
Action<ILogger, string, Exception?> logSkippedAlert = LoggerMessage.Define<string>(
    LogLevel.Warning,
    new EventId(1, "SkippedAlertFile"),
    "Skipped an alert file: {Message}"
);
builder.Services.AddSingleton(services =>
{
    ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("FreeSpaceWatcher.Service.History");
    return new HistoryStore(paths.AlertsDirectory, message => logSkippedAlert(logger, message, null));
});
builder.Services.AddSingleton<WriteAggregatorProvider>();
builder.Services.AddSingleton(_ => new DevicePathMapper(DosDevices.Query, TimeProvider.System));
builder.Services.AddSingleton<StatusHub>();
builder.Services.AddSingleton<AlertEngine>();
builder.Services.AddSingleton<ProcessActions>();
builder.Services.AddSingleton<PipeRequestHandler>();
builder.Services.AddSingleton(WriteCollectorOptions.ForService);
builder.Services.AddSingleton<WriteCollector>();
builder.Services.AddSingleton<IWriteSource>(services => services.GetRequiredService<WriteCollector>());

// The pipe server starts first: when another instance holds the pipe, the host fails before any other service starts.
builder.Services.AddHostedService<PipeServer>();
builder.Services.AddHostedService(services => services.GetRequiredService<WriteCollector>());
builder.Services.AddHostedService<DriveSampler>();
builder.Services.AddHostedService<HistoryPruner>();

using IHost host = builder.Build();
try
{
    await host.RunAsync();
    return 0;
}
catch (PipeNameInUseException ex)
{
    await Console.Error.WriteLineAsync("FreeSpaceWatcher cannot start: " + ex.Message);
    return 2;
}
