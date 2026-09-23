using System.IO.Pipes;
using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Ipc;
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

namespace FreeSpaceWatcher.Service;

/// <summary>The service's entry point: claims the pipe, then builds the host and runs it.</summary>
public static class ServiceHost
{
    /// <summary>The exit code when another process already serves the pipe.</summary>
    public const int PipeInUseExitCode = 2;

    /// <summary>
    /// Claims the service pipe before anything touches the data folder, so a second instance fails without writing there, then
    /// builds the host and runs it until it stops.
    /// </summary>
    /// <param name="args">The command line; <c>--data-dir &lt;path&gt;</c> overrides the data folder.</param>
    /// <param name="pipeName">The pipe name: <see cref="PipeProtocol.PipeName"/> for the service, a unique one in tests.</param>
    /// <returns>0 after a normal stop; <see cref="PipeInUseExitCode"/>, with the reason on standard error, when the pipe is taken.</returns>
    public static async Task<int> RunAsync(string[] args, string pipeName)
    {
        NamedPipeServerStream firstInstance;
        try
        {
            firstInstance = PipeServer.ClaimFirstInstance(pipeName);
        }
        catch (PipeNameInUseException ex)
        {
            await Console.Error.WriteLineAsync("FreeSpaceWatcher cannot start: " + ex.Message).ConfigureAwait(false);
            return PipeInUseExitCode;
        }

        using IHost host = Build(args, pipeName, firstInstance);
        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static IHost Build(string[] args, string pipeName, NamedPipeServerStream firstInstance)
    {
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
            new EventId(1304, "SkippedAlertFile"),
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
        builder.Services.AddSingleton<RecentSamples>();
        builder.Services.AddSingleton<AlertEngine>();
        builder.Services.AddSingleton<ProcessActions>();
        builder.Services.AddSingleton<PipeRequestHandler>();
        builder.Services.AddSingleton(WriteCollectorOptions.ForService);
        builder.Services.AddSingleton<WriteCollector>();
        builder.Services.AddSingleton<IWriteSource>(services => services.GetRequiredService<WriteCollector>());

        builder.Services.AddHostedService(services => new PipeServer(
            services.GetRequiredService<PipeRequestHandler>(),
            services.GetRequiredService<StatusHub>(),
            services.GetRequiredService<ILogger<PipeServer>>(),
            pipeName,
            firstInstance
        ));
        builder.Services.AddHostedService(services => services.GetRequiredService<WriteCollector>());
        builder.Services.AddHostedService<DriveSampler>();
        builder.Services.AddHostedService<HistoryPruner>();

        return builder.Build();
    }
}
