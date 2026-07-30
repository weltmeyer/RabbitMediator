using System.Diagnostics;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Weltmeyer.RabbitMediator.TestTool.Consumers;
using Weltmeyer.RabbitMediator.TestTool.Messages;

namespace Weltmeyer.RabbitMediator.Aspire.Tests;

[Collection("AspireHostCollection")]
public class LifetimeTests
{
    private readonly AspireHostFixture _aspireHostFixture;

    public LifetimeTests(AspireHostFixture aspireHostFixture)
    {
        _aspireHostFixture = aspireHostFixture;
    }

    /// <summary>
    /// The synchronous Dispose has to have finished the teardown when it returns - its queues gone from the
    /// broker - and the multiplexer has to have forgotten the mediator so nothing looks its topology up after.
    /// The queues are exclusive and auto-delete, but the multiplexer connection stays open here, so they can
    /// only be gone if the dispose actually ran to completion.
    /// </summary>
    [Fact]
    public async Task Dispose_TearsDownSynchronouslyAndForgetsTheMediator()
    {
        using var host = await _aspireHostFixture.PrepareHost();
        var multiplexer = new RabbitMediatorMultiplexer(_aspireHostFixture.RabbitMQConnectionString!);
        await using var _ = multiplexer;
        await multiplexer.Configure(CancellationToken.None);

        var mediator = multiplexer.CreateRabbitMediator(host.Services, new RabbitMediatorConfiguration
        {
            ConsumerAssemblies = [typeof(TestTargetedRequestConsumer).Assembly],
        });
        await mediator.EnsureConfigured();
        var ownedQueues = multiplexer.TryGetConfiguration(mediator)!.OwnedQueues.Keys.ToArray();
        Assert.NotEmpty(ownedQueues);

        mediator.Dispose();

        Assert.Null(multiplexer.TryGetConfiguration(mediator));
        Assert.True(mediator.Disposed);

        var connectionFactory = new ConnectionFactory { Uri = new Uri(_aspireHostFixture.RabbitMQConnectionString!) };
        await using var probeConnection = await connectionFactory.CreateConnectionAsync();
        foreach (var queueName in ownedQueues)
        {
            //a failing passive declare kills the channel, so probe each queue on a fresh one
            await using var probeChannel = await probeConnection.CreateChannelAsync();
            await Assert.ThrowsAsync<OperationInterruptedException>(async () =>
                await probeChannel.QueueDeclarePassiveAsync(queueName));
        }
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        using var host = await _aspireHostFixture.PrepareHost();
        var multiplexer = new RabbitMediatorMultiplexer(_aspireHostFixture.RabbitMQConnectionString!);
        await using var _ = multiplexer;
        await multiplexer.Configure(CancellationToken.None);

        var mediator = multiplexer.CreateRabbitMediator(host.Services, new RabbitMediatorConfiguration());
        await mediator.EnsureConfigured();

        mediator.Dispose();
        await mediator.DisposeAsync();
    }

    /// <summary>
    /// A request still waiting for its response when its mediator gets disposed must fail right away instead of
    /// sitting there until the response timeout elapses.
    /// </summary>
    [Fact]
    public async Task Dispose_FailsPendingRequestInsteadOfWaitingForTheTimeout()
    {
        using var host = await _aspireHostFixture.PrepareHost();
        var responder = host.Services.GetAllMediators(_aspireHostFixture).First();

        var multiplexer = new RabbitMediatorMultiplexer(_aspireHostFixture.RabbitMQConnectionString!);
        await using var _ = multiplexer;
        await multiplexer.Configure(CancellationToken.None);

        var requester = multiplexer.CreateRabbitMediator(host.Services, new RabbitMediatorConfiguration
        {
            DefaultResponseTimeOut = TimeSpan.FromSeconds(30),
        });
        await requester.EnsureConfigured();

        var request = new TestTargetedRequest
        {
            TargetInstance = responder.GetInstanceInformation(),
            Delay = TimeSpan.FromSeconds(10),
        };
        var pending = requester.Request(request);

        // give the request time to reach the broker before pulling the mediator away underneath it
        await Task.Delay(TimeSpan.FromSeconds(1));
        var stopwatch = Stopwatch.StartNew();
        await requester.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await pending);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"pending request took {stopwatch.Elapsed} to fail after dispose");
    }

    [Fact]
    public void GenerateId_UsesDigitsAndBothCases()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => RabbitMediator.GenerateId()).ToArray();

        Assert.All(ids, id => Assert.Equal(10, id.Length));
        Assert.All(ids, id => Assert.All(id, c => Assert.True(char.IsAsciiLetterOrDigit(c), $"unexpected char {c}")));
        var allCharacters = string.Concat(ids);
        Assert.Contains(allCharacters, char.IsAsciiDigit);
        Assert.Contains(allCharacters, char.IsAsciiLetterUpper);
        Assert.Contains(allCharacters, char.IsAsciiLetterLower);
    }

    /// <summary>
    /// The activity source has to be named after this library - AddRabbitMediatorTelemetry subscribes by that
    /// name, and it must not depend on there being an entry assembly.
    /// </summary>
    [Fact]
    public void ActivitySource_IsNamedAfterTheLibrary()
    {
        Assert.Equal("Weltmeyer.RabbitMediator", Telemetry.ActivitySource.Name);
    }
}
