using Weltmeyer.RabbitMediator.Contracts.MessageBases;

namespace Weltmeyer.RabbitMediator.Contracts.ConsumerBases;

public interface IRequestConsumer<in TRequest, TResponse> : IConsumer
    where TResponse : Response
    where TRequest : Request<TResponse>
{
    public Task<TResponse> Consume(TRequest message);

    /// <summary>
    /// Handles the request and gives up when <paramref name="cancellationToken"/> is cancelled - which happens
    /// once the sender's timeout has elapsed, or when the mediator shuts down. Implement this overload instead
    /// of the other one to be told; the default just forwards, so consumers that do not care keep working.
    /// Cancellation is cooperative: work that never looks at the token runs to its end regardless.
    /// </summary>
    public Task<TResponse> Consume(TRequest message, CancellationToken cancellationToken) => Consume(message);
}
