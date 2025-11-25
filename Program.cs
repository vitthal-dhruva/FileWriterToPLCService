using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System;
using System.IO;

class Program
{
    public static void Main(string[] args)
    {
        // Use the folder where your service exe is installed (on D:)
        string basePath = AppContext.BaseDirectory; // e.g., D:\MyService\
        string logFolder = Path.Combine(basePath, "logs");

        // Ensure log folder exists
        if (!Directory.Exists(logFolder))
            Directory.CreateDirectory(logFolder);

        string logFile = Path.Combine(logFolder, "app_log.txt");

        // Setup Serilog with absolute path
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(logFile, rollingInterval: RollingInterval.Year)
            .WriteTo.Console()
            .CreateLogger();
        //Log.Information("Service starting...");
        //Log.Information("Log file path: {LogFilePath}", logFile); // Shows exact location

        try
        {
            Host.CreateDefaultBuilder(args)
                .UseWindowsService()
                .UseSerilog()
                .ConfigureServices(services =>
                {
                    services.AddHostedService<WorkerService>();
                })
                .Build()
                .Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Service terminated unexpectedly");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
