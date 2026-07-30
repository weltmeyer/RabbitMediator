using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Weltmeyer.RabbitMediator.Contracts.ConsumerBases;

namespace Weltmeyer.RabbitMediator;

public class RabbitMediatorConfiguration
{
    public List<Type> ConsumerTypes = new();
    public TimeSpan DefaultConfirmTimeOut { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan DefaultResponseTimeOut { get; set; } = TimeSpan.FromSeconds(10);

    public List<Assembly> ConsumerAssemblies = new();

    public ServiceLifetime ServiceLifetime { get; set; } = ServiceLifetime.Singleton;

    public string ConnectionString { get; set; } = null!;

    public object? ServiceKey { get; set; } = null;

    /// <summary>
    /// How many received messages one receive channel dispatches to consumers in parallel.
    /// Applies to the connection this mediator lives on, so the first mediator registered for a given
    /// connection (service key) decides it for the ones sharing that connection.
    /// </summary>
    public ushort ConsumerDispatchConcurrency { get; set; } = 10;

    /// <summary>
    /// How many unacknowledged messages the broker hands out per receive channel before waiting. Bounds how
    /// much of a queue a burst can pull into memory. 0 means unlimited, which is what the client does by
    /// default. Same connection-wide scope as <see cref="ConsumerDispatchConcurrency"/>.
    /// </summary>
    public ushort PrefetchCount { get; set; } = 100;

#if DEBUG
    public TimeSpan WaitReadyTimeOut { get; set; } = TimeSpan.FromSeconds(100);
#else
    public TimeSpan WaitReadyTimeOut { get; set; } = TimeSpan.FromSeconds(10);
#endif

    public Type[] GetAllConsumerTypes()
    {
        var consumerTypesFromAssemblies = ConsumerAssemblies.SelectMany(asm => asm.GetTypes())
            .Where(t => t.IsAssignableTo(typeof(IConsumer)) && !t.IsAbstract)
            .Distinct()
            .ToArray();

        var consumerTypes = ConsumerTypes.ToList(); //cloning to avoid configuration mod
        consumerTypes.AddRange(consumerTypesFromAssemblies.Except(consumerTypes));

        var allConsumerTypes = consumerTypes
            .Where(t => t.IsAssignableTo(typeof(IConsumer)) && !t.IsAbstract)
            .ToArray();

        var missingTypes = consumerTypes.Except(allConsumerTypes).ToArray();
        if (missingTypes.Length > 0)
        {
            throw new ArgumentException(
                $"These types are no consumers: {string.Join(",", missingTypes.Select(mt => mt.FullName))}",
                nameof(ConsumerTypes));
        }

        return allConsumerTypes;
    }


    /// <summary>
    /// Validates the Configuration and throws an ArgumentException if it is not valid.
    /// </summary>
    /// <exception cref="ArgumentException"></exception>
    public void Validate()
    {
        if (!new[] { ServiceLifetime.Singleton, ServiceLifetime.Scoped }.Contains(ServiceLifetime))
            throw new ArgumentException("ServiceLifetime is not valid", nameof(ServiceLifetime));

        if (ConsumerDispatchConcurrency == 0)
            throw new ArgumentException("ConsumerDispatchConcurrency must be at least 1",
                nameof(ConsumerDispatchConcurrency));

        if (PrefetchCount != 0 && PrefetchCount < ConsumerDispatchConcurrency)
            throw new ArgumentException(
                "PrefetchCount must be 0 (unlimited) or at least as large as ConsumerDispatchConcurrency, " +
                "otherwise the dispatchers starve", nameof(PrefetchCount));

        var registeredSentObjectTypes = new HashSet<Type>();

        var iMessageConsumerType = typeof(IMessageConsumer<>);
        var iRequestConsumerType = typeof(IRequestConsumer<,>);
        foreach (var consumerType in this.GetAllConsumerTypes())
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
                    throw new ArgumentException("Only one consumer per sentobject type is allowed!");
            }

            foreach (var requestConsumerInterface in requestConsumerInterfaces)
            {
                var requestType = requestConsumerInterface.GetGenericArguments()[0];
                if (!registeredSentObjectTypes.Add(requestType))
                    throw new ArgumentException("Only one consumer per sentobject type is allowed!");
            }
        }
    }
}