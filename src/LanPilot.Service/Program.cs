using LanPilot.Service;
using LanPilot.Service.Engine;
using LanPilot.Service.Ipc;
using LanPilot.Service.Persistence;
using LanPilot.Service.Diagnostics;
using Microsoft.Extensions.Hosting.WindowsServices;

if (args.Contains("--prepare-update", StringComparer.Ordinal))
{
    Environment.ExitCode = await InstallerMaintenance.PrepareAsync();
    return;
}

ServiceInstanceLease lease;
try
{
    lease = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LanPilot"));
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("LanPilot service ownership could not be acquired. Another instance may be running.");
    Environment.ExitCode = 1;
    return;
}
using var serviceOwnership = lease;
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "LanPilot Service");
builder.Logging.AddEventLog(settings => settings.SourceName = "LanPilot Service");
builder.Services.AddSingleton<DiagnosticRecorder>();
builder.Services.AddSingleton<ILoggerProvider, DiagnosticLoggerProvider>();
builder.Logging.AddFilter<DiagnosticLoggerProvider>("LanPilot", LogLevel.Debug);

builder.Services.AddSingleton<SqliteStore>();
builder.Services.AddSingleton<ControlSessionJournal>();
builder.Services.AddSingleton<ControlSafetyJournal>();
builder.Services.AddSingleton<NetworkScanner>();
builder.Services.AddSingleton<TrafficEngine>();
builder.Services.AddSingleton<PolicyResolver>();
builder.Services.AddSingleton<ApplicationFlowRegistry>();
builder.Services.AddSingleton<ApplicationDownloadLimiter>();
builder.Services.AddSingleton<ApplicationTrafficMonitor>();
builder.Services.AddSingleton<ApplicationTrafficController>();
builder.Services.AddSingleton<IApplicationPolicyController>(provider => provider.GetRequiredService<ApplicationTrafficController>());
builder.Services.AddSingleton<LanPilotCoordinator>();
builder.Services.AddHostedService<DiagnosticWorker>();
builder.Services.AddHostedService<ControlHealthWorker>();
builder.Services.AddHostedService<LanPilotWorker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ApplicationTrafficMonitor>());
builder.Services.AddHostedService<PipeServer>();

if (!WindowsServiceHelpers.IsWindowsService())
{
    builder.Logging.AddConsole();
}

await builder.Build().RunAsync();
