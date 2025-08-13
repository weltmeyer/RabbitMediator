using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Weltmeyer.RabbitMediator;

public static class ExtensionMethods
{
    public static void AddRabbitMediator(this IServiceCollection serviceCollection,
        Action<RabbitMediatorConfiguration> configurationAction)
    {
        var configuration = new RabbitMediatorConfiguration();
        configurationAction(configuration);

        configuration.Validate();


        var lifeTime = configuration.ServiceLifetime;

        if (!serviceCollection.Any(sd =>
                sd.ServiceType == typeof(RabbitMediatorMultiplexer) &&
                sd.IsKeyedService == (configuration.ServiceKey != null) &&
                sd.ServiceKey == configuration.ServiceKey && sd.Lifetime == lifeTime))
        {
            var multiplexerDescriptor = new ServiceDescriptor(typeof(RabbitMediatorMultiplexer),
                configuration.ServiceKey,
                (provider, key) =>
                {
                    var result = new RabbitMediatorMultiplexer(configuration.ConnectionString,
                        logger: provider.GetRequiredService<ILogger<RabbitMediatorMultiplexer>>());
                    var workerConfiguration =
                        provider.GetRequiredService<IOptions<RabbitMediatorWorkerConfiguration>>();
                    workerConfiguration.Value.PleaseConfigureMultiplexers.Writer.TryWrite(result);
                    return result;
                }, lifeTime);

            serviceCollection.Add(multiplexerDescriptor);
        }


        var instanceDescriptor = new ServiceDescriptor(typeof(IRabbitMediator), configuration.ServiceKey,
            (provider, key) =>
            {
                var multiplexer = key == null
                    ? provider.GetRequiredService<RabbitMediatorMultiplexer>()
                    : provider.GetRequiredKeyedService<RabbitMediatorMultiplexer>(key);

                var newMediator = multiplexer.CreateRabbitMediator(provider,configuration);
                var workerConfiguration =
                    provider.GetRequiredService<IOptions<RabbitMediatorWorkerConfiguration>>();
                workerConfiguration.Value.PleaseConfigureMediators.Writer.TryWrite(newMediator);
                Task.Run(() => newMediator.EnsureConfigured()); //how to start a service asynchronously?
                if (!newMediator.WaitReady(configuration.WaitReadyTimeOut))
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
            serviceCollection.Configure<RabbitMediatorWorkerConfiguration>(opt => { });
            serviceCollection.AddHostedService<RabbitMediatorWorker>();
        }
    }

    public static void AddRabbitMediatorTelemetry(this IServiceCollection serviceCollection)
    {
        serviceCollection.AddOpenTelemetry().WithTracing(t => t.AddSource(Telemetry.ActivitySource.Name));
    }
}

internal class RabbitMediatorWorkerConfiguration
{
    public readonly Channel<RabbitMediatorMultiplexer> PleaseConfigureMultiplexers =
        Channel.CreateUnbounded<RabbitMediatorMultiplexer>();

    public readonly Channel<RabbitMediator> PleaseConfigureMediators =
        Channel.CreateUnbounded<RabbitMediator>();
}