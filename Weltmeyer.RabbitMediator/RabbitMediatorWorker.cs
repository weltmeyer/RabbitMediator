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

    public async Task StartingAsync(CancellationToken cancellationToken)
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
    }

    private async Task WorkOnMultiplexed()
    {
        await foreach (var client in options.Value.PleaseConfigureMediators.Reader.ReadAllAsync())
        {
            _=Task.Run(client.Configure);
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