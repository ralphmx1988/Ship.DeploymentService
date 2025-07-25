using Ship.DeploymentService;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();

builder.Services.AddSerilog();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<Worker>();
builder.Services.AddWindowsService();

var host = builder.Build();
host.Run();