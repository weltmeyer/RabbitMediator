using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Weltmeyer.RabbitMediator.ConsumerBases;

namespace Weltmeyer.RabbitMediator;

public static class ExtensionMethods
{
    public static void AddRabbitMediator(this IServiceCollection serviceCollection, Assembly consumerAssemby,
        string connectionString,
        object? instanceKey = null, bool scoped = false)
    {
        AddRabbitMediator(serviceCollection, [consumerAssemby], connectionString, instanceKey, scoped);
    }

    public static void AddRabbitMediator(this IServiceCollection serviceCollection, Assembly[] consumerAssemblies,
        string connectionString,
        object? instanceKey = null, bool scoped = false)
    {
        var allConsumerTypes = consumerAssemblies.SelectMany(asm => asm.GetTypes())
            .Where(t => t.IsAssignableTo(typeof(IConsumer)) && !t.IsAbstract)
            .ToArray();
        AddRabbitMediator(serviceCollection, allConsumerTypes, connectionString, instanceKey, scoped);
    }


    public static void AddRabbitMediator(this IServiceCollection serviceCollection,
        Type[] consumerTypes, string connectionString, object? instanceKey = null, bool scoped = false)
    {
        var allConsumerTypes = consumerTypes
            .Where(t => t.IsAssignableTo(typeof(IConsumer)) && !t.IsAbstract)
            .ToArray();

        var missingTypes = consumerTypes.Except(allConsumerTypes).ToArray();
        if (missingTypes.Length > 0)
        {
            throw new InvalidCastException(
                $"These types are no consumers: {string.Join(",", missingTypes.Select(mt => mt.FullName))}");
        }

        serviceCollection.Configure<RabbitMediatorWorkerConfiguration>(opt => { });

        var lifeTime = scoped ? ServiceLifetime.Scoped : ServiceLifetime.Singleton;

        if (!serviceCollection.Any(sd =>
                sd.ServiceType == typeof(RabbitMediatorMultiplexer) && sd.IsKeyedService == (instanceKey != null) &&
                sd.ServiceKey == instanceKey && sd.Lifetime == lifeTime))
        {
            var multiplexerDescriptor = new ServiceDescriptor(typeof(RabbitMediatorMultiplexer), instanceKey,
                (provider, key) =>
                {
                    var result = new RabbitMediatorMultiplexer(connectionString,
                        logger: provider.GetRequiredService<ILogger<RabbitMediatorMultiplexer>>());
                    var workerConfiguration =
                        provider.GetRequiredService<IOptions<RabbitMediatorWorkerConfiguration>>();
                    workerConfiguration.Value.PleaseConfigureMultiplexers.Writer.TryWrite(result);
                    return result;
                }, lifeTime);

            serviceCollection.Add(multiplexerDescriptor);
        }


        var instanceDescriptor = new ServiceDescriptor(typeof(IRabbitMediator), instanceKey,
            (provider, key) =>
            {
                var multiplexer = key == null
                    ? provider.GetRequiredService<RabbitMediatorMultiplexer>()
                    : provider.GetRequiredKeyedService<RabbitMediatorMultiplexer>(key);

                var newMediator = multiplexer.CreateRabbitMediator(provider, consumerTypes);
                var workerConfiguration =
                    provider.GetRequiredService<IOptions<RabbitMediatorWorkerConfiguration>>();
                workerConfiguration.Value.PleaseConfigureMediators.Writer.TryWrite(newMediator);
                if (!newMediator.WaitReady(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Could not created mediator within time!");
                }
                return newMediator;
            }, lifeTime);

        serviceCollection.Add(instanceDescriptor);

        AddRabbitMediatorWorker(serviceCollection);
    }


    private static void AddRabbitMediatorWorker(IServiceCollection serviceCollection)
    {
        if (serviceCollection.All(sd => sd.ServiceType != typeof(RabbitMediatorWorker)))
        {
            serviceCollection.AddHostedService<RabbitMediatorWorker>();
        }
    }
}

internal class RabbitMediatorWorkerConfiguration
{
    public readonly Channel<RabbitMediatorMultiplexer> PleaseConfigureMultiplexers =
        Channel.CreateUnbounded<RabbitMediatorMultiplexer>();

    public readonly Channel<MultiplexedRabbitMediator> PleaseConfigureMediators =
        Channel.CreateUnbounded<MultiplexedRabbitMediator>();
}