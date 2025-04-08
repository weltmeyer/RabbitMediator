using System.Collections.Concurrent;
using RabbitMQ.Client;
using Weltmeyer.RabbitMediator.ConsumerBases;

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
    
    public List<Type> RegisteredConsumerTypes { get; } = new();

    public readonly SemaphoreSlim EnsureReceiverSemaphore = new(1, 1);

    public readonly ConcurrentDictionary<string, IChannel> OwnedQueues = new();

    public readonly Dictionary<string, IChannel> ConsumerTags = new();
    public readonly ConcurrentDictionary<Type, IConsumer> ConsumerInstances = new();
    internal readonly ConcurrentDictionary<Type, Type> SentTypeToConsumerMapping = new();


}