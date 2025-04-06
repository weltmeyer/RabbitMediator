using Weltmeyer.RabbitMediator.MessageBases;

namespace Weltmeyer.RabbitMediator.ConsumerBases;

public interface IRequestConsumer<in TRequest, TResponse> : IConsumer
    where TResponse : Response
    where TRequest : Request<TResponse>
{
    public Task<TResponse> Consume(TRequest message);
}