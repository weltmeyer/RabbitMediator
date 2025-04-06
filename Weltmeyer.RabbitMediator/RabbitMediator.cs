using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Weltmeyer.RabbitMediator.ConsumerBases;
using Weltmeyer.RabbitMediator.Contracts;
using Weltmeyer.RabbitMediator.MessageBases;

namespace Weltmeyer.RabbitMediator;

public partial class RabbitMediator : IAsyncDisposable
{
    private readonly ILogger<RabbitMediator>? _logger;
    private readonly Type[] _consumerTypes;
    private readonly JsonSerializerHelper _serializerHelper = new();
    private readonly string? _instanceKey;
    private readonly ushort _consumerDispatchConcurrency;
    private readonly ConnectionFactory _connectionFactory;
    private IConnection? _connection;

    private IChannel? _sendMessageChannel;
    private IChannel? _sendRequestChannel;
    private IChannel? _sendResponseChannel;

    private IChannel? _receiveMessageChannel;
    private IChannel? _receiveRequestChannel;
    private IChannel? _receiveResponseChannel;

    private IChannel? _receiveAckChannel;
    private IChannel? _sendAckChannel;
    public Guid InstanceId { get; } = Guid.NewGuid();


    private const string TargetedExchangeName = "TargetedExchange";
    private const string BroadCastExchangeName = "BroadCastExchange";
    private const string AnyTargetedExchangeName = "AnyTargetedExchange";

    private const string BroadcastRoutingKey = "broadcast";
    private const string SharedRoutingKey = "broadcast";
    private const string SharedQueuePrefix = "shared";

    private const string InputQueuePrefixMessage = "inputMessage";
    private const string InputQueuePrefixRequest = "inputRequest";
    private const string InputQueuePrefixResponse = "inputResponse";

    private const string AckQueuePrefix = "ackqueue";

    private const string KeySeparator = "::";

    public RabbitMediator(string connectionString, Type[] consumerTypes,
        ushort consumerDispatchConcurrency = 10, ILogger<RabbitMediator>? logger = null)
    {
        _logger = logger;
        _consumerTypes = consumerTypes;
        _instanceKey = null;
        _consumerDispatchConcurrency = consumerDispatchConcurrency;
        _connectionFactory = new ConnectionFactory
        {
            Uri = new Uri(connectionString),
            AutomaticRecoveryEnabled = true,
        };
    }

    internal RabbitMediator(ILogger<RabbitMediator> logger, Type[] consumerTypes, string connectionString,
        string? instanceKey, ushort consumerDispatchConcurrency)
    {
        _logger = logger;
        _consumerTypes = consumerTypes;
        _instanceKey = instanceKey;
        _consumerDispatchConcurrency = consumerDispatchConcurrency;
        _connectionFactory = new ConnectionFactory
        {
            Uri = new Uri(connectionString),
            AutomaticRecoveryEnabled = true,
        };
    }


    private async Task<bool> TryHandleSentObjectReceived(object _, BasicDeliverEventArgs eventArgs)
    {
        try
        {
            _logger?.LogTrace(
                "Received a message: Exchange: {Exchange} RoutingKey:{RoutingKey} BodyLength:{Length}",
                eventArgs.Exchange, eventArgs.RoutingKey, eventArgs.Body.Length);

            var sentObject = await _serializerHelper.Deserialize<ISentObject>(eventArgs.Body);
#if DEBUG
            Debug.Assert(sentObject != null);
            switch (sentObject)
            {
                case ITargetedSentObject targetedMessage:
                {
                    Debug.Assert(targetedMessage.TargetId == InstanceId);
                    break;
                }
            }
#endif

            switch (sentObject)
            {
                case IMessage message:
                {
                    var handleResult = await HandleMessage(message);
                    return handleResult;
                }
                case IRequest request:
                {
                    var handleResult = await HandleRequest(request);
                    return handleResult;
                }
                default:
                    _logger?.LogError("SentObject of type {SentObjectType} has not been handled.",
                        sentObject.GetType().FullName);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "Could not work on received object:{EventArgs}", eventArgs);
            //Debugger.Break();
        }

        return false;
    }

    private async Task HandleSentObjectReceived(object sender, BasicDeliverEventArgs eventArgs)
    {
        //ack must be sent via source channel
        var consumer = (AsyncEventingBasicConsumer)sender;

        var success = await TryHandleSentObjectReceived(sender, eventArgs);
        if (success)
        {
            await consumer.Channel.BasicAckAsync(eventArgs.DeliveryTag, false);
        }
        else
        {
            //should we always requeue if we are not successful?
            //maybe another consumer can work on this?
            //but if the message was exactly for us...hmmm...
            //await _channel!.BasicNackAsync(eventArgs.DeliveryTag, false, false);

            await consumer.Channel.BasicRejectAsync(eventArgs.DeliveryTag, false);
            //await _channel.BasicPublishAsync(eventArgs.BasicProperties.ReplyToAddress)
        }
    }

    private async Task<bool> HandleMessage(IMessage message)
    {
        if (_messageConsumers.TryGetValue(message.GetType().FullName!, out var consumer))
        {
            var sentObjectAck = new SentObjectAck
            {
                SentId = message.SentId,
                Target = message.SenderId,
            };

            try
            {
                var consumeMethod = consumer.GetType().GetMethod(nameof(IMessageConsumer<Message>.Consume))!;
                await (Task)consumeMethod.Invoke(consumer, [message])!;
                sentObjectAck.Success = true;
            }
            catch (Exception ex)
            {
                if (ex is TargetInvocationException && ex.InnerException is not null)
                    ex = ex.InnerException; // dont return the invocation exception. return the actual exception within the worker.
                sentObjectAck.Success = false;
                sentObjectAck.ExceptionData = ExceptionData.FromException(ex);
            }

            if (message.RequireAck)
            {
                var targetQueue = $"{AckQueuePrefix}{KeySeparator}{message.SenderId}";
                if (!_isDisposing)
                {
                    await _serializerHelper.Serialize(sentObjectAck, async data =>
                    {
                        await _sendAckChannel!.BasicPublishAsync(string.Empty, targetQueue,
                            data);
                    });
                }
            }

            return sentObjectAck.Success;
        }

        if (message is Response response)
        {
            if (_responseWaiters.Remove(response.RequestId, out var waiter))
            {
                waiter.Result = response;
                waiter.TaskCompletionSource.SetResult();
                return true;
            }

            _logger?.LogError("Got a response of type {ResponseType} - but did not expect it",
                response.GetType().FullName);
        }

        _logger?.LogError("No message consumer to handle a message of type {MessageType}", message.GetType().FullName);
        return false;
    }

    private async Task<bool> HandleRequest(IRequest request)
    {
        _requestConsumers.TryGetValue(request.GetType().FullName!, out var consumer);
        Debug.Assert(consumer != null);

        var consumeMethod = consumer.GetType()
            .GetMethod(nameof(IRequestConsumer<Request<Response>, Response>.Consume))!;

        Response response;
        try
        {
            var runningTask = (Task)consumeMethod.Invoke(consumer, [request])!;
            await runningTask;
            response = (Response)runningTask.GetType().GetProperty("Result")?.GetValue(runningTask)!;
            response.Success = true;
        }
        catch (Exception ex)
        {
            var methodResultType = consumeMethod.ReturnType.GenericTypeArguments.First(); //Task<0>
            response = (Response)Activator.CreateInstance(methodResultType)!;
            response.Success = false;
            if (ex is TargetInvocationException && ex.InnerException is not null)
                ex = ex.InnerException; // dont return the invocation exception. return the actual exception within the worker.
            response.ExceptionData = ExceptionData.FromException(ex);
        }

        response.RequestId = request.RequestId;
        response.TargetId = request.SenderId;
        response.SenderId = this.InstanceId;
        response.SentId = request.SentId;

        var targetQueue =
            $"{InputQueuePrefixResponse}{KeySeparator}{response.GetType().FullName}{KeySeparator}{response.TargetId}";
        await _serializerHelper.Serialize(response,
            async data => { await _sendResponseChannel!.BasicPublishAsync(string.Empty, targetQueue, data); });

        return true;
    }

    private readonly Dictionary<string, AsyncEventingBasicConsumer> _consumers = new();


    private SemaphoreSlim _ensureReceiverSemaphore = new(1, 1);

    private Task EnsureReceiver<TISentObjectType>()
        where TISentObjectType : ISentObject
    {
        return EnsureReceiver(typeof(TISentObjectType));
    }

    private async Task EnsureReceiver(Type sentObjectType)
    {
        var typeName = sentObjectType.FullName!;
        if (_consumers.ContainsKey(typeName))
            return; //fast return, no semaphore needed :)


        await _ensureReceiverSemaphore.WaitAsync();
        try
        {
            if (_consumers.ContainsKey(typeName))
                return;

            string exchangeName;
            QueueDeclareOk? queue;

            var useChannel = _receiveMessageChannel!;
            var inputQueuePrefix = InputQueuePrefixMessage;
            _serializerHelper.AddTypeIfMissing(sentObjectType);
            if (sentObjectType.IsAssignableTo(typeof(Response)))
            {
                useChannel = _receiveResponseChannel!;
                inputQueuePrefix = InputQueuePrefixResponse;
            }
            else if (sentObjectType.IsAssignableTo(typeof(IRequest)))
            {
                useChannel = _receiveRequestChannel!;
                inputQueuePrefix = InputQueuePrefixRequest;
            }

            if (sentObjectType.IsAssignableTo(typeof(ITargetedSentObject)))
            {
                exchangeName = TargetedExchangeName + KeySeparator + typeName;
                await useChannel.ExchangeDeclareAsync(exchangeName, ExchangeType.Direct, false, true);

                queue = await useChannel.QueueDeclareAsync(
                    $"{inputQueuePrefix}{KeySeparator}{typeName}{KeySeparator}{InstanceId}",
                    durable: false, exclusive: true,
                    autoDelete: true);

                await useChannel.QueueBindAsync(queue.QueueName, exchangeName, InstanceId.ToString());
            }
            else if (sentObjectType.IsAssignableTo(typeof(IBroadCastSentObject)))
            {
                exchangeName = BroadCastExchangeName + KeySeparator + typeName;
                await useChannel.ExchangeDeclareAsync(exchangeName, ExchangeType.Fanout, false,
                    true);

                queue = await useChannel.QueueDeclareAsync(
                    $"{inputQueuePrefix}{KeySeparator}{typeName}{KeySeparator}{InstanceId}",
                    durable: false, exclusive: true,
                    autoDelete: true);

                await useChannel.QueueBindAsync(queue.QueueName,
                    exchangeName, BroadcastRoutingKey);
            }
            else if (sentObjectType.IsAssignableTo(typeof(IAnyTargetedSentObject)))
            {
                exchangeName = AnyTargetedExchangeName + KeySeparator + typeName;
                await useChannel.ExchangeDeclareAsync(exchangeName, ExchangeType.Direct, false,
                    true);

                queue = await useChannel.QueueDeclareAsync($"{SharedQueuePrefix}{KeySeparator}{typeName}",
                    durable: false,
                    exclusive: false,
                    autoDelete: true);


                await useChannel.QueueBindAsync(queue.QueueName, exchangeName, SharedRoutingKey);
            }
            else
            {
                //cant happen, guards in every caller. - right?
                throw new InvalidConstraintException($"Unknown sendObjectType: {sentObjectType.FullName}");
            }

            var consumer = new AsyncEventingBasicConsumer(useChannel);
            consumer.ReceivedAsync += HandleSentObjectReceived;
            var registeredSem =
                new SemaphoreSlim(1,
                    1); //used to make sure then receiver is registered before returning. TaskCompletionSource could also be used?

            consumer.RegisteredAsync += (_, args) =>
            {
                _logger?.LogTrace("Consumer registered {tags}", string.Join(",", args.ConsumerTags));
                registeredSem.Release();
                return Task.CompletedTask;
            };
#if DEBUG
            var gotSem = await registeredSem.WaitAsync(TimeSpan.FromSeconds(10));
            Debug.Assert(gotSem);
#else
            await registeredSem.WaitAsync(TimeSpan.FromSeconds(10));
#endif
            consumer.UnregisteredAsync += (_, args) =>
            {
                _logger?.LogWarning("Consumer unregistered {tags}", string.Join(",", args.ConsumerTags));
                return Task.CompletedTask;
            };
            consumer.ShutdownAsync += (_, args) =>
            {
                _logger?.LogWarning("Consumer shutDown {ReplyText}, {MethodId}", args.ReplyText, args.MethodId);
                return Task.CompletedTask;
            };

            _ = await useChannel.BasicConsumeAsync(queue.QueueName, false,
                exchangeName + KeySeparator + InstanceId.ToString(),
                consumer);

            await registeredSem.WaitAsync();
            _consumers.Add(typeName, consumer);
        }
        finally
        {
            _ensureReceiverSemaphore.Release();
        }
    }

    private readonly Dictionary<string, IConsumer> _messageConsumers = new();
    private readonly Dictionary<string, IConsumer> _requestConsumers = new();
    private readonly ConcurrentDictionary<Guid, RequestResponseAwaiter> _responseWaiters = new();
    private readonly ConcurrentDictionary<Guid, TargetAckAwaiter> _targetAckWaiters = new();

    private class RequestResponseAwaiter
    {
        public readonly TaskCompletionSource TaskCompletionSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public readonly Guid RequestId = Guid.CreateVersion7();
        public Response? Result;
    }

    private class TargetAckAwaiter
    {
        public readonly TaskCompletionSource<SentObjectAck> TaskCompletionSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public required Guid SentId;
    }

    private readonly ManualResetEventSlim _configureBusDoneEvent = new(false);

    public async Task ConfigureBus(IServiceProvider serviceProvider, IConnection? customConnection = null)
    {
        Debug.Assert(_connection == null);
        var myName =
            $"{AppDomain.CurrentDomain.FriendlyName}{KeySeparator}{this.InstanceId}{KeySeparator}{this._instanceKey ?? "unKeyed"}";
        _connection = customConnection ?? await _connectionFactory.CreateConnectionAsync(clientProvidedName: myName);

        _connection.ConnectionShutdownAsync += (_, args) =>
        {
            if (_isDisposing)
                return Task.CompletedTask;
            _logger?.LogWarning("Connection shutdown {args}", args);
            return Task.CompletedTask;
        };

        _connection.RecoveringConsumerAsync += (_, args) =>
        {
            _logger?.LogInformation("Recover consumer... {ConsumerTag}", args.ConsumerTag);
            return Task.CompletedTask;
        };


        _connection.RecoverySucceededAsync += (_, _) =>
        {
            _logger?.LogInformation("Recovery succeeded");
            return Task.CompletedTask;
        };
        _connection.ConnectionRecoveryErrorAsync += (_, args) =>
        {
            if (_isDisposing)
                return Task.CompletedTask;
            _logger?.LogCritical(args.Exception, "Recovery failed");
            return Task.CompletedTask;
        };

        _sendMessageChannel = await _connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true
        ));

        _sendRequestChannel = await _connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true
        ));

        _sendResponseChannel = await _connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true
        ));


        _receiveMessageChannel = await _connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: false,
            publisherConfirmationTrackingEnabled: false,
            consumerDispatchConcurrency: _consumerDispatchConcurrency
        ));


        _receiveRequestChannel = await _connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: false,
            publisherConfirmationTrackingEnabled: false,
            consumerDispatchConcurrency: _consumerDispatchConcurrency
        ));

        _receiveResponseChannel = await _connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: false,
            publisherConfirmationTrackingEnabled: false,
            consumerDispatchConcurrency: _consumerDispatchConcurrency
        ));

        _receiveAckChannel = await _connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: false,
            publisherConfirmationTrackingEnabled: false
        ));
        _sendAckChannel = await _connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: false,
            publisherConfirmationTrackingEnabled: false
        ));

/*
            _channel.BasicAcksAsync += (_, args) =>
            {
                _logger.LogTrace("Acks Received");

                //if(_ackWaitContainers.ContainsKey())
                return Task.CompletedTask;
            };

            _channel.BasicNacksAsync += (_, args) =>
            {
                _logger.LogWarning("NAcks Received");
                return Task.CompletedTask;
            };

            _channel.BasicReturnAsync += (_, args) =>
            {
                _logger.LogWarning("Return Received");
                return Task.CompletedTask;
            };*/


        var allConsumerTypes = _consumerTypes
            .Where(t => t.IsAssignableTo(typeof(IConsumer)) && !t.IsAbstract)
            .ToArray();
#if DEBUG
        var sentObjectTypes = allConsumerTypes.Select(ct => ct.Assembly).Distinct().SelectMany(a => a.GetTypes())
            .Where(t => t.IsAssignableTo(typeof(ISentObject)) && !t.IsAbstract)
            .ToList();
#endif

        //this._sentObjectTypes = sentObjectTypes.ToArray();


        var ackQueue = await _receiveAckChannel.QueueDeclareAsync($"{AckQueuePrefix}{KeySeparator}{InstanceId}",
            false,
            true, true);

        var ackConsumer = new AsyncEventingBasicConsumer(_receiveAckChannel);
        ackConsumer.ReceivedAsync += async (_, args) =>
        {
            var ackMsg = await _serializerHelper.Deserialize<SentObjectAck>(args.Body);
            Debug.Assert(ackMsg != null);
            if (!_targetAckWaiters.Remove(ackMsg.SentId, out var waiter))
                return;
            waiter.TaskCompletionSource.SetResult(ackMsg);
        };
        await _receiveAckChannel.BasicConsumeAsync(ackQueue.QueueName, true, ackConsumer);


        var iMessageConsumerType = typeof(IMessageConsumer<>);
        var iRequestConsumerType = typeof(IRequestConsumer<,>);
        foreach (var consumerType in allConsumerTypes)
        {
            var interfaces = consumerType.GetInterfaces();
            var messageConsumerInterfaces = interfaces.Where(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == iMessageConsumerType).ToArray();
            var requestConsumerInterfaces = interfaces.Where(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == iRequestConsumerType).ToArray();
            foreach (var messageConsumerInterface in messageConsumerInterfaces)
            {
                var messageType = messageConsumerInterface.GetGenericArguments()[0];
#if DEBUG
                Debug.Assert(sentObjectTypes.Contains(messageType));
#endif
                await EnsureReceiver(messageType);
                var consumerInstance = (IConsumer)ActivatorUtilities.CreateInstance(serviceProvider, consumerType);
                _messageConsumers.Add(messageType.FullName!, consumerInstance);
            }

            foreach (var requestConsumerInterface in requestConsumerInterfaces)
            {
                var requestType = requestConsumerInterface.GetGenericArguments()[0];
#if DEBUG
                var responseType = requestConsumerInterface.GetGenericArguments()[1];
                Debug.Assert(sentObjectTypes.Contains(requestType));
                Debug.Assert(sentObjectTypes.Contains(responseType));
#endif
                await EnsureReceiver(requestType);
                var consumerInstance = (IConsumer)ActivatorUtilities.CreateInstance(serviceProvider, consumerType);
                _requestConsumers.Add(requestType.FullName!, consumerInstance);
            }
        }

        _configureBusDoneEvent.Set();
    }


    private bool _isDisposing;

    public async ValueTask DisposeAsync()
    {
        _isDisposing = true;
        if (_connection != null)
        {
            await _connection.CloseAsync();
            /*
            while (_connection.IsOpen)
                try
                {
                    await _connection.CloseAsync();
                }
                catch (OperationCanceledException)
                {
                    //ignore
                }*/

            await _connection.DisposeAsync();
        }

        if (_sendMessageChannel != null) await _sendMessageChannel.DisposeAsync();
        _configureBusDoneEvent.Dispose();
    }
}