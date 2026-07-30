using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Weltmeyer.RabbitMediator.Contracts.Contracts;
using Weltmeyer.RabbitMediator.Contracts.MessageBases;

namespace Weltmeyer.RabbitMediator;

/// <summary>The outgoing side: publishing requests and messages and awaiting their response / ack.</summary>
internal partial class RabbitMediatorMultiplexer
{
    internal async Task<TResponse> Request<TResponse>(RabbitMediator rabbitMediator,
        Request<TResponse> request, TimeSpan? responseTimeOut, bool throwOnFailure,
        CancellationToken cancellationToken = default)
        where TResponse : Response
    {
        using var activity = Telemetry.ActivitySource.StartActivity(ActivityKind.Producer);
        if (rabbitMediator.Disposed)
            throw new ObjectDisposedException(nameof(IRabbitMediator));

        cancellationToken.ThrowIfCancellationRequested();
        GuardTargetIsSet(request);

        var configuration = GetConfiguration(rabbitMediator);

        await EnsureReceiver(rabbitMediator, typeof(TResponse), cancellationToken);

        var typeName = RabbitNaming.TypeName(request.GetType());
        var routingKey = RoutingKeyFor(request);
        var exchangeName = ExchangeNameFor(request, typeName);

        request.SenderInstance = new InstanceInformation
        {
            InstanceId = this.InstanceId,
            InstanceScope = rabbitMediator.ScopeId,
        };
        request.CorrelationId = Guid.NewGuid();
        request.TelemetryTraceParent = Activity.Current?.Id;
        request.TelemetryTraceState = Activity.Current?.TraceStateString;

        var awaiter = new RequestResponseAwaiter(request.CorrelationId, rabbitMediator);
        _responseWaiters.TryAdd(awaiter.CorrelationId, awaiter);

        var useTimeout = responseTimeOut ?? configuration.Configuration.DefaultResponseTimeOut;
        request.TimeOut = useTimeout;
        try
        {
            await EnsureSendExchange(exchangeName, ExchangeTypeFor(request), cancellationToken);
            activity?.Enrich(request);
            await _serializerHelper.Serialize(request, async data =>
            {
                var props = new BasicProperties
                {
                    Expiration = Convert.ToInt64(useTimeout.TotalMilliseconds).ToString()
                };

                var requestChannel = await _sendRequestChannel!.GetAsync(cancellationToken);
                await requestChannel.BasicPublishAsync(exchangeName, routingKey, true, props, data,
                    cancellationToken);
            });
        }
        catch (RabbitMQClientException rabbitException)
        {
            _logger?.LogWarning(rabbitException, "Publishing failed");
            _responseWaiters.TryRemove(awaiter.CorrelationId, out _);
            if (throwOnFailure)
                throw new RabbitMediatorSendFailureException(request.GetType(), request.CorrelationId, rabbitException);
            return CreateFailureResponse<TResponse>(rabbitMediator, request.CorrelationId, timedOut: false,
                sendFailure: true);
        }

        var timeOutTask = Task.Delay(useTimeout, cancellationToken);
        var responseWaitTask = awaiter.TaskCompletionSource.Task;
        var waitResult = await Task.WhenAny(timeOutTask, responseWaitTask);
        if (cancellationToken.IsCancellationRequested && waitResult != responseWaitTask)
        {
            // The caller gave up on us. Both Request and TryRequest surface that as the cancellation it is -
            // TryRequest only promises not to throw for a timeout or a failed publish.
            _responseWaiters.TryRemove(awaiter.CorrelationId, out _);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (waitResult == responseWaitTask)
        {
            await responseWaitTask;
            Debug.Assert(awaiter.Result != null);
            var response = (TResponse?)awaiter.Result;
            return response ?? throw new InvalidCastException();
        }

        _logger?.LogWarning("Timed out waiting for response: Timeout: {Timeout}ms, Message: {Message}",
            useTimeout.TotalMilliseconds, request);

        _responseWaiters.TryRemove(awaiter.CorrelationId, out _);
        if (throwOnFailure)
            throw new RabbitMediatorTimeoutException(request.GetType(), request.CorrelationId, useTimeout);
        return CreateFailureResponse<TResponse>(rabbitMediator, request.CorrelationId, timedOut: true,
            sendFailure: false);
    }

    internal async Task<SendResult> Send<TMessageType>(RabbitMediator rabbitMediator,
        TMessageType message, bool confirmPublish, TimeSpan? confirmTimeOut,
        CancellationToken cancellationToken = default)
        where TMessageType : Message
    {
        using var activity = Telemetry.ActivitySource.StartActivity(ActivityKind.Producer);
        if (rabbitMediator.Disposed)
            throw new ObjectDisposedException(nameof(IRabbitMediator));

        cancellationToken.ThrowIfCancellationRequested();
        GuardTargetIsSet(message);

        // The runtime type decides, not TMessageType: a message handed over through a base-typed variable
        // would otherwise be published to the exchange of that base type, where nothing is bound.
        var typeName = RabbitNaming.TypeName(message.GetType());
        var routingKey = RoutingKeyFor(message);
        var exchangeName = ExchangeNameFor(message, typeName);

        message.SenderInstance = new InstanceInformation
        {
            InstanceId = InstanceId,
            InstanceScope = rabbitMediator.ScopeId,
        };
        message.CorrelationId = Guid.NewGuid();
        message.RequireAck = confirmPublish;
        message.TelemetryTraceParent = Activity.Current?.Id;
        message.TelemetryTraceState = Activity.Current?.TraceStateString;

        var props = new BasicProperties();
        try
        {
            TargetAckAwaiter? targetAckAwaiter = null;
            if (confirmPublish)
            {
                targetAckAwaiter = new TargetAckAwaiter(message.CorrelationId, rabbitMediator);
                _targetAckWaiters.TryAdd(targetAckAwaiter.CorrelationId, targetAckAwaiter);
            }

            await EnsureSendExchange(exchangeName, ExchangeTypeFor(message), cancellationToken);
            activity?.Enrich(message);
            await _serializerHelper.Serialize(message, async data =>
            {
                var messageChannel = await _sendMessageChannel!.GetAsync(cancellationToken);
                await messageChannel.BasicPublishAsync(exchangeName, routingKey, confirmPublish, props, data,
                    cancellationToken);
            });

            if (confirmPublish)
            {
                var configuration = GetConfiguration(rabbitMediator);
                var timeOutTask = Task.Delay(confirmTimeOut ?? configuration.Configuration.DefaultConfirmTimeOut,
                    cancellationToken);
                var ackMsgTask = targetAckAwaiter!.TaskCompletionSource.Task;
                var waitResult = await Task.WhenAny(timeOutTask, ackMsgTask);
                if (cancellationToken.IsCancellationRequested && waitResult != ackMsgTask)
                {
                    _targetAckWaiters.TryRemove(message.CorrelationId, out _);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (waitResult == ackMsgTask)
                {
                    var ackMsg = await ackMsgTask;
                    return new SendResult { Success = ackMsg.Success, ExceptionData = ackMsg.ExceptionData };
                }

                //timed out
                return new SendResult { Success = false, TimedOut = true };
            }
        }
        // Same breadth as Request: a PublishException means unroutable, but a channel that is closed or gone
        // is just as much a send failure and used to escape from here as a raw exception.
        catch (RabbitMQClientException ex)
        {
            _logger?.LogWarning(ex, "Publishing failed");
            if (confirmPublish)
                _targetAckWaiters.TryRemove(message.CorrelationId, out _);
            return new SendResult { Success = false, SendFailure = true };
        }

        return new SendResult { Success = true };
    }

    private static void GuardTargetIsSet(ISentObject sentObject)
    {
        if (sentObject is ITargetedSentObject targetedMessage &&
            (targetedMessage.TargetInstance.InstanceId == string.Empty ||
             targetedMessage.TargetInstance.InstanceScope == string.Empty))
            throw new InvalidOperationException("TargetId not set!");
    }

    private static string RoutingKeyFor(ISentObject sentObject) => sentObject switch
    {
        IBroadCastSentObject => RabbitNaming.BroadcastRoutingKey,
        IAnyTargetedSentObject => RabbitNaming.AnyTargetedRoutingKey,
        ITargetedSentObject tm => RabbitNaming.InstanceRoutingKey(tm.TargetInstance.InstanceId,
            tm.TargetInstance.InstanceScope),
        _ => throw new ArgumentException("Invalid message type")
    };

    private static string ExchangeTypeFor(ISentObject sentObject) => sentObject switch
    {
        IBroadCastSentObject => ExchangeType.Fanout,
        _ => ExchangeType.Direct
    };

    /// <summary>
    /// Declares the exchange we are about to publish to, with the same arguments the receiving side uses.
    /// Publishing to an exchange that does not exist is a channel-level error: the broker closes the channel
    /// and the client does not bring it back, so one message of a type nobody consumes anywhere used to break
    /// every later publish on that connection. Declaring first turns that case into a plain unroutable
    /// message, which is what SendFailure is for.
    /// </summary>
    private async Task EnsureSendExchange(string exchangeName, string exchangeType,
        CancellationToken cancellationToken)
    {
        if (_declaredSendExchanges.ContainsKey(exchangeName))
            return;

        var topologyChannel = await _topologyChannel!.GetAsync(cancellationToken);
        await topologyChannel.ExchangeDeclareAsync(exchangeName, exchangeType, durable: false, autoDelete: false,
            cancellationToken: cancellationToken);
        _declaredSendExchanges.TryAdd(exchangeName, true);
    }

    private static string ExchangeNameFor(ISentObject sentObject, string typeName) => sentObject switch
    {
        IBroadCastSentObject => RabbitNaming.BroadcastExchange(typeName),
        IAnyTargetedSentObject => RabbitNaming.AnyTargetedExchange(typeName),
        ITargetedSentObject => RabbitNaming.TargetedExchange(typeName),
        _ => throw new ArgumentException("Invalid message type")
    };

    /// <summary>
    /// The synthetic response the non-throwing <c>TryRequest</c> overloads hand back when no real response
    /// arrives: everything at its default apart from the correlation id and the flag saying what went wrong.
    /// </summary>
    private TResponse CreateFailureResponse<TResponse>(RabbitMediator rabbitMediator, Guid correlationId,
        bool timedOut, bool sendFailure)
        where TResponse : Response
    {
        var response = Activator.CreateInstance<TResponse>();
        response.Success = false;
        response.TimedOut = timedOut;
        response.SendFailure = sendFailure;
        response.TargetInstance = new InstanceInformation
        {
            InstanceId = this.InstanceId,
            InstanceScope = rabbitMediator.ScopeId
        };
        response.CorrelationId = correlationId;
        response.SenderInstance = InstanceInformation.Empty;
        return response;
    }
}
