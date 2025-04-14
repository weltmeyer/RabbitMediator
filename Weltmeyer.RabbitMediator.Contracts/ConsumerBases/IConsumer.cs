namespace Weltmeyer.RabbitMediator.Contracts.ConsumerBases;

/// <summary>
/// Don't implement this interface
/// use  <see cref="IMessageConsumer{TMessageType}" >IMessageConsumer</see> or <see cref="IRequestConsumer{TRequest,TResponse}" >IRequestConsumer</see> instead
/// </summary>
public interface IConsumer
{
    
}