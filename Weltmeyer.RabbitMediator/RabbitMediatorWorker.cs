using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Weltmeyer.RabbitMediator;

internal class RabbitMediatorWorker(
    IServiceProvider serviceProvider,
    IOptions<RabbitMediatorWorkerConfiguration> options)
    : IHostedLifecycleService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        var optionsInst = options.Value;
        /*foreach (var instanceKey in optionsInst.KeyListNormalMediators)
        {
            var iMediator = instanceKey == null
                ? serviceProvider.GetRequiredService<IRabbitMediator>()
                : serviceProvider.GetRequiredKeyedService<IRabbitMediator>(instanceKey);
            var mediator = iMediator as RabbitMediator;
            await mediator!.ConfigureBus(serviceProvider);
        }*/

       /* foreach (var instanceKey in optionsInst.KeyListMediatorMultiPlexer)
        {
            var iMediator = instanceKey == null
                ? serviceProvider.GetRequiredService<RabbitMediatorMultiplexer>()
                : serviceProvider.GetRequiredKeyedService<RabbitMediatorMultiplexer>(instanceKey);
            var mediator = iMediator as RabbitMediatorMultiplexer;
            await mediator!.Configure();
        }*/

        _ = Task.Run(WorkOnMultiplexed);
        _ = Task.Run(WorkOnMultiplexer);

        // Eagerly resolve every registered singleton mediator so its consumer exchanges/queues are declared at
        // host startup. Resolution runs EnsureConfigured + WaitReady in the DI factory. Without this a service
        // whose IRabbitMediator is never resolved (a consumer-only host, or one that only resolves it lazily
        // per request) never declares its receive topology, and other hosts get a 404 publishing a request to
        // its not-yet-declared exchange — the reason services used to Send(new Ping()) in StartAsync.
        foreach (var (serviceKey, lifetime) in optionsInst.RegisteredMediators)
        {
            // Scoped mediators can't be resolved from the root provider; they configure per scope on first use.
            if (lifetime != ServiceLifetime.Singleton)
                continue;
            try
            {
                _ = serviceKey == null
                    ? serviceProvider.GetRequiredService<IRabbitMediator>()
                    : serviceProvider.GetRequiredKeyedService<IRabbitMediator>(serviceKey);
            }
            catch
            {
                // A genuine connect/config failure (e.g. broker unreachable) must not abort host startup or the
                // remaining mediators; it will resurface when the mediator is first used.
            }
        }

        return Task.CompletedTask;
    }

    private async Task WorkOnMultiplexed()
    {
        await foreach (var client in options.Value.PleaseConfigureMediators.Reader.ReadAllAsync())
        {
            _=Task.Run(client.EnsureConfigured);
        }
    }
    
    private async Task WorkOnMultiplexer()
    {
        await foreach (var mp in options.Value.PleaseConfigureMultiplexers.Reader.ReadAllAsync())
        {
            using var cts = new CancellationTokenSource();
            var timeOutTask = Task.Delay(5000);
            var configTask=Task.Run(()=>mp.Configure(cts.Token));
            if(await Task.WhenAny(timeOutTask,configTask)!=configTask)
                cts.Cancel();
        }
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        options.Value.PleaseConfigureMultiplexers.Writer.Complete();
        options.Value.PleaseConfigureMediators.Writer.Complete();
        return Task.CompletedTask;
    }

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}