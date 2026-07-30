using Microsoft.Extensions.DependencyInjection;
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

        // Two registrations under the same key used to leave the second one shadowed: the multiplexer of the
        // first was reused, so its connection string and settings won, while resolving IRabbitMediator handed
        // back whichever descriptor came last. Say so instead of picking one silently.
        if (serviceCollection.Any(sd =>
                sd.ServiceType == typeof(IRabbitMediator) &&
                sd.IsKeyedService == (configuration.ServiceKey != null) &&
                Equals(sd.IsKeyedService ? sd.ServiceKey : null, configuration.ServiceKey)))
        {
            throw new ArgumentException(
                configuration.ServiceKey == null
                    ? "A RabbitMediator without a service key is already registered. Give one of them a " +
                      $"{nameof(RabbitMediatorConfiguration.ServiceKey)} to run several mediators side by side."
                    : $"A RabbitMediator with the service key '{configuration.ServiceKey}' is already registered.",
                nameof(RabbitMediatorConfiguration.ServiceKey));
        }

        var multiplexerDescriptor = new ServiceDescriptor(typeof(RabbitMediatorMultiplexer),
            configuration.ServiceKey,
            (provider, key) =>
            {
                var result = new RabbitMediatorMultiplexer(configuration.ConnectionString,
                    consumerDispatchConcurrency: configuration.ConsumerDispatchConcurrency,
                    logger: provider.GetRequiredService<ILogger<RabbitMediatorMultiplexer>>(),
                    prefetchCount: configuration.PrefetchCount);
                var workerConfiguration =
                    provider.GetRequiredService<IOptions<RabbitMediatorWorkerConfiguration>>();
                workerConfiguration.Value.PleaseConfigureMultiplexers.Writer.TryWrite(result);
                return result;
            }, lifeTime);

        serviceCollection.Add(multiplexerDescriptor);


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

        // Record this mediator so the hosted worker can eagerly resolve (and thereby configure) it at startup.
        serviceCollection.Configure<RabbitMediatorWorkerConfiguration>(opt =>
            opt.RegisteredMediators.Add((configuration.ServiceKey, lifeTime)));

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
