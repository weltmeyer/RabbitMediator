using Weltmeyer.RabbitMediator.Contracts.MessageBases;

namespace Weltmeyer.RabbitMediator.Contracts.ConsumerBases;

public interface IMessageConsumer<in TMessageType> : IConsumer
    where TMessageType : Message
{
    public Task Consume(TMessageType message);
}