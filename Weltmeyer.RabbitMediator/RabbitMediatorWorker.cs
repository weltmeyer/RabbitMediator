using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Weltmeyer.RabbitMediator;

internal class RabbitMediatorWorker(IServiceProvider serviceProvider,IOptions<RabbitMediatorWorkerConfiguration> options)
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
        foreach (var instanceKey in optionsInst.KeyList)
        {
            var iMediator = instanceKey == null ? serviceProvider.GetRequiredService<IRabbitMediator>() : serviceProvider.GetRequiredKeyedService<IRabbitMediator>(instanceKey);
            var mediator = iMediator as RabbitMediator;
            await mediator!.ConfigureBus(serviceProvider);
        }
        
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}