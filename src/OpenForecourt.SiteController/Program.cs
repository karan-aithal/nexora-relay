using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using OpenForecourt.Abstractions.Ports;
using OpenForecourt.Adapters.InProc;
using OpenForecourt.Adapters.RabbitMq;
using OpenForecourt.Adapters.Sqlite;
using OpenForecourt.SiteController.Api;
using OpenForecourt.SiteController.Config;
using OpenForecourt.SiteController.Hosting;
using OpenForecourt.SiteController.Logging;
using OpenForecourt.SiteController.Orchestration;
using OpenForecourt.SiteController.Realtime;
using OpenForecourt.SiteController.Services;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Context;

// Masking lives at the sink, so nothing logged anywhere can leak a PAN (CLAUDE.md 7.3).
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .MinimumLevel.Information()
    .WriteTo.Sink(new PanMaskingSink(Console.Out))
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    var options = new SiteOptions();
    builder.Configuration.GetSection(SiteOptions.Section).Bind(options);
    builder.Services.AddSingleton(options);

    // --- Ports: real adapter or in-process stand-in, selected by configuration (CLAUDE.md 3). ---
    ITransactionJournal journal = options.JournalPath == ":memory:"
        ? new InMemoryJournal()
        : await SqliteJournal.OpenAsync(options.JournalPath, CancellationToken.None);
    builder.Services.AddSingleton(journal);

    if (options.HostEndpoint is { Length: > 0 } hostEndpoint)
    {
        var (host, port) = ParseEndpoint(hostEndpoint);
        builder.Services.AddSingleton(sp => new TcpHostConnection(
            host, port, sp.GetRequiredService<IClock>(), options, sp.GetRequiredService<ILogger<TcpHostConnection>>()));
        builder.Services.AddSingleton<IHostConnection>(sp => sp.GetRequiredService<TcpHostConnection>());
        builder.Services.AddSingleton<IHostProbe>(sp => sp.GetRequiredService<TcpHostConnection>());
    }
    else
    {
        var inProcHost = new InProcHostConnection();
        builder.Services.AddSingleton<IHostConnection>(inProcHost);
        builder.Services.AddSingleton<IHostProbe>(inProcHost);
    }

    ITransactionDispatch dispatch = options.RabbitMqUri is { Length: > 0 } uri
        ? await RabbitMqDispatch.ConnectAsync(new Uri(uri), CancellationToken.None)
        : new InProcDispatch();
    builder.Services.AddSingleton(dispatch);

    builder.Services.AddSingleton<IClock>(SystemClock.Instance);
    builder.Services.AddSingleton<HostAvailability>();
    builder.Services.AddSingleton<OfflinePolicy>();
    builder.Services.AddSingleton<PumpRegistry>();
    builder.Services.AddSingleton<StanSequence>();
    builder.Services.AddSingleton<TransactionService>();
    builder.Services.AddSingleton<SnapshotProvider>();
    builder.Services.AddSingleton<IdempotencyStore>();
    builder.Services.AddSingleton<RecoveryService>();

    builder.Services.AddSingleton(sp =>
    {
        var ctx = sp.GetRequiredService<IHubContext<ForecourtHub>>();
        return new PerConnectionDispatcher(
            (id, frame, ct) => ctx.Clients.Client(id).SendAsync(frame.Method, frame.Payload, ct),
            sp.GetRequiredService<ILogger<PerConnectionDispatcher>>());
    });

    builder.Services.AddSingleton<SettlementConsumer>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SettlementConsumer>());
    builder.Services.AddHostedService<HostProber>();
    builder.Services.AddHostedService<OfflineReplayService>();
    builder.Services.AddHostedService<HubBroadcaster>();

    builder.Services.AddSignalR()
        .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
    builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
    builder.Services.AddProblemDetails();
    builder.Services.AddOpenApi();
    builder.Services.AddHealthChecks()
        .AddCheck<JournalHealthCheck>("journal")
        .AddCheck<HostLinkHealthCheck>("host-link")
        .AddCheck<DispatchHealthCheck>("dispatch");

    builder.Services.AddOpenTelemetry().WithTracing(t => t
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("OpenForecourt.SiteController"))
        .AddAspNetCoreInstrumentation()
        .AddConsoleExporter());

    var app = builder.Build();

    app.UseExceptionHandler();
    app.UseStatusCodePages();
    app.UseMiddleware<CorrelationIdMiddleware>();

    app.MapOpenApi();
    app.MapHealthChecks("/health");
    app.MapHealthChecks("/api/v1/health");
    app.MapHub<ForecourtHub>("/hubs/forecourt");
    app.MapForecourtApi();

    // Reconcile any crash-interrupted transactions BEFORE the site accepts new work.
    using (LogContext.PushProperty("CorrelationId", "startup-recovery"))
    {
        await app.Services.GetRequiredService<RecoveryService>().StartAsync(CancellationToken.None);
    }

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Site controller terminated unexpectedly.");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static (string Host, int Port) ParseEndpoint(string hostPort)
{
    string[] parts = hostPort.Split(':', 2);
    int port = parts.Length == 2 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 9583;
    return (parts[0], port);
}

/// <summary>Exposed so the integration tests can spin up the site controller in-process.</summary>
public partial class Program;
