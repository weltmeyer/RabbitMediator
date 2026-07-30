using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Weltmeyer.RabbitMediator.Contracts.Contracts;
using Weltmeyer.RabbitMediator.Contracts.MessageBases;

namespace Weltmeyer.RabbitMediator;

/// <summary>The incoming side: declaring receive topology and dispatching what arrives on it.</summary>
internal partial class RabbitMediatorMultiplexer
{
    /// <summary>How long a delivery that arrived before this mediator was ready waits before being requeued.</summary>
    private static readonly TimeSpan NotReadyRequeueDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Declares exchange, queue and binding for one sent-object type of one mediator and starts consuming.
    /// Idempotent per mediator and type; returns once the broker confirmed the consumer registration.
    /// </summary>
    private async Task EnsureReceiver(RabbitMediator mediatorT, Type sentObjectType,
        CancellationToken cancellationToken = default)
    {
        if (mediatorT.Disposed)
            return;

        var typeName = RabbitNaming.TypeName(sentObjectType);

        var configuration = GetConfiguration(mediatorT);

        if (configuration.RegisteredConsumerTypes.ContainsKey(sentObjectType))
            return;

        await configuration.EnsureReceiverSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (configuration.RegisteredConsumerTypes.ContainsKey(sentObjectType))
                return;
            _logger?.LogInformation("Registering receiver for {ObjectType}", sentObjectType);

            var (useChannel, inputQueuePrefix) = ReceiveChannelFor(sentObjectType);
            await _serializerHelper.AddTypeIfMissing(sentObjectType);

            var (exchangeName, queueName) = await DeclareReceiveTopology(configuration, sentObjectType, typeName,
                useChannel, inputQueuePrefix, cancellationToken);

            var consumer = new AsyncEventingBasicConsumer(useChannel);
            consumer.ReceivedAsync += async (obj, args) =>
            {
                _logger?.LogInformation(
                    "Received message on queue {QueueName} with routingKey {RoutingKey}, exchange {Exchange}, tags: {ConsumerTags}",
                    queueName, args.RoutingKey, args.Exchange, args.ConsumerTag);
                await HandleSentObjectReceived(obj, args, configuration.RabbitMediator);
                _logger?.LogInformation("Handled message on queue {QueueName} with routingKey {RoutingKey}",
                    queueName, args.RoutingKey);
            };
            var registeredSem =
                new SemaphoreSlim(0,
                    1); //used to make sure then receiver is registered before returning. TaskCompletionSource could also be used?
            consumer.RegisteredAsync += (_, args) =>
            {
                _logger?.LogInformation("Consumer registered {tags}", string.Join(",", args.ConsumerTags));
                try
                {
                    if (registeredSem.CurrentCount == 0)
                        registeredSem.Release();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error in registeredSem.Release()");
                }

                return Task.CompletedTask;
            };
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

            var consumerTag = RabbitNaming.ConsumerTag(exchangeName, InstanceId, configuration.RabbitMediator.ScopeId);
            _ = await useChannel.BasicConsumeAsync(queueName, false, consumerTag, consumer, cancellationToken);

            await registeredSem.WaitAsync();

            configuration.RegisteredConsumerTypes.TryAdd(sentObjectType, true);
            configuration.ConsumerTags.TryAdd(consumerTag, useChannel);
        }
        finally
        {
            configuration.EnsureReceiverSemaphore.Release();
        }
    }

    /// <summary>Responses, requests and messages each get their own receive channel.</summary>
    private (IChannel channel, string inputQueuePrefix) ReceiveChannelFor(Type sentObjectType)
    {
        if (sentObjectType.IsAssignableTo(typeof(Response)))
            return (_receiveResponseChannel!, RabbitNaming.InputQueuePrefixResponse);
        if (sentObjectType.IsAssignableTo(typeof(IRequest)))
            return (_receiveRequestChannel!, RabbitNaming.InputQueuePrefixRequest);
        return (_receiveMessageChannel!, RabbitNaming.InputQueuePrefixMessage);
    }

    /// <summary>
    /// Declares the exchange/queue/binding trio matching the addressing style of
    /// <paramref name="sentObjectType"/>: one own queue per mediator for targeted and broadcast types, one
    /// queue shared by all consumers for any-targeted types.
    /// </summary>
    private async Task<(string exchangeName, string queueName)> DeclareReceiveTopology(
        RabbitMultiplexerMediatorConfiguration configuration, Type sentObjectType, string typeName, IChannel useChannel,
        string inputQueuePrefix, CancellationToken cancellationToken = default)
    {
        var scopeId = configuration.RabbitMediator.ScopeId;

        if (sentObjectType.IsAssignableTo(typeof(ITargetedSentObject)))
        {
            var exchangeName = RabbitNaming.TargetedExchange(typeName);
            await useChannel.ExchangeDeclareAsync(exchangeName, ExchangeType.Direct, false, false,
                cancellationToken: cancellationToken);
            var queue = await useChannel.QueueDeclareAsync(
                RabbitNaming.InputQueue(inputQueuePrefix, typeName, InstanceId, scopeId),
                durable: false, exclusive: true,
                autoDelete: true, cancellationToken: cancellationToken);
            configuration.OwnedQueues.TryAdd(queue.QueueName, useChannel);
            await useChannel.QueueBindAsync(queue.QueueName, exchangeName,
                RabbitNaming.InstanceRoutingKey(InstanceId, scopeId), cancellationToken: cancellationToken);
            configuration.QueueToExchangeBindings.TryAdd(queue.QueueName, (exchangeName, ExchangeType.Direct));
            return (exchangeName, queue.QueueName);
        }

        if (sentObjectType.IsAssignableTo(typeof(IBroadCastSentObject)))
        {
            var exchangeName = RabbitNaming.BroadcastExchange(typeName);
            await useChannel.ExchangeDeclareAsync(exchangeName, ExchangeType.Fanout, false, false,
                cancellationToken: cancellationToken);
            var queue = await useChannel.QueueDeclareAsync(
                RabbitNaming.InputQueue(inputQueuePrefix, typeName, InstanceId, scopeId),
                durable: false, exclusive: true,
                autoDelete: false, cancellationToken: cancellationToken);
            configuration.OwnedQueues.TryAdd(queue.QueueName, useChannel);
            await useChannel.QueueBindAsync(queue.QueueName, exchangeName, RabbitNaming.BroadcastRoutingKey,
                cancellationToken: cancellationToken);
            configuration.QueueToExchangeBindings.TryAdd(queue.QueueName, (exchangeName, ExchangeType.Fanout));
            return (exchangeName, queue.QueueName);
        }

        if (sentObjectType.IsAssignableTo(typeof(IAnyTargetedSentObject)))
        {
            var exchangeName = RabbitNaming.AnyTargetedExchange(typeName);
            await useChannel.ExchangeDeclareAsync(exchangeName, ExchangeType.Direct, false, false,
                cancellationToken: cancellationToken);
            var queue = await useChannel.QueueDeclareAsync(
                RabbitNaming.SharedQueue(typeName),
                durable: false,
                exclusive: false,
                autoDelete: false, cancellationToken: cancellationToken);
            await useChannel.QueueBindAsync(queue.QueueName, exchangeName, RabbitNaming.AnyTargetedRoutingKey,
                cancellationToken: cancellationToken);
            configuration.QueueToExchangeBindings.TryAdd(queue.QueueName, (exchangeName, ExchangeType.Direct));
            return (exchangeName, queue.QueueName);
        }

        //cant happen, guards in every caller. - right?
        throw new InvalidOperationException($"Unknown sendObjectType: {sentObjectType.FullName}");
    }

    private async Task HandleSentObjectReceived(object sender, BasicDeliverEventArgs eventArgs,
        RabbitMediator mediator)
    {
        // Taken before anything else: with a prefetch window the broker considers the message delivered while
        // it may still be waiting for a free dispatcher here, and that wait counts against the sender's
        // timeout just as much as the work itself does.
        var deliveredAt = Stopwatch.GetTimestamp();

        //ack must be sent via source channel
        var consumer = (AsyncEventingBasicConsumer)sender;
        if (!_configureDone || !mediator.ConfigureDone)
        {
            // Requeue until we are ready. The consumer is registered inside the configure run, so deliveries
            // can arrive during that window - and an immediate requeue makes the broker hand the same message
            // straight back, spinning both sides. Back off first.
            await Task.Delay(NotReadyRequeueDelay);
            await consumer.Channel.BasicRejectAsync(eventArgs.DeliveryTag, true);
            return;
        }

        var success = await TryHandleSentObjectReceived(eventArgs, mediator, deliveredAt);
        if (success)
        {
            await consumer.Channel.BasicAckAsync(eventArgs.DeliveryTag, false);
        }
        else
        {
            //should we always requeue if we are not successful?
            //maybe another consumer can work on this?
            //but if the message was exactly for us...hmmm...
            await consumer.Channel.BasicRejectAsync(eventArgs.DeliveryTag, false);
        }
    }

    private async Task<bool> TryHandleSentObjectReceived(BasicDeliverEventArgs eventArgs, RabbitMediator mediator,
        long deliveredAt)
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
                Telemetry.ActivitySource.StartActivity(ActivityKind.Consumer, parentContext: parentActivityContext);
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
                    return await HandleMessage(message, mediator);
                }
                case IRequest request:
                {
                    activity?.AddEvent(new ActivityEvent("HandleRequest"));
                    return await HandleRequest(request, mediator, deliveredAt, eventArgs.CancellationToken);
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
        }

        return false;
    }

    private async Task<bool> HandleMessage(IMessage message, RabbitMediator mediator)
    {
        var configuration = GetConfiguration(mediator);
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
                var consumeMethod = ConsumerInvoker.GetConsumeMethod(consumerType, message.GetType());
                await ConsumerInvoker.Invoke(consumer!, consumeMethod, message);
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
                var targetQueue = RabbitNaming.AckQueue(message.SenderInstance.InstanceId);
                await _serializerHelper.Serialize(sentObjectAck, async data =>
                {
                    var ackChannel = await _sendAckChannel!.GetAsync();
                    await ackChannel.BasicPublishAsync(string.Empty, targetQueue, data);
                });
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

            // The waiter is gone because the request already timed out and gave up. That is the normal end of
            // a timeout, not a fault of its own - the caller was told about it by the timeout exception. Warn
            // and stop here: falling through logged a second, misleading error claiming no consumer exists for
            // a response type that never had one, and the two always appeared together.
            _logger?.LogWarning(
                "Discarding a late response of type {ResponseType} (correlation {CorrelationId}) - the request " +
                "already timed out and stopped waiting", response.GetType().FullName, response.CorrelationId);
            return false;
        }

        _logger?.LogError("No message consumer to handle a message of type {MessageType}", message.GetType().FullName);
        Activity.Current?.SetStatus(ActivityStatusCode.Error);
        return false;
    }

    private async Task<bool> HandleRequest(IRequest request, RabbitMediator mediator, long deliveredAt,
        CancellationToken deliveryCancellationToken)
    {
        var configuration = GetConfiguration(mediator);
        configuration.SentTypeToConsumerMapping.TryGetValue(request.GetType(), out var consumerType);

        if (consumerType == null)
        {
            // A Debug.Assert used to guard this, so a release build dereferenced null, the receive loop logged
            // the NullReferenceException and dropped the request - leaving the requester to wait out its full
            // timeout for a failure we already knew about. Answer instead: the response type is part of the
            // request's own Request<TResponse> base, so we can build one without knowing any consumer.
            _logger?.LogError("No request consumer to handle a request of type {RequestType}",
                request.GetType().FullName);
            Activity.Current?.SetStatus(ActivityStatusCode.Error);
            return await RespondWithoutConsumer(request, mediator);
        }

        Activity.Current?.SetTag("ConsumerType", consumerType.FullName);
        var consumer = GetConsumer(mediator, consumerType);
        Debug.Assert(consumer != null);
        var consumeMethod = ConsumerInvoker.GetRequestConsumeMethod(consumerType, request.GetType());

        var remainingTime = RemainingTime(request, deliveredAt);
        if (remainingTime <= TimeSpan.Zero)
        {
            // The sender stopped waiting before we got to it, so running the consumer would be work nobody
            // collects. Answer so the failure is named rather than looking like silence.
            _logger?.LogWarning(
                "Dropping a request of type {RequestType} (correlation {CorrelationId}): its timeout of " +
                "{TimeOut} had already elapsed when it reached the consumer",
                request.GetType().FullName, request.CorrelationId, request.TimeOut);
            Activity.Current?.SetStatus(ActivityStatusCode.Error);
            return await RespondWithFailure(request, mediator, ConsumerInvoker.GetResponseType(consumeMethod),
                new TimeoutException(
                    $"The request timed out after {request.TimeOut?.TotalMilliseconds:0}ms before a consumer could start on it."));
        }

        // Cancelled by the sender's timeout, or by the delivery token when the channel or the mediator goes away.
        using var consumeCancellation = CancellationTokenSource.CreateLinkedTokenSource(deliveryCancellationToken);
        if (remainingTime != Timeout.InfiniteTimeSpan)
            consumeCancellation.CancelAfter(remainingTime);

        Response response;
        try
        {
            var runningTask = ConsumerInvoker.Invoke(consumer, consumeMethod, request, consumeCancellation.Token);
            await runningTask;
            response = ConsumerInvoker.GetResponse(runningTask);
            response.Success = true;
        }
        catch (Exception ex)
        {
            response = (Response)Activator.CreateInstance(ConsumerInvoker.GetResponseType(consumeMethod))!;
            response.Success = false;
            if (ex is TargetInvocationException && ex.InnerException is not null)
                ex = ex.InnerException; // don't return the invocation exception. return the actual exception within the worker.
            if (ex is OperationCanceledException && consumeCancellation.IsCancellationRequested &&
                !deliveryCancellationToken.IsCancellationRequested)
                ex = new TimeoutException(
                    $"The consumer was cancelled after the request timeout of {request.TimeOut?.TotalMilliseconds:0}ms elapsed.");
            response.ExceptionData = ExceptionData.FromException(ex);
            _logger?.LogError(ex, "Error in ConsumerInvoke");
            Activity.Current?.SetStatus(ActivityStatusCode.Error);
        }

        await SendResponse(response, request, mediator);
        return true;
    }

    /// <summary>
    /// What is left of the sender's timeout, measured from the moment this process was handed the message.
    /// The timeout travels as a duration, so this is one process' clock throughout and cannot be thrown off by
    /// hosts disagreeing about the time. <see cref="Timeout.InfiniteTimeSpan"/> when the sender sent none.
    /// </summary>
    private static TimeSpan RemainingTime(IRequest request, long deliveredAt) =>
        request.TimeOut is { } timeOut
            ? timeOut - Stopwatch.GetElapsedTime(deliveredAt)
            : Timeout.InfiniteTimeSpan;

    private async Task<bool> RespondWithFailure(IRequest request, RabbitMediator mediator, Type responseType,
        Exception reason)
    {
        var response = (Response)Activator.CreateInstance(responseType)!;
        response.Success = false;
        response.ExceptionData = ExceptionData.FromException(reason);
        await SendResponse(response, request, mediator);
        return true;
    }

    /// <summary>
    /// Answers a request nobody consumes here, so the requester learns why instead of running into its
    /// timeout. The response type comes from the request's own <see cref="Request{TResponse}"/> base.
    /// </summary>
    private async Task<bool> RespondWithoutConsumer(IRequest request, RabbitMediator mediator)
    {
        var responseType = GetResponseTypeOfRequest(request.GetType());
        if (responseType == null)
        {
            _logger?.LogError("Cannot determine the response type of {RequestType}, dropping the request",
                request.GetType().FullName);
            return false;
        }

        return await RespondWithFailure(request, mediator, responseType, new InvalidOperationException(
            $"No consumer for requests of type {request.GetType().FullName} is registered on the target instance."));
    }

    /// <summary>The TResponse of the <see cref="Request{TResponse}"/> the request type derives from.</summary>
    private static Type? GetResponseTypeOfRequest(Type requestType)
    {
        for (var type = requestType; type != null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Request<>))
                return type.GetGenericArguments()[0];
        }

        return null;
    }

    /// <summary>Addresses a response at the requester's response queue and publishes it.</summary>
    private async Task SendResponse(Response response, IRequest request, RabbitMediator mediator)
    {
        response.CorrelationId = request.CorrelationId;
        response.TelemetryTraceParent = Activity.Current?.Id;
        response.TelemetryTraceState = Activity.Current?.TraceStateString;
        response.SenderInstance = new InstanceInformation
        {
            InstanceScope = mediator.ScopeId,
            InstanceId = this.InstanceId,
        };
        response.TargetInstance = request.SenderInstance;

        var targetQueue = RabbitNaming.InputQueue(RabbitNaming.InputQueuePrefixResponse,
            RabbitNaming.TypeName(response.GetType()), response.TargetInstance.InstanceId,
            response.TargetInstance.InstanceScope);
        await _serializerHelper.Serialize(response,
            async data =>
            {
                var responseChannel = await _sendResponseChannel!.GetAsync();
                await responseChannel.BasicPublishAsync(string.Empty, targetQueue, data);
            });
    }
}
