using Aspire.Hosting;
using Microsoft.Extensions.Hosting;
using Projects;
using Weltmeyer.RabbitMediator.TestTool.Consumers;

namespace Weltmeyer.RabbitMediator.Aspire.Tests;


[CollectionDefinition("AspireHostCollection")]
public class AspireHostCollection : ICollectionFixture<AspireHostFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}
public class AspireHostFixture : IDisposable, IAsyncLifetime
{

    public DistributedApplication AspireAppHost { get; private set; } = null!;

    private string?[] _mediatorKeys = null!;
    public string?[] MediatorKeys => (string?[])_mediatorKeys.Clone();

    public void Dispose()
    {
        // TODO release managed resources here
    }

    public string? RabbitMQConnectionString;

    public async Task InitializeAsync()
    {
        var appHostBuilder = await DistributedApplicationTestingBuilder.CreateAsync<Weltmeyer_RabbitMediator_Aspire_AppHost>();
        AspireAppHost = await appHostBuilder.BuildAsync();
        var resourceNotificationService = AspireAppHost.Services.GetRequiredService<ResourceNotificationService>();
        Console.WriteLine("Wait for rabbitmq...");
        await resourceNotificationService.WaitForResourceAsync("rabbitmq");
        await resourceNotificationService.WaitForResourceHealthyAsync("rabbitmq");
        Console.WriteLine("Have Rabbitmq");

        _mediatorKeys = new string[11];//11 seems odd enough to see problems
        for (int i = 0; i < _mediatorKeys.Length; i++)
        {
            var mediatorKey = $"med#{i}";
            if (i == _mediatorKeys.Length - 1)
                mediatorKey = null; //also test for unkeyed
            _mediatorKeys[i] = mediatorKey;
        }
        
        RabbitMQConnectionString = await AspireAppHost.GetConnectionStringAsync("rabbitmq");
    }
    
    public async Task<IHost> PrepareHost()
    {
        
        return await PrepareEmptyHost((builder) =>
        {
            for (int i = 0; i < MediatorKeys.Length; i++)
            {
                var mediatorKey = MediatorKeys[i];
                builder.Services.AddRabbitMediator(typeof(TestTargetedMessageConsumer).Assembly, RabbitMQConnectionString!,
                    mediatorKey);
            }
        });
    }

    public async Task<IHost> PrepareEmptyHost(Action<IHostApplicationBuilder> builderAction)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builderAction(builder);
        var testApp = builder.Build();
        await testApp.StartAsync();
        Console.WriteLine("TestApp started");
        return testApp;
    }

    public async Task DisposeAsync()
    {
        await AspireAppHost.StopAsync();
        await AspireAppHost.DisposeAsync();
    }
}