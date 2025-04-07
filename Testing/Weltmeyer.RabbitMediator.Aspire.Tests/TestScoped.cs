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
    public async Task FirstTest()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddRabbitMediator(
            [typeof(TestTargetedMessageConsumer), typeof(TestTargetedRequestConsumer)],
            _aspireHostFixture.RabbitMQConnectionString!, scoped: true);

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
        var consumer2 = mediator2.GetMessageConsumerInstance<TestTargetedMessageConsumer>();
        Assert.NotNull(consumer2);
        Assert.Equal(1,consumer2.ReceivedMessages);

        await app.StopAsync();
    }
}