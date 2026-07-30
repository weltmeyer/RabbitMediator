using Microsoft.Extensions.Hosting;
using Weltmeyer.RabbitMediator.TestTool.Consumers;
using Weltmeyer.RabbitMediator.TestTool.Messages;

namespace Weltmeyer.RabbitMediator.Aspire.Tests;

/// <summary>
/// The queue behind an any-targeted type is shared by all its consumers and outlives them, so it behaves like
/// a work queue: sending while nobody consumes is not a lost message, it waits.
/// </summary>
[Collection("AspireHostCollection")]
public class AnyTargetedQueueingTests
{
    private readonly AspireHostFixture _aspireHostFixture;

    public AnyTargetedQueueingTests(AspireHostFixture aspireHostFixture)
    {
        _aspireHostFixture = aspireHostFixture;
    }

    private Task<IHost> PrepareHost(string serviceKey, bool withConsumer) =>
        _aspireHostFixture.PrepareEmptyHost(builder => builder.Services.AddRabbitMediator(cfg =>
        {
            cfg.ConnectionString = _aspireHostFixture.RabbitMQConnectionString!;
            cfg.ServiceKey = serviceKey;
            cfg.DefaultConfirmTimeOut = TimeSpan.FromSeconds(3);
            if (withConsumer)
                cfg.ConsumerTypes = [typeof(TestAnyTargetedMessageConsumer)];
        }));

    [Fact]
    public async Task SentWhileNoConsumerIsOnline_TimesOutButIsDeliveredLater()
    {
        //a consumer has to have existed once for the shared queue to be there at all
        using (var consumerApp = await PrepareHost("queueing-first", withConsumer: true))
        {
            _ = consumerApp.Services.GetRequiredKeyedService<IRabbitMediator>("queueing-first");
            await consumerApp.StopAsync();
        }

        using var senderApp = await PrepareHost("queueing-sender", withConsumer: false);
        var sender = senderApp.Services.GetRequiredKeyedService<IRabbitMediator>("queueing-sender");

        //nobody acknowledges it, but it is sitting in the shared queue
        var result = await sender.Send(new TestAnyTargetedMessage());
        Assert.False(result.Success);
        Assert.True(result.TimedOut);
        Assert.False(result.SendFailure);

        using var laterApp = await PrepareHost("queueing-later", withConsumer: true);
        var later = laterApp.Services.GetRequiredKeyedService<IRabbitMediator>("queueing-later");

        var received = 0L;
        for (var i = 0; i < 10 && received == 0; i++)
        {
            received = later.GetConsumerInstance<TestAnyTargetedMessageConsumer>()!.ReceivedMessages;
            if (received == 0)
                await Task.Delay(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(1, received);
    }
}
