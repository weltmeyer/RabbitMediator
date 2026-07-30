using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Weltmeyer.RabbitMediator.Contracts.MessageBases;
using Weltmeyer.RabbitMediator.TestTool.Consumers;
using Weltmeyer.RabbitMediator.TestTool.Messages;

namespace Weltmeyer.RabbitMediator.Aspire.Tests;

public class NoConsumerTargetedMessage : TargetedMessage;

public class NoConsumerResponse : Response;

public class NoConsumerRequest : TargetedRequest<NoConsumerResponse>;

/// <summary>
/// What happens to a message or request whose type nobody consumes anywhere - so not even the exchange for it
/// exists yet. Publishing to a missing exchange is a channel-level error, and the broker answers it by closing
/// the channel for good: one such message used to leave every later publish of every mediator on that
/// connection failing, silently, forever.
/// </summary>
[Collection("AspireHostCollection")]
public class NoConsumerTests
{
    private readonly AspireHostFixture _aspireHostFixture;

    public NoConsumerTests(AspireHostFixture aspireHostFixture)
    {
        _aspireHostFixture = aspireHostFixture;
    }

    private async Task<IRabbitMediator> PrepareMediator(IHost host) =>
        await Task.FromResult(host.Services.GetRequiredKeyedService<IRabbitMediator>("noconsumer"));

    private Task<IHost> PrepareHost() => _aspireHostFixture.PrepareEmptyHost(builder =>
    {
        builder.Services.AddRabbitMediator(cfg =>
        {
            cfg.ConnectionString = _aspireHostFixture.RabbitMQConnectionString!;
            cfg.ConsumerTypes = [typeof(TestTargetedMessageConsumer), typeof(TestTargetedRequestConsumer)];
            cfg.ServiceKey = "noconsumer";
            cfg.DefaultConfirmTimeOut = TimeSpan.FromSeconds(3);
            cfg.DefaultResponseTimeOut = TimeSpan.FromSeconds(3);
        });
    });

    [Fact]
    public async Task MessageWithoutConsumer_ReportsSendFailureAndLeavesTheChannelUsable()
    {
        using var testApp = await PrepareHost();
        var mediator = await PrepareMediator(testApp);
        var self = mediator.GetInstanceInformation();

        var unconsumed = await mediator.Send(new NoConsumerTargetedMessage { TargetInstance = self });
        Assert.False(unconsumed.Success);
        Assert.True(unconsumed.SendFailure);

        var afterwards = await mediator.Send(new TestTargetedMessage { TargetInstance = self });
        Assert.True(afterwards.Success);
    }

    [Fact]
    public async Task RequestWithoutConsumer_ThrowsAndLeavesTheChannelUsable()
    {
        using var testApp = await PrepareHost();
        var mediator = await PrepareMediator(testApp);
        var self = mediator.GetInstanceInformation();

        await Assert.ThrowsAsync<RabbitMediatorSendFailureException>(async () =>
            await mediator.Request(new NoConsumerRequest { TargetInstance = self }));

        var afterwards = await mediator.Request(new TestTargetedRequest { TargetInstance = self });
        Assert.True(afterwards.Success);
    }

    [Fact]
    public async Task TryRequestWithoutConsumer_ReportsSendFailure()
    {
        using var testApp = await PrepareHost();
        var mediator = await PrepareMediator(testApp);

        var response = await mediator.TryRequest(new NoConsumerRequest
        {
            TargetInstance = mediator.GetInstanceInformation(),
        });

        Assert.False(response.Success);
        Assert.True(response.SendFailure);
        Assert.False(response.TimedOut);
    }

    /// <summary>Without a publish confirmation there is nothing to report on, so an unroutable message is lost.</summary>
    [Fact]
    public async Task MessageWithoutConsumerAndWithoutConfirm_IsSilentlyDropped()
    {
        using var testApp = await PrepareHost();
        var mediator = await PrepareMediator(testApp);

        var result = await mediator.Send(new NoConsumerTargetedMessage
        {
            TargetInstance = mediator.GetInstanceInformation(),
        }, confirmPublish: false);

        Assert.True(result.Success);
    }

    /// <summary>A publish channel the broker closed has to be replaced, the client does not do it for us.</summary>
    [Fact]
    public async Task PublishChannel_ReplacesAClosedChannel()
    {
        var connectionFactory = new ConnectionFactory { Uri = new Uri(_aspireHostFixture.RabbitMQConnectionString!) };
        await using var connection = await connectionFactory.CreateConnectionAsync();

        var created = 0;
        await using var publishChannel = new PublishChannel(async () =>
        {
            created++;
            return await connection.CreateChannelAsync();
        });

        var first = await publishChannel.GetAsync();
        Assert.Same(first, await publishChannel.GetAsync());
        Assert.Equal(1, created);

        await first.CloseAsync();

        var second = await publishChannel.GetAsync();
        Assert.NotSame(first, second);
        Assert.True(second.IsOpen);
        Assert.Equal(2, created);
    }
}
