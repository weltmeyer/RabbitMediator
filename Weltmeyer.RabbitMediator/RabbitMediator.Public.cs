using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Weltmeyer.RabbitMediator.ConsumerBases;
using Weltmeyer.RabbitMediator.MessageBases;

namespace Weltmeyer.RabbitMediator;

public partial class RabbitMediator : IRabbitMediator
{
    public TimeSpan DefaultConfirmTimeOut { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan DefaultResponseTimeOut { get; set; } = TimeSpan.FromSeconds(10);


    public async Task<TResponse> Request<TRequest, TResponse>(TRequest request, TimeSpan? responseTimeOut = null)
        where TResponse : Response
        where TRequest : Request<TResponse>
    {
        responseTimeOut ??= DefaultResponseTimeOut;
        Debug.Assert(_configureBusDoneEvent.IsSet);
        
        {
            if (request is ITargetedSentObject targetedMessage && targetedMessage.TargetId == Guid.Empty)
                throw new InvalidOperationException("TargetId not set!");
        }
        await EnsureReceiver<TResponse>();
        var routingKey = request switch
        {
            //IBroadCastSentObject => BroadcastRoutingKey,
            IAnyTargetedSentObject => SharedRoutingKey,
            ITargetedSentObject tm => tm.TargetId.ToString(),
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

        var awaiter = new RequestResponseAwaiter();
        request.SenderId = InstanceId;
        request.RequestId = awaiter.RequestId;
        _responseWaiters.TryAdd(awaiter.RequestId, awaiter);
        

        try
        {
            await _serializerHelper.Serialize(request, async data =>
            {
                await _sendRequestChannel!.BasicPublishAsync(exchangeName, routingKey, true,
                    data);
            });
        }
        catch (RabbitMQClientException rabbitException)
        {
            _logger?.LogWarning(rabbitException, "Publishing failed");
            _responseWaiters.TryRemove(awaiter.RequestId, out _);
            var publishErrorResponse = Activator.CreateInstance<TResponse>();
            publishErrorResponse.Success = false;
            publishErrorResponse.RequestId = request.RequestId;
            publishErrorResponse.SentId = request.SentId;
            publishErrorResponse.SendFailure = true;
            publishErrorResponse.TargetId = this.InstanceId;
            {
                if (request is ITargetedSentObject targetedMessage)
                    publishErrorResponse.SenderId = targetedMessage.TargetId;
            }
            return publishErrorResponse;
        }
       

        var timeOutTask = Task.Delay(responseTimeOut.Value);
        var responseWaitTask = awaiter.TaskCompletionSource.Task;
        var waitResult = await Task.WhenAny(timeOutTask, responseWaitTask);
        if (waitResult == responseWaitTask)
        {
            await responseWaitTask;
            Debug.Assert(awaiter.Result != null);
            var response = (TResponse?)awaiter.Result;
            return response ?? throw new InvalidCastException();
        }

        _responseWaiters.TryRemove(awaiter.RequestId, out _);
        //timed out
        var timedOutResponse = Activator.CreateInstance<TResponse>();
        timedOutResponse.Success = false;
        timedOutResponse.RequestId = request.RequestId;
        timedOutResponse.SentId = request.SentId;
        timedOutResponse.TimedOut = true;
        timedOutResponse.TargetId = this.InstanceId;
        {
            if (request is ITargetedSentObject targetedMessage)
                timedOutResponse.SenderId = targetedMessage.TargetId;
        }
        return timedOutResponse;
    }

    public async Task<SendResult> Send<TMessageType>(TMessageType message, bool confirmPublish = true,
        TimeSpan? confirmTimeOut = null)
        where TMessageType : Message
    {
        Debug.Assert(_configureBusDoneEvent.IsSet);
        await EnsureReceiver<TMessageType>();

        confirmTimeOut ??= DefaultConfirmTimeOut;

        if (message is ITargetedSentObject targetedMessage && targetedMessage.TargetId == Guid.Empty)
            throw new InvalidOperationException("TargetId not set!");


        var routingKey = message switch
        {
            IBroadCastSentObject => BroadcastRoutingKey,
            IAnyTargetedSentObject => SharedRoutingKey,
            ITargetedSentObject tm => tm.TargetId.ToString(),
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
        message.SenderId = InstanceId;
        message.SentId = Guid.NewGuid();
        message.RequireAck = confirmPublish;


        var props = new BasicProperties
        {
            //CorrelationId = message.SentId.ToString(),
        };
        try
        {
            TargetAckAwaiter? targetAckAwaiter = null;
            if (confirmPublish)
            {
                targetAckAwaiter = new TargetAckAwaiter
                {
                    SentId = message.SentId,
                };
                _targetAckWaiters.TryAdd(targetAckAwaiter.SentId, targetAckAwaiter);
            }

            await _serializerHelper.Serialize(message, async data =>
            {
                await _sendMessageChannel!.BasicPublishAsync(exchangeName, routingKey, confirmPublish, props,
                    data);
            });


            if (confirmPublish)
            {
                var timeOutTask = Task.Delay(confirmTimeOut.Value);
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
                _targetAckWaiters.TryRemove(message.SentId, out _);
            return new SendResult { Success = false, SendFailure = true };
        }


        return new SendResult { Success = true };
    }




    public T? GetRequestConsumerInstance<T>()
        where T : IConsumer
    {
        var consumer = this._requestConsumers.Values.FirstOrDefault(k => k.GetType() == typeof(T));
        return (T?)consumer;
    }

    public T? GetMessageConsumerInstance<T>()
        where T : IConsumer
    {
        var consumer = this._messageConsumers.Values.FirstOrDefault(k => k.GetType() == typeof(T));
        return (T?)consumer;
    }
}