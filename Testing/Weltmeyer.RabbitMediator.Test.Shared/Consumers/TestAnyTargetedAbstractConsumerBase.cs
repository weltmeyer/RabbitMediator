using Weltmeyer.RabbitMediator.Contracts.ConsumerBases;
using Weltmeyer.RabbitMediator.Contracts.MessageBases;

namespace Weltmeyer.RabbitMediator.TestTool.Consumers;

public abstract class TestAnyTargetedAbstractConsumerBase<TRequest, TResponse> : IRequestConsumer<TRequest, TResponse>
    where TRequest : Request<TResponse>
    where TResponse : Response
{
    async Task<TResponse> IRequestConsumer<TRequest, TResponse>.Consume(TRequest message)
    {
        return await Consume(message);
    }

    public abstract Task<TResponse> Consume(TRequest message);
}