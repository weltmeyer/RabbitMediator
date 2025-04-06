using Weltmeyer.RabbitMediator.ConsumerBases;
using Weltmeyer.RabbitMediator.MessageBases;

namespace Weltmeyer.RabbitMediator;

public interface IRabbitMediator
{
    /// <summary>
    /// The id of this instance, used to identify this instance as a specific sender and receiver of messages and requests/responses 
    /// </summary>
    public Guid InstanceId { get; }

    public TimeSpan DefaultConfirmTimeOut { get; set; }
    public TimeSpan DefaultResponseTimeOut { get; set; }

    /// <summary>
    /// Sends a request and waits for the specified response
    /// </summary>
    /// <param name="request">The actual request</param>
    /// <param name="responseTimeOut"></param>
    /// <typeparam name="TRequest"></typeparam>
    /// <typeparam name="TResponse"></typeparam>
    /// <returns></returns>
    Task<TResponse> Request<TRequest, TResponse>(TRequest request,TimeSpan? responseTimeOut=null)
        where TResponse : Response
        where TRequest : Request<TResponse>;

    /// <summary>
    /// Sends a message without awaiting any specific response.
    /// </summary>
    /// <param name="message">The message to send</param>
    /// <param name="confirmPublish">Awaits for an acknowledgment of the message of at least one receiver.</param>
    /// <param name="confirmTimeOut"></param>
    /// <typeparam name="TMessageType"></typeparam>
    /// <returns></returns>
    Task<SendResult> Send<TMessageType>(TMessageType message, bool confirmPublish = true,
        TimeSpan? confirmTimeOut = null)
        where TMessageType : Message;


    T? GetRequestConsumerInstance<T>()
        where T : IConsumer;

    T? GetMessageConsumerInstance<T>()
        where T : IConsumer;

}