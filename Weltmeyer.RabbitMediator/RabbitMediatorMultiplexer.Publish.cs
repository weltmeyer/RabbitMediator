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
        Request<TResponse> request, TimeSpan? responseTimeOut, bool throwOnFailure)
        where TResponse : Response
    {
        using var activity = Telemetry.ActivitySource.StartActivity(ActivityKind.Producer);
        if (rabbitMediator.Disposed)
            throw new ObjectDisposedException(nameof(IRabbitMediator));

        GuardTargetIsSet(request);

        var configuration = GetConfiguration(rabbitMediator);

        await EnsureReceiver(rabbitMediator, typeof(TResponse));

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
        try
        {
            activity?.Enrich(request);
            await _serializerHelper.Serialize(request, async data =>
            {
                var props = new BasicProperties
                {
                    Expiration = Convert.ToInt64(useTimeout.TotalMilliseconds).ToString()
                };

                await _sendRequestChannel!.BasicPublishAsync(exchangeName, routingKey, true, props, data);
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

        var timeOutTask = Task.Delay(useTimeout);
        var responseWaitTask = awaiter.TaskCompletionSource.Task;
        var waitResult = await Task.WhenAny(timeOutTask, responseWaitTask);
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
        TMessageType message, bool confirmPublish, TimeSpan? confirmTimeOut)
        where TMessageType : Message
    {
        using var activity = Telemetry.ActivitySource.StartActivity(ActivityKind.Producer);
        if (rabbitMediator.Disposed)
            throw new ObjectDisposedException(nameof(IRabbitMediator));

        GuardTargetIsSet(message);

        var typeName = RabbitNaming.TypeName(typeof(TMessageType));
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

            activity?.Enrich(message);
            await _serializerHelper.Serialize(message, async data =>
            {
                await _sendMessageChannel!.BasicPublishAsync(exchangeName, routingKey, confirmPublish, props, data);
            });

            if (confirmPublish)
            {
                var configuration = GetConfiguration(rabbitMediator);
                var timeOutTask = Task.Delay(confirmTimeOut ?? configuration.Configuration.DefaultConfirmTimeOut);
                var ackMsgTask = targetAckAwaiter!.TaskCompletionSource.Task;
                var waitResult = await Task.WhenAny(timeOutTask, ackMsgTask);
                if (waitResult == ackMsgTask)
                {
                    var ackMsg = await ackMsgTask;
                    return new SendResult { Success = ackMsg.Success, ExceptionData = ackMsg.ExceptionData };
                }

                //timed out
                return new SendResult { Success = false, TimedOut = true };
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
