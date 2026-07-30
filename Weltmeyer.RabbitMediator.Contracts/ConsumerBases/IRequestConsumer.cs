using Weltmeyer.RabbitMediator.Contracts.MessageBases;

namespace Weltmeyer.RabbitMediator.Contracts.ConsumerBases;

public interface IRequestConsumer<in TRequest, TResponse> : IConsumer
    where TResponse : Response
    where TRequest : Request<TResponse>
{
    /// <summary>
    /// Handles the request without ever learning that the sender stopped waiting.
    /// Implement <see cref="Consume(TRequest,CancellationToken)"/> instead. Implementing this one keeps
    /// working - the cancellable overload forwards to it - but the work then runs to its end even when the
    /// answer is already worthless.
    /// </summary>
    [Obsolete("Implement Consume(TRequest, CancellationToken) instead, so the work can stop when the sender's timeout elapses", false)]
    public Task<TResponse> Consume(TRequest message) =>
        throw new NotImplementedException(
            $"{GetType().FullName} implements neither Consume overload of IRequestConsumer.");

    /// <summary>
    /// Handles the request and gives up when <paramref name="cancellationToken"/> is cancelled - which happens
    /// once the sender's timeout has elapsed, or when the mediator shuts down.
    /// Cancellation is cooperative: work that never looks at the token runs to its end regardless.
    /// </summary>
    public Task<TResponse> Consume(TRequest message, CancellationToken cancellationToken) =>
#pragma warning disable CS0618 // forwarding for consumers that only implement the old overload is the point
        Consume(message);
#pragma warning restore CS0618
}
