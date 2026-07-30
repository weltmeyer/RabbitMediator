using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Weltmeyer.RabbitMediator.Contracts.ConsumerBases;

namespace Weltmeyer.RabbitMediator;

/// <summary>
/// One AMQP connection shared by all mediators registered against the same connection string. Holds the
/// channels, the topology of every mediator on it, and the awaiters for outstanding requests and acks.
/// </summary>
/// <remarks>
/// Split across files by concern: this one covers state and mediator lifecycle,
/// <c>RabbitMediatorMultiplexer.Connection.cs</c> the connection/channel setup and recovery,
/// <c>.Publish.cs</c> the outgoing side and <c>.Receive.cs</c> the incoming side.
/// </remarks>
internal partial class RabbitMediatorMultiplexer : IAsyncDisposable, IDisposable
{
    public string InstanceId { get; } = RabbitMediator.GenerateId();

    private readonly ILogger<RabbitMediatorMultiplexer>? _logger;
    private readonly JsonSerializerHelper _serializerHelper = new();
    private readonly ushort _consumerDispatchConcurrency;
    private readonly ConnectionFactory? _connectionFactory;
    private IConnection? _connection;

    private IChannel? _sendMessageChannel;
    private IChannel? _sendRequestChannel;
    private IChannel? _sendResponseChannel;

    private IChannel? _receiveMessageChannel;
    private IChannel? _receiveRequestChannel;
    private IChannel? _receiveResponseChannel;

    private IChannel? _receiveAckChannel;
    private IChannel? _sendAckChannel;

    private readonly ConcurrentDictionary<Guid, TargetAckAwaiter> _targetAckWaiters = new();
    private readonly ConcurrentDictionary<Guid, RequestResponseAwaiter> _responseWaiters = new();

    private readonly List<RabbitMultiplexerMediatorConfiguration> _rabbitMultiplexerMediatorConfigurations = new();

    private readonly ConcurrentDictionary<string, RabbitMediator> _rabbitMediatorInstances = new();


    public RabbitMediatorMultiplexer(string connectionString, ushort consumerDispatchConcurrency = 10,
        ILogger<RabbitMediatorMultiplexer>? logger = null, IConnection? customConnection = null)
    {
        _logger = logger;
        _consumerDispatchConcurrency = consumerDispatchConcurrency;
        _connection = customConnection;
        if (_connection == null)
        {
            _connectionFactory = new ConnectionFactory
            {
                Uri = new Uri(connectionString),
                AutomaticRecoveryEnabled = true,
            };
        }
    }

    private RabbitMultiplexerMediatorConfiguration GetConfiguration(RabbitMediator rabbitMediator) =>
        _rabbitMultiplexerMediatorConfigurations.First(cfg => cfg.RabbitMediator == rabbitMediator);

    public RabbitMediator CreateRabbitMediator(IServiceProvider serviceProvider,
        RabbitMediatorConfiguration configuration)
    {
        configuration.Validate();
        var newMediator = new RabbitMediator(this);
        _rabbitMultiplexerMediatorConfigurations.Add(
            new RabbitMultiplexerMediatorConfiguration(newMediator, configuration, serviceProvider));
        return newMediator;
    }

    internal async Task ConfigureRabbitMediator(RabbitMediator rabbitMediator)
    {
        await Configure();
        _rabbitMediatorInstances.TryAdd(rabbitMediator.ScopeId, rabbitMediator);
        await ConfigureAllReceivers(rabbitMediator);
    }

    /// <summary>
    /// Declares the receive topology for every consumer of <paramref name="rabbitMediator"/> and remembers
    /// which consumer handles which sent-object type.
    /// </summary>
    private async Task ConfigureAllReceivers(RabbitMediator rabbitMediator)
    {
        var iMessageConsumerType = typeof(IMessageConsumer<>);
        var iRequestConsumerType = typeof(IRequestConsumer<,>);

        var configuration = GetConfiguration(rabbitMediator);

        foreach (var consumerType in configuration.Configuration.GetAllConsumerTypes())
        {
            var interfaces = consumerType.GetInterfaces();
            var messageConsumerInterfaces = interfaces.Where(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == iMessageConsumerType).ToArray();
            var requestConsumerInterfaces = interfaces.Where(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == iRequestConsumerType).ToArray();

            foreach (var messageConsumerInterface in messageConsumerInterfaces)
            {
                var messageType = messageConsumerInterface.GetGenericArguments()[0];
                configuration.SentTypeToConsumerMapping.TryAdd(messageType, consumerType);
                await EnsureReceiver(rabbitMediator, messageType);
            }

            foreach (var requestConsumerInterface in requestConsumerInterfaces)
            {
                var requestType = requestConsumerInterface.GetGenericArguments()[0];
                configuration.SentTypeToConsumerMapping.TryAdd(requestType, consumerType);

                await EnsureReceiver(rabbitMediator, requestType);
            }
        }
    }

    internal IConsumer? GetConsumer(RabbitMediator rabbitMediator, Type consumerType)
    {
        if (!consumerType.IsAssignableTo(typeof(IConsumer)))
            throw new ArgumentException($"The type {consumerType.FullName} does not implement {nameof(IConsumer)}");

        var configuration = GetConfiguration(rabbitMediator);

        if (!configuration.Configuration.GetAllConsumerTypes().Contains(consumerType))
            return null;
        return configuration.ConsumerInstances.GetOrAdd(consumerType, static (_, serviceProviderAndType) =>
            (IConsumer)ActivatorUtilities.CreateInstance(serviceProviderAndType.ServiceProvider,
                serviceProviderAndType.consumerType), (configuration.ServiceProvider, consumerType));
    }

    internal async Task DisposeRabbitMediatorConnection(RabbitMediator mediator)
    {
        var configuration = GetConfiguration(mediator);
        _rabbitMediatorInstances.TryRemove(mediator.ScopeId, out _);
        foreach (var consumerKv in configuration.ConsumerTags)
        {
            await consumerKv.Value.BasicCancelAsync(consumerKv.Key, true);
        }

        foreach (var queue in configuration.OwnedQueues)
        {
            await queue.Value.QueueDeleteAsync(queue.Key, false, false, true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null) await _connection.DisposeAsync();
        if (_sendMessageChannel != null) await _sendMessageChannel.DisposeAsync();
        if (_sendRequestChannel != null) await _sendRequestChannel.DisposeAsync();
        if (_sendResponseChannel != null) await _sendResponseChannel.DisposeAsync();
        if (_receiveMessageChannel != null) await _receiveMessageChannel.DisposeAsync();
        if (_receiveRequestChannel != null) await _receiveRequestChannel.DisposeAsync();
        if (_receiveResponseChannel != null) await _receiveResponseChannel.DisposeAsync();
        if (_receiveAckChannel != null) await _receiveAckChannel.DisposeAsync();
        if (_sendAckChannel != null) await _sendAckChannel.DisposeAsync();
    }

    public void Dispose()
    {
        Task.Run(this.DisposeAsync).GetAwaiter().GetResult(); //baaaaaaah
    }
}
