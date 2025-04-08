using Weltmeyer.RabbitMediator.ConsumerBases;

namespace Weltmeyer.RabbitMediator;

public class RabbitMediatorConfiguration
{
    public required Type[] ConsumerTypes;
    public TimeSpan DefaultConfirmTimeOut { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan DefaultResponseTimeOut { get; set; } = TimeSpan.FromSeconds(10);

    public void Validate()
    {
        var registeredSentObjectTypes = new HashSet<Type>();

        var iMessageConsumerType = typeof(IMessageConsumer<>);
        var iRequestConsumerType = typeof(IRequestConsumer<,>);
        foreach (var consumerType in this.ConsumerTypes)
        {
            var interfaces = consumerType.GetInterfaces();
            var messageConsumerInterfaces = interfaces.Where(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == iMessageConsumerType).ToArray();
            var requestConsumerInterfaces = interfaces.Where(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == iRequestConsumerType).ToArray();

            foreach (var messageConsumerInterface in messageConsumerInterfaces)
            {
                var messageType = messageConsumerInterface.GetGenericArguments()[0];
                if (!registeredSentObjectTypes.Add(messageType))
                    throw new InvalidOperationException("Only one consumer per sentobject type is allowed!");
            }

            foreach (var requestConsumerInterface in requestConsumerInterfaces)
            {
                var requestType = requestConsumerInterface.GetGenericArguments()[0];
                if (!registeredSentObjectTypes.Add(requestType))
                    throw new InvalidOperationException("Only one consumer per sentobject type is allowed!");
            }
        }
    }
}