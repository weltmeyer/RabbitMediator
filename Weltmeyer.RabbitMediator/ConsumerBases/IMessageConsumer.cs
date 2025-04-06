using Weltmeyer.RabbitMediator.MessageBases;

namespace Weltmeyer.RabbitMediator.ConsumerBases;

public interface IMessageConsumer<in TMessageType> : IConsumer
    where TMessageType : Message
{
    public Task Consume(TMessageType message);
}