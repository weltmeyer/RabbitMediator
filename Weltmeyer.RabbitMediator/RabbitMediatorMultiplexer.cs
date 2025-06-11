using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using Weltmeyer.RabbitMediator.Contracts.ConsumerBases;
using Weltmeyer.RabbitMediator.Contracts.Contracts;
using Weltmeyer.RabbitMediator.Contracts.MessageBases;

namespace Weltmeyer.RabbitMediator;

internal class RabbitMediatorMultiplexer : IAsyncDisposable, IDisposable
{
    public Guid InstanceId { get; } = Guid.NewGuid();

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


    private readonly ConcurrentDictionary<Guid, TargetAckAwaiter> _targetAckWaiters = new();
    private readonly ConcurrentDictionary<Guid, RequestResponseAwaiter> _responseWaiters = new();


    private readonly List<RabbitMultiplexerMediatorConfiguration> _rabbitMultiplexerMediatorConfigurations = new();


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


    internal async Task<TResponse> Request<TRequest, TResponse>(RabbitMediator rabbitMediator,
        TRequest request, TimeSpan? responseTimeOut)
        where TResponse : Response
        where TRequest : Request<TResponse>
    {
        using var activity = Telemetry.ActivitySource.StartActivity(ActivityKind.Producer);
        if (rabbitMediator.Disposed)
            throw new ObjectDisposedException(nameof(IRabbitMediator));

        {
            if (request is ITargetedSentObject targetedMessage &&
                (targetedMessage.TargetInstance.InstanceId == Guid.Empty ||
                 targetedMessage.TargetInstance.InstanceScope == Guid.Empty))
                throw new InvalidOperationException("TargetId not set!");
        }
        var configuration = _rabbitMultiplexerMediatorConfigurations.First(cfg => cfg.RabbitMediator == rabbitMediator);

        await EnsureReceiver(rabbitMediator, typeof(TResponse));
        var routingKey = request switch
        {
            //IBroadCastSentObject => BroadcastRoutingKey,
            IAnyTargetedSentObject => SharedRoutingKey,
            ITargetedSentObject tm => tm.TargetInstance.InstanceId.ToString() + "_" +
                                      tm.TargetInstance.InstanceScope.ToString(),
            _ => throw new ArgumentException("Invalid message type")
        };

        var typeName = typeof(TRequest).FullName;

        var exchangeName = request switch
        {
            //IBroadCastSentObject => BroadCastExchangeName + KeySeparator + typeName,
            IAnyTargetedSentObject => AnyTargetedExchangeName + KeySeparator + typeName,
            ITargetedSentObject => TargetedExchangeName + KeySeparator + typeName,
            _ => throw new ArgumentException("Invalid message type")
        };


        request.SenderInstance = new InstanceInformation
        {
            InstanceId = this.InstanceId,
            InstanceScope = rabbitMediator.ScopeId,
        };
        request.CorrelationId = Guid.NewGuid();
        request.TelemetryTraceParent = Activity.Current?.Id;
        request.TelemetryTraceState = Activity.Current?.TraceStateString;

        //request.TelemetryTraceParent=Activity?.Current.Context.

        var awaiter = new RequestResponseAwaiter(request.CorrelationId);
        _responseWaiters.TryAdd(awaiter.CorrelationId, awaiter);


        try
        {
            activity?.Enrich(request);
            await _serializerHelper.Serialize(request, async data =>
            {
                await _sendRequestChannel!.BasicPublishAsync(exchangeName, routingKey, true,
                    data);
            });
        }
        catch (RabbitMQClientException rabbitException)
        {
            _logger?.LogWarning(rabbitException, "Publishing failed");
            _responseWaiters.TryRemove(awaiter.CorrelationId, out _);
            var publishErrorResponse = Activator.CreateInstance<TResponse>();
            publishErrorResponse.Success = false;
            publishErrorResponse.SendFailure = true;
            publishErrorResponse.TargetInstance = new InstanceInformation
            {
                InstanceId = this.InstanceId,
                InstanceScope = rabbitMediator.ScopeId
            };
            publishErrorResponse.CorrelationId = request.CorrelationId;


            {
                publishErrorResponse.SenderInstance = InstanceInformation.Empty;
                /*
                if (request is ITargetedSentObject targetedMessage)
                {
                    publishErrorResponse.SenderInstance = targetedMessage.TargetInstance;
                }*/
            }
            return publishErrorResponse;
        }


        var timeOutTask = Task.Delay(responseTimeOut ?? configuration.Configuration.DefaultResponseTimeOut);
        var responseWaitTask = awaiter.TaskCompletionSource.Task;
        var waitResult = await Task.WhenAny(timeOutTask, responseWaitTask);
        if (waitResult == responseWaitTask)
        {
            await responseWaitTask;
            Debug.Assert(awaiter.Result != null);
            var response = (TResponse?)awaiter.Result;
            return response ?? throw new InvalidCastException();
        }

        _responseWaiters.TryRemove(awaiter.CorrelationId, out _);
        //timed out
        var timedOutResponse = Activator.CreateInstance<TResponse>();
        timedOutResponse.Success = false;
        timedOutResponse.TimedOut = true;
        timedOutResponse.TargetInstance = new InstanceInformation
        {
            InstanceId = this.InstanceId,
            InstanceScope = rabbitMediator.ScopeId
        };
        timedOutResponse.CorrelationId = request.CorrelationId;


        {
            timedOutResponse.SenderInstance = InstanceInformation.Empty;

            /*if (request is ITargetedSentObject targetedMessage)
            {
                timedOutResponse.SenderInstance = targetedMessage.TargetInstance;
            }*/
        }


        return timedOutResponse;
    }

    internal async Task<SendResult> Send<TMessageType>(RabbitMediator rabbitMediator,
        TMessageType message, bool confirmPublish, TimeSpan? confirmTimeOut)
        where TMessageType : Message
    {
        using var activity = Telemetry.ActivitySource.StartActivity(ActivityKind.Producer);
        if (rabbitMediator.Disposed)
            throw new ObjectDisposedException(nameof(IRabbitMediator));

        //await EnsureReceiver(rabbitMediator, typeof(TMessageType));

        if (message is ITargetedSentObject targetedMessage &&
            (targetedMessage.TargetInstance.InstanceId == Guid.Empty ||
             targetedMessage.TargetInstance.InstanceScope == Guid.Empty))
            throw new InvalidOperationException("TargetId not set!");


        var routingKey = message switch
        {
            IBroadCastSentObject => BroadcastRoutingKey,
            IAnyTargetedSentObject => SharedRoutingKey,
            ITargetedSentObject tm => tm.TargetInstance.InstanceId.ToString() + "_" +
                                      tm.TargetInstance.InstanceScope.ToString(),
            _ => throw new ArgumentException("Invalid message type")
        };

        var typeName = typeof(TMessageType).FullName;

        var exchangeName = message switch
        {
            IBroadCastSentObject => BroadCastExchangeName + KeySeparator + typeName,
            IAnyTargetedSentObject => AnyTargetedExchangeName + KeySeparator + typeName,
            ITargetedSentObject => TargetedExchangeName + KeySeparator + typeName,
            _ => throw new ArgumentException("Invalid message type")
        };

        message.SenderInstance = new InstanceInformation
        {
            InstanceId = InstanceId,
            InstanceScope = rabbitMediator.ScopeId,
        };
        message.CorrelationId = Guid.NewGuid();
        message.RequireAck = confirmPublish;
        message.TelemetryTraceParent = Activity.Current?.Id;
        message.TelemetryTraceState = Activity.Current?.TraceStateString;

        var props = new BasicProperties
        {
            //CorrelationId = message.SentId.ToString(),
        };
        try
        {
            TargetAckAwaiter? targetAckAwaiter = null;
            if (confirmPublish)
            {
                targetAckAwaiter = new TargetAckAwaiter(message.CorrelationId);
                _targetAckWaiters.TryAdd(targetAckAwaiter.CorrelationId, targetAckAwaiter);
            }

            activity?.Enrich(message);
            await _serializerHelper.Serialize(message, async data =>
            {
                await _sendMessageChannel!.BasicPublishAsync(exchangeName, routingKey, confirmPublish, props,
                    data);
            });


            if (confirmPublish)
            {
                var configuration =
                    _rabbitMultiplexerMediatorConfigurations.First(cfg => cfg.RabbitMediator == rabbitMediator);
                var timeOutTask = Task.Delay(confirmTimeOut ?? configuration.Configuration.DefaultConfirmTimeOut);
                var ackMsgTask = targetAckAwaiter!.TaskCompletionSource.Task;
                var waitResult = await Task.WhenAny(timeOutTask, ackMsgTask);
                if (waitResult == ackMsgTask)
                {
                    var ackMsg = await ackMsgTask;
                    return new SendResult
                        { Success = ackMsg.Success, ExceptionData = ackMsg.ExceptionData };
                }

                //timed out
                return new SendResult
                    { Success = false, TimedOut = true };
            }
        }
        catch (PublishException ex)
        {
            _logger?.LogWarning(ex, "Publishing failed");
            if (confirmPublish)
                _targetAckWaiters.TryRemove(message.CorrelationId, out _);
            return new SendResult { Success = false, SendFailure = true };
        }


        return new SendResult { Success = true };
    }


    internal async Task ConfigureRabbitMediator(RabbitMediator rabbitMediator)
    {
        await Configure();
        var iMessageConsumerType = typeof(IMessageConsumer<>);
        var iRequestConsumerType = typeof(IRequestConsumer<,>);

        var configuration = _rabbitMultiplexerMediatorConfigurations.First(cfg => cfg.RabbitMediator == rabbitMediator);


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

    public RabbitMediator CreateRabbitMediator(IServiceProvider serviceProvider,
        RabbitMediatorConfiguration configuration)
    {
        configuration.Validate();
        var newMediator = new RabbitMediator(this);
        _rabbitMultiplexerMediatorConfigurations.Add(
            new RabbitMultiplexerMediatorConfiguration(newMediator, configuration, serviceProvider));
        return newMediator;
    }

    internal IConsumer? GetConsumer(RabbitMediator rabbitMediator, Type consumerType)
    {
        if (!consumerType.IsAssignableTo(typeof(IConsumer)))
            throw new ArgumentException($"The type {consumerType.FullName} does not implement {nameof(IConsumer)}");

        var configuration = _rabbitMultiplexerMediatorConfigurations.First(cfg => cfg.RabbitMediator == rabbitMediator);

        if (!configuration.Configuration.GetAllConsumerTypes().Contains(consumerType))
            return null;
        return configuration.ConsumerInstances.GetOrAdd(consumerType, static (_, serviceProviderAndType) =>
            (IConsumer)ActivatorUtilities.CreateInstance(serviceProviderAndType.ServiceProvider,
                serviceProviderAndType.consumerType), (configuration.ServiceProvider, consumerType));
    }


    internal async Task DisposeRabbitMediatorConnection(RabbitMediator mediator)
    {
        var configuration =
            this._rabbitMultiplexerMediatorConfigurations.First(cfg => cfg.RabbitMediator == mediator);
        foreach (var consumerKv in configuration.ConsumerTags)
        {
            await consumerKv.Value.BasicCancelAsync(consumerKv.Key, true);
        }


        foreach (var queue in configuration.OwnedQueues)
        {
            await queue.Value.QueueDeleteAsync(queue.Key, false, false, true);
        }
    }


    private async Task EnsureReceiver(RabbitMediator mediatorT, Type sentObjectType)
    {
        if (mediatorT.Disposed)
            return;

        var typeName = sentObjectType.FullName!;

        var configuration = _rabbitMultiplexerMediatorConfigurations.First(x => x.RabbitMediator == mediatorT);

        if (configuration.RegisteredConsumerTypes.Contains(sentObjectType))
            return;


        await configuration.EnsureReceiverSemaphore.WaitAsync();
        try
        {
            if (configuration.RegisteredConsumerTypes.Contains(sentObjectType))
                return;
            _logger?.LogInformation("Registering receiver for {ObjectType}", sentObjectType);

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
                await useChannel.ExchangeDeclareAsync(exchangeName, ExchangeType.Direct, false, false);

                queue = await useChannel.QueueDeclareAsync(
                    $"{inputQueuePrefix}{KeySeparator}{typeName}{KeySeparator}{InstanceId}{KeySeparator}{configuration.RabbitMediator.ScopeId}",
                    durable: false, exclusive: true,
                    autoDelete: true);
                configuration.OwnedQueues.TryAdd(queue.QueueName, useChannel);
                await useChannel.QueueBindAsync(queue.QueueName, exchangeName,
                    InstanceId.ToString() + "_" + configuration.RabbitMediator.ScopeId.ToString());
            }
            else if (sentObjectType.IsAssignableTo(typeof(IBroadCastSentObject)))
            {
                exchangeName = BroadCastExchangeName + KeySeparator + typeName;
                await useChannel.ExchangeDeclareAsync(exchangeName, ExchangeType.Fanout, false,
                    false);

                queue = await useChannel.QueueDeclareAsync(
                    $"{inputQueuePrefix}{KeySeparator}{typeName}{KeySeparator}{InstanceId}{KeySeparator}{configuration.RabbitMediator.ScopeId}",
                    durable: false, exclusive: true,
                    autoDelete: false);
                configuration.OwnedQueues.TryAdd(queue.QueueName, useChannel);
                await useChannel.QueueBindAsync(queue.QueueName,
                    exchangeName, BroadcastRoutingKey);
            }
            else if (sentObjectType.IsAssignableTo(typeof(IAnyTargetedSentObject)))
            {
                exchangeName = AnyTargetedExchangeName + KeySeparator + typeName;
                await useChannel.ExchangeDeclareAsync(exchangeName, ExchangeType.Direct, false,
                    false);

                queue = await useChannel.QueueDeclareAsync(
                    $"{SharedQueuePrefix}{KeySeparator}{typeName}",
                    durable: false,
                    exclusive: false,
                    autoDelete: false);


                await useChannel.QueueBindAsync(queue.QueueName, exchangeName, SharedRoutingKey);
            }
            else
            {
                //cant happen, guards in every caller. - right?
                throw new InvalidConstraintException($"Unknown sendObjectType: {sentObjectType.FullName}");
            }

            var consumer = new AsyncEventingBasicConsumer(useChannel);
            consumer.ReceivedAsync += (obj, args) => HandleSentObjectReceived(obj, args, configuration.RabbitMediator);
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

            var consumerTag = exchangeName + KeySeparator + InstanceId.ToString() + KeySeparator +
                              configuration.RabbitMediator.ScopeId.ToString();
            _ = await useChannel.BasicConsumeAsync(queue.QueueName, false,
                consumerTag,
                consumer);

            await registeredSem.WaitAsync();
            configuration.RegisteredConsumerTypes.Add(sentObjectType);
            configuration.ConsumerTags.Add(consumerTag, useChannel);
        }
        finally
        {
            configuration.EnsureReceiverSemaphore.Release();
        }
    }


    private async Task<bool> TryHandleSentObjectReceived(object _, BasicDeliverEventArgs eventArgs,
        RabbitMediator mediator)
    {
        if (mediator.Disposed)
            return false;

        try
        {
            _logger?.LogTrace(
                "Received a message: Exchange: {Exchange} RoutingKey:{RoutingKey} BodyLength:{Length}",
                eventArgs.Exchange, eventArgs.RoutingKey, eventArgs.Body.Length);

            var sentObject = await _serializerHelper.Deserialize<ISentObject>(eventArgs.Body);

            ActivityContext.TryParse(sentObject.TelemetryTraceParent, sentObject.TelemetryTraceState,
                out var parentActivityContext);
            using var activity =
                Telemetry.ActivitySource.StartActivity(ActivityKind.Consumer,
                    //parentContext: traceParentContextOkay ? parentActivityContext : default);
                    parentContext: parentActivityContext);
            activity?.Enrich(sentObject);
#if DEBUG
            Debug.Assert(sentObject != null);
            switch (sentObject)
            {
                case ITargetedSentObject targetedMessage:
                {
                    Debug.Assert(targetedMessage.TargetInstance.InstanceId == InstanceId);
                    break;
                }
            }
#endif

            switch (sentObject)
            {
                case IMessage message:
                {
                    activity?.AddEvent(new ActivityEvent("HandleMessage"));
                    var handleResult = await HandleMessage(message, mediator);
                    return handleResult;
                }
                case IRequest request:
                {
                    activity?.AddEvent(new ActivityEvent("HandleRequest"));
                    var handleResult = await HandleRequest(request, mediator);
                    return handleResult;
                }
                default:
                    _logger?.LogError("SentObject of type {SentObjectType} has not been handled.",
                        sentObject.GetType().FullName);
                    activity?.SetStatus(ActivityStatusCode.Error);
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

    private async Task HandleSentObjectReceived(object sender, BasicDeliverEventArgs eventArgs,
        RabbitMediator mediator)
    {
        //ack must be sent via source channel
        var consumer = (AsyncEventingBasicConsumer)sender;

        var success = await TryHandleSentObjectReceived(sender, eventArgs, mediator);
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


    private async Task<bool> HandleMessage(IMessage message, RabbitMediator mediator)
    {
        var configuration =
            _rabbitMultiplexerMediatorConfigurations.First(cfg => cfg.RabbitMediator == mediator);
        if (configuration.SentTypeToConsumerMapping.TryGetValue(message.GetType(), out var consumerType))
        {
            Activity.Current?.SetTag("ConsumerType", consumerType.FullName);
            var sentObjectAck = new SentObjectAck
            {
                CorrelationId = message.CorrelationId,
                Target = message.SenderInstance.InstanceId,
            };

            try
            {
                var consumer = mediator.GetConsumer(consumerType);

                var consumeMethod = consumerType.GetMethod(nameof(IMessageConsumer<Message>.Consume))!;
                await (Task)consumeMethod.Invoke(consumer, [message])!;
                sentObjectAck.Success = true;
            }
            catch (Exception ex)
            {
                if (ex is TargetInvocationException && ex.InnerException is not null)
                    ex = ex.InnerException; // don't return the invocation exception. return the actual exception within the worker.
                sentObjectAck.Success = false;
                sentObjectAck.ExceptionData = ExceptionData.FromException(ex);
                _logger?.LogError(ex, "Error in ConsumerInvoke");
                Activity.Current?.SetStatus(ActivityStatusCode.Error);
            }

            if (message.RequireAck)
            {
                var targetQueue = $"{AckQueuePrefix}{KeySeparator}{message.SenderInstance.InstanceId}";
                //if (!_isDisposing)
                {
                    await _serializerHelper.Serialize(sentObjectAck, async data =>
                    {
                        await _sendAckChannel!.BasicPublishAsync(string.Empty, targetQueue,
                            data);
                    });
                }
            }

            return true; //sentObjectAck.Success;
        }

        if (message is Response response)
        {
            if (_responseWaiters.Remove(response.CorrelationId, out var waiter))
            {
                waiter.Result = response;
                waiter.TaskCompletionSource.SetResult();
                return true;
            }

            _logger?.LogError("Got a response of type {ResponseType} - but did not expect it",
                response.GetType().FullName);
            Activity.Current?.SetStatus(ActivityStatusCode.Error);
        }

        _logger?.LogError("No message consumer to handle a message of type {MessageType}", message.GetType().FullName);
        Activity.Current?.SetStatus(ActivityStatusCode.Error);
        return false;
    }

    private async Task<bool> HandleRequest(IRequest request, RabbitMediator mediator)
    {
        var configuration =
            _rabbitMultiplexerMediatorConfigurations.First(cfg => cfg.RabbitMediator == mediator);
        configuration.SentTypeToConsumerMapping.TryGetValue(request.GetType(), out var consumerType);

        Debug.Assert(consumerType != null);
        Activity.Current?.SetTag("ConsumerType", consumerType.FullName);
        var consumer = GetConsumer(mediator, consumerType);
        //var consumer = serviceScopeContainer.ServiceProvider.GetService(consumerType);
        //consumer ??= Activator.CreateInstance(consumerType);
        //_requestConsumers.TryGetValue(request.GetType().FullName!, out var consumer);
        Debug.Assert(consumer != null);

        var consumeMethod = consumerType
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
                ex = ex.InnerException; // don't return the invocation exception. return the actual exception within the worker.
            response.ExceptionData = ExceptionData.FromException(ex);
            _logger?.LogError(ex, "Error in ConsumerInvoke");
            Activity.Current?.SetStatus(ActivityStatusCode.Error);
        }

        response.CorrelationId = request.CorrelationId;
        response.TelemetryTraceParent = Activity.Current?.Id;
        response.TelemetryTraceState = Activity.Current?.TraceStateString;
        response.SenderInstance = new InstanceInformation()
        {
            InstanceScope = mediator.ScopeId,
            InstanceId = this.InstanceId,
        };
        response.TargetInstance = request.SenderInstance;

        var targetQueue =
            $"{InputQueuePrefixResponse}{KeySeparator}{response.GetType().FullName}{KeySeparator}{response.TargetInstance.InstanceId}{KeySeparator}{response.TargetInstance.InstanceScope}";
        await _serializerHelper.Serialize(response,
            async data => { await _sendResponseChannel!.BasicPublishAsync(string.Empty, targetQueue, data); });

        return true;
    }


    private bool _configureDone;
    private readonly SemaphoreSlim _configureLock = new(1, 1);


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
            var myName = $"{AppDomain.CurrentDomain.FriendlyName}{KeySeparator}{this.InstanceId}";
            _connection ??=
                await _connectionFactory!.CreateConnectionAsync(clientProvidedName: myName,
                    cancellationToken: cancellationToken.Value);

            #region Create Channels

            _connection.ConnectionShutdownAsync += (_, args) =>
            {
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
                _logger?.LogCritical(args.Exception, "Recovery failed");
                return Task.CompletedTask;
            };

            async Task<IChannel> CreateSendChannel(IConnection connection)
            {
                return await connection.CreateChannelAsync(new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true));
            }

            async Task<IChannel> CreateReceiveChannel(IConnection connection)
            {
                return await connection.CreateChannelAsync(new CreateChannelOptions(
                    publisherConfirmationsEnabled: false,
                    publisherConfirmationTrackingEnabled: false,
                    consumerDispatchConcurrency: _consumerDispatchConcurrency
                ));
            }

            async Task<IChannel> CreateAckChannel(IConnection connection)
            {
                return await connection.CreateChannelAsync(new CreateChannelOptions(
                    publisherConfirmationsEnabled: false,
                    publisherConfirmationTrackingEnabled: false
                ));
            }


            _sendMessageChannel = await CreateSendChannel(_connection);

            _sendRequestChannel = await CreateSendChannel(_connection);

            _sendResponseChannel = await CreateSendChannel(_connection);


            _receiveMessageChannel = await CreateReceiveChannel(_connection);


            _receiveRequestChannel = await CreateReceiveChannel(_connection);

            _receiveResponseChannel = await CreateReceiveChannel(_connection);


            _receiveAckChannel = await CreateAckChannel(_connection);
            _sendAckChannel = await CreateAckChannel(_connection);

            #endregion


            var ackQueue = await _receiveAckChannel.QueueDeclareAsync($"{AckQueuePrefix}{KeySeparator}{InstanceId}",
                false,
                true, true);

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
            _configureDone = true;
        }
        finally
        {
            _configureLock.Release();
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