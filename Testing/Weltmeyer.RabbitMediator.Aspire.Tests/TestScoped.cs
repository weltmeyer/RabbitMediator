using Microsoft.Extensions.Hosting;
using Weltmeyer.RabbitMediator.TestTool.Consumers;
using Weltmeyer.RabbitMediator.TestTool.Messages;

namespace Weltmeyer.RabbitMediator.Aspire.Tests;

[Collection("AspireHostCollection")]
public class TestScoped
{
    private readonly AspireHostFixture _aspireHostFixture;


    public TestScoped(AspireHostFixture aspireHostFixture)
    {
        _aspireHostFixture = aspireHostFixture;
    }

    [Fact]
    public async Task TestSimpleScoped()
    {
        var builder = Host.CreateApplicationBuilder();
        
        builder.Services.AddRabbitMediator(cfg =>
        {
            cfg.ConsumerTypes = [typeof(TestTargetedMessageConsumer)];
            cfg.ConnectionString = _aspireHostFixture.RabbitMQConnectionString!;
            cfg.ServiceLifetime = ServiceLifetime.Scoped;
        });

        var app = builder.Build();
        await app.StartAsync();

        using var scope1 = app.Services.CreateScope();
        var mediator1 = scope1.ServiceProvider.GetRequiredService<IRabbitMediator>();

        using var scope2 = app.Services.CreateScope();
        var mediator2 = scope2.ServiceProvider.GetRequiredService<IRabbitMediator>();
        var msg = new TestTargetedMessage
        {
            TargetInstance = mediator2.GetInstanceInformation()
        };
        var sendResult = await mediator1.Send(msg);
        Assert.True(sendResult.Success);
        var consumer2 = mediator2.GetConsumerInstance<TestTargetedMessageConsumer>();
        Assert.NotNull(consumer2);
        Assert.Equal(1,consumer2.ReceivedMessages);

        await app.StopAsync();
    }
    
    public async Task TestScopedKeyed()
    {
        var builder = Host.CreateApplicationBuilder();
        
        builder.Services.AddRabbitMediator(cfg =>
        {
            cfg.ConsumerTypes = [typeof(TestTargetedMessageConsumer)];
            cfg.ConnectionString = _aspireHostFixture.RabbitMQConnectionString!;
            cfg.ServiceLifetime = ServiceLifetime.Scoped;
            cfg.ServiceKey = "TheServiceKey";
        });

        var app = builder.Build();
        await app.StartAsync();

        using var scope1 = app.Services.CreateScope();
        var mediator1 = scope1.ServiceProvider.GetRequiredKeyedService<IRabbitMediator>("TheServiceKey");

        using var scope2 = app.Services.CreateScope();
        var mediator2 = scope2.ServiceProvider.GetRequiredKeyedService<IRabbitMediator>("TheServiceKey");
        var msg = new TestTargetedMessage
        {
            TargetInstance = mediator2.GetInstanceInformation()
        };
        var sendResult = await mediator1.Send(msg);
        Assert.True(sendResult.Success);
        var consumer2 = mediator2.GetConsumerInstance<TestTargetedMessageConsumer>();
        Assert.NotNull(consumer2);
        Assert.Equal(1,consumer2.ReceivedMessages);

        await app.StopAsync();
    }
}