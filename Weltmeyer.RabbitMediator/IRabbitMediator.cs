using Weltmeyer.RabbitMediator.Contracts.ConsumerBases;
using Weltmeyer.RabbitMediator.Contracts.Contracts;
using Weltmeyer.RabbitMediator.Contracts.MessageBases;


namespace Weltmeyer.RabbitMediator;

public interface IRabbitMediator
{
    /// <summary>
    /// The id of this instance, used to identify this instance as a specific sender and receiver of messages and requests/responses 
    /// </summary>
    public string InstanceId { get; }

    public string ScopeId { get; }

    public InstanceInformation GetInstanceInformation() => new() { InstanceId = InstanceId, InstanceScope = ScopeId };

    /// <summary>
    /// Sends a request and waits for the specified response.
    /// Throws <see cref="RabbitMediatorTimeoutException"/> if no response arrives within the timeout and
    /// <see cref="RabbitMediatorSendFailureException"/> if the request cannot be published/routed.
    /// Use <see cref="TryRequest{TResponse}"/> to receive a synthetic response instead of an exception.
    /// </summary>
    /// <param name="request">The actual request</param>
    /// <param name="responseTimeOut"></param>
    /// <typeparam name="TRequest"></typeparam>
    /// <typeparam name="TResponse"></typeparam>
    /// <returns></returns>
    [Obsolete("Use Request<TResponse>(Request<TResponse>,TimeSpan?) instead", false)]
    Task<TResponse> Request<TRequest, TResponse>(TRequest request, TimeSpan? responseTimeOut = null)
        where TResponse : Response
        where TRequest : Request<TResponse>;

    /// <summary>
    /// Sends a request and waits for the specified response.
    /// Throws <see cref="RabbitMediatorTimeoutException"/> if no response arrives within the timeout and
    /// <see cref="RabbitMediatorSendFailureException"/> if the request cannot be published/routed.
    /// Use <see cref="TryRequest{TResponse}"/> to receive a synthetic response instead of an exception.
    /// </summary>
    Task<TResponse> Request<TResponse>(Request<TResponse> request, TimeSpan? responseTimeOut = null)
        where TResponse : Response;

    /// <summary>
    /// Sends a request and waits for the specified response, returning a synthetic response instead of
    /// throwing on failure: on timeout the returned response has <see cref="Response.TimedOut"/> set, on a
    /// publish/routing failure it has <see cref="Response.SendFailure"/> set, both with
    /// <see cref="Response.Success"/> == false and all other fields at their defaults. Callers MUST inspect
    /// these flags. Prefer <see cref="Request{TResponse}"/> unless a timeout is an expected, handled outcome.
    /// </summary>
    [Obsolete("Use TryRequest<TResponse>(Request<TResponse>,TimeSpan?) instead", false)]
    Task<TResponse> TryRequest<TRequest, TResponse>(TRequest request, TimeSpan? responseTimeOut = null)
        where TResponse : Response
        where TRequest : Request<TResponse>;

    /// <summary>
    /// Sends a request and waits for the specified response, returning a synthetic response instead of
    /// throwing on failure: on timeout the returned response has <see cref="Response.TimedOut"/> set, on a
    /// publish/routing failure it has <see cref="Response.SendFailure"/> set, both with
    /// <see cref="Response.Success"/> == false and all other fields at their defaults. Callers MUST inspect
    /// these flags. Prefer <see cref="Request{TResponse}"/> unless a timeout is an expected, handled outcome.
    /// </summary>
    Task<TResponse> TryRequest<TResponse>(Request<TResponse> request, TimeSpan? responseTimeOut = null)
        where TResponse : Response;


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


    T? GetConsumerInstance<T>()
        where T : IConsumer;
}