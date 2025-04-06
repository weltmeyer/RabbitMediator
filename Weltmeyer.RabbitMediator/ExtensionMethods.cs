using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Weltmeyer.RabbitMediator.ConsumerBases;

namespace Weltmeyer.RabbitMediator;

public static class ExtensionMethods
{

    public static void AddRabbitMediator(this IServiceCollection serviceCollection, Assembly consumerAssemby,
        string connectionString,
        string? keyedInstanceName = null)
    {
        AddRabbitMediator(serviceCollection, [consumerAssemby], connectionString, keyedInstanceName);
    }

    public static void AddRabbitMediator(this IServiceCollection serviceCollection, Assembly[] consumerAssemblies,
        string connectionString,
        string? keyedInstanceName = null)
    {
        var allConsumerTypes = consumerAssemblies.SelectMany(asm => asm.GetTypes())
            .Where(t => t.IsAssignableTo(typeof(IConsumer)) && !t.IsAbstract)
            .ToArray();
        AddRabbitMediator(serviceCollection, allConsumerTypes, connectionString, keyedInstanceName);
    }

    public static void AddRabbitMediator(this IServiceCollection serviceCollection, Type[] consumerTypes,
        string connectionString,
        string? keyedInstanceName = null)
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


        if (keyedInstanceName != null)
        {
            serviceCollection.AddKeyedSingleton<IRabbitMediator>(keyedInstanceName, (provider, key) =>
            {
                var result = new RabbitMediator(provider.GetRequiredService<ILogger<RabbitMediator>>(),
                    consumerTypes,
                    connectionString,
                    key as string, 50);

                return result;
            });
        }
        else
        {
            serviceCollection.AddSingleton<IRabbitMediator>(provider =>
            {
                var result = new RabbitMediator(provider.GetRequiredService<ILogger<RabbitMediator>>(),
                    consumerTypes, connectionString, null, 50);
                return result;
            });
        }
        
        serviceCollection.Configure<RabbitMediatorWorkerConfiguration>(opt =>
        {
            opt.KeyList.Add(keyedInstanceName);
        });
        
        if (serviceCollection.All(sd => sd.ServiceType != typeof(RabbitMediatorWorker)))
        {
            serviceCollection.AddHostedService<RabbitMediatorWorker>(provider =>
            {
                var instance = new RabbitMediatorWorker(provider,
                    provider.GetRequiredService<IOptions<RabbitMediatorWorkerConfiguration>>());
                return instance;
            });
        }
    }
}

internal class RabbitMediatorWorkerConfiguration
{
    public List<string?> KeyList { get; } = new();
}