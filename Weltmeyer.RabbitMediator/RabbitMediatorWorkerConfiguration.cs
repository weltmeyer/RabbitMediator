using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace Weltmeyer.RabbitMediator;

/// <summary>
/// What <see cref="ExtensionMethods.AddRabbitMediator"/> hands over to <see cref="RabbitMediatorWorker"/>:
/// everything that has to be configured or resolved once the host starts.
/// </summary>
internal class RabbitMediatorWorkerConfiguration
{
    public readonly Channel<RabbitMediatorMultiplexer> PleaseConfigureMultiplexers =
        Channel.CreateUnbounded<RabbitMediatorMultiplexer>();

    public readonly Channel<RabbitMediator> PleaseConfigureMediators =
        Channel.CreateUnbounded<RabbitMediator>();

    /// <summary>
    /// Every mediator registered via <see cref="ExtensionMethods.AddRabbitMediator"/> (its DI service key and
    /// lifetime). The hosted worker eagerly resolves the singleton ones at startup so their consumer
    /// exchanges/queues are declared even if application code never resolves the mediator itself.
    /// </summary>
    public readonly ConcurrentBag<(object? serviceKey, ServiceLifetime lifetime)> RegisteredMediators = new();
}
