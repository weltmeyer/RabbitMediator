using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Weltmeyer.RabbitMediator;

/// <summary>Connection and channel setup plus everything that has to happen after a recovery.</summary>
internal partial class RabbitMediatorMultiplexer
{
    private bool _configureDone;
    private readonly SemaphoreSlim _configureLock = new(1, 1);

    /// <summary>
    /// Opens the connection (unless one was handed in), creates the channels and starts consuming the ack
    /// queue. Idempotent - the first caller does the work, later ones return immediately.
    /// </summary>
    public async Task Configure(CancellationToken? cancellationToken = null)
    {
        if (_configureDone)
            return;
        await _configureLock.WaitAsync();
        try
        {
            if (_configureDone)
                return;

            cancellationToken ??= CancellationToken.None;
            var myName = $"{AppDomain.CurrentDomain.FriendlyName}{RabbitNaming.KeySeparator}{this.InstanceId}";
            _connection ??=
                await _connectionFactory!.CreateConnectionAsync(clientProvidedName: myName,
                    cancellationToken: cancellationToken.Value);

            RegisterConnectionEventHandlers(_connection);
            await CreateChannels(_connection);
            await StartAckConsumer();

            _configureDone = true;
        }
        finally
        {
            _configureLock.Release();
        }
    }

    private void RegisterConnectionEventHandlers(IConnection connection)
    {
        connection.ConnectionShutdownAsync += (_, args) =>
        {
            _logger?.LogWarning("Connection shutdown {args}", args);
            return Task.CompletedTask;
        };

        connection.RecoveringConsumerAsync += (_, args) =>
        {
            _logger?.LogInformation("Recover consumer... {ConsumerTag}", args.ConsumerTag);
            return Task.CompletedTask;
        };

        connection.ConsumerTagChangeAfterRecoveryAsync += (_, args) =>
        {
            _logger?.LogWarning("Recovery: ChannelTagChanged: TagBefore:{TagBefore} TagAfter:{TagAfter}",
                args.TagBefore, args.TagAfter);
            return Task.CompletedTask;
        };

        connection.QueueNameChangedAfterRecoveryAsync += (_, args) =>
        {
            _logger?.LogWarning("Recovery: QueueNameChanged: NameBefore:{NameBefore} NameAfter:{NameAfter}",
                args.NameBefore, args.NameAfter);
            return Task.CompletedTask;
        };

        connection.RecoverySucceededAsync += async (_, _) => await RebindAllQueuesAfterRecovery();

        connection.ConnectionRecoveryErrorAsync += (_, args) =>
        {
            _logger?.LogCritical(args.Exception, "Recovery failed");
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Re-declares and re-binds every queue a mediator on this connection owns. Bindings of auto-deleted
    /// queues do not survive a broker-side drop, so without this targeted delivery silently stops.
    /// </summary>
    private async Task RebindAllQueuesAfterRecovery()
    {
        _logger?.LogInformation("Recovery succeeded");

        foreach (var m in _rabbitMediatorInstances)
        {
            try
            {
                _logger?.LogInformation("Reconfiguring mediator {mediator}", m.Key);
                var configuration = TryGetConfiguration(m.Value);
                if (configuration == null)
                    continue; //disposed while we were recovering
                foreach (var queuePair in configuration.OwnedQueues)
                {
                    if (!configuration.QueueToExchangeBindings.TryGetValue(queuePair.Key, out var exchangeNameAndType))
                        continue;

                    _logger?.LogInformation("Rebinding queue {queue} to exchange {exchange}", queuePair.Key,
                        exchangeNameAndType.exchangeName);
                    try
                    {
                        // Fanout exchanges (broadcast) bind on BroadcastRoutingKey; direct exchanges
                        // owned by this instance are targeted queues and must be re-bound on the
                        // instance/scope routing key, otherwise targeted delivery (incl. every
                        // request/response) silently stops after a connection recovery.
                        var recoveryRoutingKey = exchangeNameAndType.exchangeType == ExchangeType.Fanout
                            ? RabbitNaming.BroadcastRoutingKey
                            : RabbitNaming.InstanceRoutingKey(InstanceId, configuration.RabbitMediator.ScopeId);
                        await queuePair.Value.ExchangeDeclareAsync(exchangeNameAndType.exchangeName,
                            exchangeNameAndType.exchangeType, false, false);
                        await queuePair.Value.QueueBindAsync(queuePair.Key, exchangeNameAndType.exchangeName,
                            recoveryRoutingKey);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error in QueueBindAsync");
                    }
                }

                _logger?.LogInformation("Reconfiguring mediator {mediator} done", m.Key);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error recovering mediate {mediator}", m.Key);
            }
        }
    }

    /// <summary>
    /// Send, receive and ack each get their own channels: publisher confirmations only make sense on the
    /// sending ones, and the receiving ones need the dispatch concurrency.
    /// </summary>
    private async Task CreateChannels(IConnection connection)
    {
        Task<IChannel> CreateSendChannel() => connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true));

        Task<IChannel> CreateReceiveChannel() => connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: false,
            publisherConfirmationTrackingEnabled: false,
            consumerDispatchConcurrency: _consumerDispatchConcurrency));

        Task<IChannel> CreateAckChannel() => connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: false,
            publisherConfirmationTrackingEnabled: false));

        _sendMessageChannel = await CreateSendChannel();
        _sendRequestChannel = await CreateSendChannel();
        _sendResponseChannel = await CreateSendChannel();

        _receiveMessageChannel = await CreateReceiveChannel();
        _receiveRequestChannel = await CreateReceiveChannel();
        _receiveResponseChannel = await CreateReceiveChannel();

        _receiveAckChannel = await CreateAckChannel();
        _sendAckChannel = await CreateAckChannel();
    }

    /// <summary>
    /// One ack queue per multiplexer instance. Consumers of confirmed messages publish their ack here, which
    /// completes the awaiter <see cref="Send{TMessageType}"/> is waiting on.
    /// </summary>
    private async Task StartAckConsumer()
    {
        var ackQueue = await _receiveAckChannel!.QueueDeclareAsync(RabbitNaming.AckQueue(InstanceId),
            durable: false, exclusive: true, autoDelete: true);

        var ackConsumer = new AsyncEventingBasicConsumer(_receiveAckChannel);
        ackConsumer.ReceivedAsync += async (_, args) =>
        {
            var ackMsg = await _serializerHelper.Deserialize<SentObjectAck>(args.Body);
            Debug.Assert(ackMsg != null);
            if (!_targetAckWaiters.Remove(ackMsg.CorrelationId, out var waiter))
                return;
            waiter.TaskCompletionSource.SetResult(ackMsg);
        };
        await _receiveAckChannel.BasicConsumeAsync(ackQueue.QueueName, true, ackConsumer);
    }
}
