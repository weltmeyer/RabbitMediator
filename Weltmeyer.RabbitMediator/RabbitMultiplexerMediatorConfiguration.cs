using System.Collections.Concurrent;
using RabbitMQ.Client;
using Weltmeyer.RabbitMediator.Contracts.ConsumerBases;

namespace Weltmeyer.RabbitMediator;

internal class RabbitMultiplexerMediatorConfiguration
{
    public RabbitMultiplexerMediatorConfiguration(RabbitMediator mediator, RabbitMediatorConfiguration configuration,
        IServiceProvider serviceProvider)
    {
        Configuration = configuration;
        ServiceProvider = serviceProvider;
        this.RabbitMediator = mediator;
    }


    public RabbitMediator RabbitMediator { get; }

    public readonly RabbitMediatorConfiguration Configuration;
    public readonly IServiceProvider ServiceProvider;

    /// <summary>
    /// Sent-object types this mediator already has a receiver for. Concurrent because the fast path of
    /// EnsureReceiver reads it before taking <see cref="EnsureReceiverSemaphore"/>, while another type may be
    /// getting added inside the semaphore.
    /// </summary>
    public readonly ConcurrentDictionary<Type, bool> RegisteredConsumerTypes = new();

    public readonly SemaphoreSlim EnsureReceiverSemaphore = new(1, 1);

    public readonly ConcurrentDictionary<string, IChannel> OwnedQueues = new();

    public readonly ConcurrentDictionary<string, (string exchangeName, string exchangeType)> QueueToExchangeBindings =
        new();

    /// <summary>Consumer tag to the channel it was registered on, needed to cancel them on dispose.</summary>
    public readonly ConcurrentDictionary<string, IChannel> ConsumerTags = new();

    public readonly ConcurrentDictionary<Type, IConsumer> ConsumerInstances = new();
    internal readonly ConcurrentDictionary<Type, Type> SentTypeToConsumerMapping = new();
}
