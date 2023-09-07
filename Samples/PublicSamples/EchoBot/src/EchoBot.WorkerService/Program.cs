using EchoBot.WorkerService;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(loggerFactory => loggerFactory.AddEventLog())
    .UseWindowsService()
    .ConfigureServices(services =>
    {
        services.AddHostedService<EchoBotWorker>();
    })
    .Build();

await host.RunAsync();
