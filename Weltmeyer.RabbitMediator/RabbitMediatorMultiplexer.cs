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

    /// <summary>
    /// The mediators living on this connection and their topology. Concurrent and keyed instead of a scanned
    /// list: scoped mediators are created from DI factories on any thread while published and received
    /// messages look their configuration up on every single call.
    /// </summary>
    private readonly ConcurrentDictionary<RabbitMediator, RabbitMultiplexerMediatorConfiguration>
        _rabbitMultiplexerMediatorConfigurations = new();

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
        TryGetConfiguration(rabbitMediator) ??
        throw new InvalidOperationException(
            $"The mediator {rabbitMediator.ScopeId} is not (or no longer) registered on this multiplexer.");

    /// <summary>The mediator's topology, or null once it has been disposed.</summary>
    internal RabbitMultiplexerMediatorConfiguration? TryGetConfiguration(RabbitMediator rabbitMediator) =>
        _rabbitMultiplexerMediatorConfigurations.GetValueOrDefault(rabbitMediator);

    public RabbitMediator CreateRabbitMediator(IServiceProvider serviceProvider,
        RabbitMediatorConfiguration configuration)
    {
        configuration.Validate();
        var newMediator = new RabbitMediator(this);
        _rabbitMultiplexerMediatorConfigurations.TryAdd(newMediator,
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

    /// <summary>
    /// Tears down one mediator's topology and forgets it. The multiplexer itself keeps running for the other
    /// mediators sharing this connection.
    /// </summary>
    internal async Task DisposeRabbitMediatorConnection(RabbitMediator mediator)
    {
        // Forget the mediator first: nothing published or received afterwards should still find its topology.
        if (!_rabbitMultiplexerMediatorConfigurations.TryRemove(mediator, out var configuration))
            return; //already disposed
        _rabbitMediatorInstances.TryRemove(mediator.ScopeId, out _);

        AbortWaiters(waiter => waiter == mediator);

        // Tolerate a broker that is already gone: now that the synchronous Dispose really awaits this, an
        // exception in here would escape a using block on a connection that died before shutdown.
        try
        {
            foreach (var consumerKv in configuration.ConsumerTags)
            {
                await consumerKv.Value.BasicCancelAsync(consumerKv.Key, true);
            }

            foreach (var queue in configuration.OwnedQueues)
            {
                await queue.Value.QueueDeleteAsync(queue.Key, false, false, true);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not fully tear down mediator {ScopeId}", mediator.ScopeId);
        }
    }

    /// <summary>
    /// Fails every pending request and ack awaiter owned by a mediator the predicate matches. Without this a
    /// caller awaiting a response would keep waiting for the full timeout after its mediator is already gone.
    /// </summary>
    private void AbortWaiters(Func<RabbitMediator, bool> ownerPredicate)
    {
        foreach (var (correlationId, waiter) in _responseWaiters)
        {
            if (!ownerPredicate(waiter.Owner) || !_responseWaiters.TryRemove(correlationId, out _))
                continue;
            waiter.TaskCompletionSource.TrySetException(new ObjectDisposedException(nameof(IRabbitMediator)));
        }

        foreach (var (correlationId, waiter) in _targetAckWaiters)
        {
            if (!ownerPredicate(waiter.Owner) || !_targetAckWaiters.TryRemove(correlationId, out _))
                continue;
            waiter.TaskCompletionSource.TrySetException(new ObjectDisposedException(nameof(IRabbitMediator)));
        }
    }

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        AbortWaiters(_ => true);

        // Channels before the connection: disposing the connection first closes them underneath, so their own
        // dispose then runs against a dead connection.
        foreach (var channel in new[]
                 {
                     _sendMessageChannel, _sendRequestChannel, _sendResponseChannel, _receiveMessageChannel,
                     _receiveRequestChannel, _receiveResponseChannel, _receiveAckChannel, _sendAckChannel
                 })
        {
            if (channel != null)
                await channel.DisposeAsync();
        }

        if (_connection != null)
            await _connection.DisposeAsync();

        _configureLock.Dispose();
    }

    public void Dispose()
    {
        // Task.Run to get off any SynchronizationContext, and AsTask so the ValueTask is actually awaited -
        // Task.Run(this.DisposeAsync) binds to Task.Run(Func<TResult>) and hands back a Task<ValueTask> whose
        // inner ValueTask nobody ever awaited, which made this method return before disposing anything.
        Task.Run(() => DisposeAsync().AsTask()).GetAwaiter().GetResult();
    }
}
