using System.Diagnostics;
using Weltmeyer.RabbitMediator.Contracts;
using Weltmeyer.RabbitMediator.TestTool;
using Weltmeyer.RabbitMediator.TestTool.Consumers;
using Weltmeyer.RabbitMediator.TestTool.Messages;

namespace Weltmeyer.RabbitMediator.Aspire.Tests;

[Collection("AspireHostCollection")]
public class MessageTests
{
    private readonly AspireHostFixture _aspireHostFixture;


    public MessageTests(AspireHostFixture aspireHostFixture)
    {
        _aspireHostFixture = aspireHostFixture;
    }


    [Fact]
    public async Task TestBroadcast()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);


        var tasks = new List<Task>();
        foreach (var mediator in allMediators)
            mediator.GetConsumerInstance<TestBroadCastMessageConsumer>()!.ReceivedMessages = 0;
        foreach (var mediator in allMediators)
        {
            tasks.Add(Task.Run(async () =>
            {
                var message = new TestBroadcastMessage();
                var sendResult = await mediator.Send(message);
                if (!sendResult.Success)
                    Debugger.Break();
            }));
        }

        await Task.WhenAll(tasks);
        var requiredMessageCount = allMediators.Length * allMediators.Length;
        var sumReceived = allMediators.Sum(m =>
            m.GetConsumerInstance<TestBroadCastMessageConsumer>()!.ReceivedMessages);
        Assert.Equal(requiredMessageCount, sumReceived);
        await testApp.StopAsync();
    }

    [Fact]
    public async Task TestTargeted()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);
        var tasks = allMediators.SelectMany(mediator => allMediators.Select(target => (mediator, target)))
            .Select((mediatorAndTarget) => Task.Run(async () =>
            {
                var message = new TestTargetedMessage
                {
                    TargetInstance = mediatorAndTarget.target.GetInstanceInformation(),
                };
                var sendResult = await mediatorAndTarget.mediator.Send(message);
                Assert.True(sendResult.Success);
            })).ToArray();

        await Task.WhenAll(tasks);
        var requiredMessageCount = allMediators.Length * allMediators.Length;
        var sumReceived = allMediators.Sum(m =>
            m.GetConsumerInstance<TestTargetedMessageConsumer>()!.ReceivedMessages);
        Assert.Equal(requiredMessageCount, sumReceived);
        await testApp.StopAsync();
    }

    [Fact]
    public async Task TestTargeted_TimedOut()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);
        var tasks = allMediators.SelectMany(mediator => allMediators.Select(target => (mediator, target)))
            .Select((mediatorAndTarget) => Task.Run(async () =>
            {
                var message = new TestTargetedMessage
                {
                    TargetInstance = mediatorAndTarget.target.GetInstanceInformation(),
                    Delay = TimeSpan.FromSeconds(1),
                };

                var sendResult =
                    await mediatorAndTarget.mediator.Send(message, confirmTimeOut: TimeSpan.FromSeconds(0.5));
                Assert.False(sendResult.Success);
                Assert.True(sendResult.TimedOut);
            })).ToArray();

        await Task.WhenAll(tasks);
        var requiredMessageCount = allMediators.Length * allMediators.Length;
        await Task.Delay(TimeSpan
            .FromSeconds(2)); //wait some time as the consumers get the remaining message later than our timeout raise :)
        var sumReceived = allMediators.Sum(m =>
            m.GetConsumerInstance<TestTargetedMessageConsumer>()!.ReceivedMessages);
        Assert.Equal(requiredMessageCount, sumReceived);
        await testApp.StopAsync();
    }


    [Fact]
    public async Task TestAnyTargeted_Small()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);

        foreach (var mediator in allMediators)
            mediator.GetConsumerInstance<TestAnyTargetedMessageConsumer>()!.ReceivedMessages = 0;
        var sender = allMediators.First();
        //var receiver = allMediators.Skip(1).First();
        var message = new TestAnyTargetedMessage();
        var sendResult = await sender.Send(message, confirmTimeOut: TimeSpan.FromSeconds(999));
        Assert.True(sendResult.Success);
        var requiredMessageCount = 1;
        var sumReceived = allMediators.Sum(m =>
            m.GetConsumerInstance<TestAnyTargetedMessageConsumer>()!.ReceivedMessages);

        Assert.Equal(requiredMessageCount, sumReceived);
        await testApp.StopAsync();
    }

    [Fact]
    public async Task TestAnyTargeted_Small_Crashing()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);

        foreach (var mediator in allMediators)
            mediator.GetConsumerInstance<TestAnyTargetedMessageConsumer>()!.ReceivedMessages = 0;
        var sender = allMediators.First();
        //var receiver = allMediators.Skip(1).First();
        var message = new TestAnyTargetedMessage { CrashPlease = true };
        var sendResult = await sender.Send(message);
        Assert.Equal(typeof(TestException).FullName, sendResult.ExceptionData?.TypeFullName);
        Assert.False(sendResult.Success);
        var requiredMessageCount = 1;
        var sumReceived = allMediators.Sum(m =>
            m.GetConsumerInstance<TestAnyTargetedMessageConsumer>()!.ReceivedMessages);

        Assert.Equal(requiredMessageCount, sumReceived);
        await testApp.StopAsync();
    }

    [Fact]
    public async Task TestAnyTargeted()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);

        foreach (var mediator in allMediators)
            mediator.GetConsumerInstance<TestAnyTargetedMessageConsumer>()!.ReceivedMessages = 0;
        var tasks = new List<Task>();
        foreach (var mediator in allMediators)
        {
            tasks.Add(Task.Run(async () =>
            {
                var message = new TestAnyTargetedMessage();
                var sendResult = await mediator.Send(message);
                Assert.True(sendResult.Success);
            }));
        }

        await Task.WhenAll(tasks);
        var requiredMessageCount = allMediators.Length;
        var sumReceived = allMediators.Sum(m =>
            m.GetConsumerInstance<TestAnyTargetedMessageConsumer>()!.ReceivedMessages);

        Assert.Equal(requiredMessageCount, sumReceived);
        await testApp.StopAsync();
    }

    [Fact]
    public async Task TestAnyTargeted_Crashing()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);

        foreach (var mediator in allMediators)
        {
            mediator.GetConsumerInstance<TestAnyTargetedMessageConsumer>()!.ReceivedMessages = 0;
        }

        var tasks = new List<Task>();
        foreach (var mediator in allMediators)
        {
            tasks.Add(Task.Run(async () =>
            {
                var message = new TestAnyTargetedMessage { CrashPlease = true };
                var sendResult = await mediator.Send(message);
                Assert.Equal(typeof(TestException).FullName, sendResult.ExceptionData?.TypeFullName);
                Assert.False(sendResult.Success);
            }));
        }

        await Task.WhenAll(tasks);
        var requiredMessageCount = allMediators.Length;
        var sumReceived = allMediators.Sum(m =>
            m.GetConsumerInstance<TestAnyTargetedMessageConsumer>()!.ReceivedMessages);

        Assert.Equal(requiredMessageCount, sumReceived);
        await testApp.StopAsync();
    }

    [Fact]
    public async Task TestGuidEmptyTarget()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await allMediators.First().Send(new TestTargetedMessage
                { TargetInstance = new InstanceInformation(Guid.Empty, Guid.Empty) });
        });
    }

    [Fact]
    public async Task TestNonExistingTarget()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);

        var result =
            await allMediators.First().Send(new TestTargetedMessage
                { TargetInstance = new InstanceInformation(Guid.NewGuid(), Guid.NewGuid()) }); //should fail
        Assert.False(result.Success);
        Assert.True(result.SendFailure);
    }

    [Fact]
    public async Task TestNonExistingTarget_NoConfirm()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);

        var result = await allMediators.First()
            .Send(new TestTargetedMessage
                {
                    TargetInstance = new InstanceInformation(Guid.NewGuid(), Guid.NewGuid())
                },
                confirmPublish: false);
        Assert.True(result.Success);
    }


    [Fact]
    public async Task TestOneReceiverOneSender()
    {
        var connectionString = await _aspireHostFixture.AspireAppHost.GetConnectionStringAsync("rabbitmq");

        using var testApp = await _aspireHostFixture.PrepareEmptyHost(builder =>
        {
            builder.Services.AddRabbitMediator(
                [typeof(TestTargetedMessageConsumer)],
                connectionString!, "consumer");
            builder.Services.AddRabbitMediator(Array.Empty<Type>(),
                connectionString!, "sender");
        });

        var consumer = testApp.Services.GetRequiredKeyedService<IRabbitMediator>("consumer");
        var sender = testApp.Services.GetRequiredKeyedService<IRabbitMediator>("sender");

        var sendResult = await sender.Send(new TestTargetedMessage
        {
            TargetInstance = consumer.GetInstanceInformation()
        });
        Assert.True(sendResult.Success);
        //Assert.Null(sender.GetConsumerInstance<TestTargetedMessageConsumer>());
        Assert.Equal(1, consumer.GetConsumerInstance<TestTargetedMessageConsumer>()!.ReceivedMessages);
    }

    [Fact]
    public async Task TestNoReceiverOneSender()
    {
        var connectionString = await _aspireHostFixture.AspireAppHost.GetConnectionStringAsync("rabbitmq");

        using var testApp = await _aspireHostFixture.PrepareEmptyHost(builder =>
        {
            builder.Services.AddRabbitMediator(Array.Empty<Type>(),
                connectionString!, "consumer");
            builder.Services.AddRabbitMediator(Array.Empty<Type>(),
                connectionString!, "sender");
        });

        var consumer = testApp.Services.GetRequiredKeyedService<IRabbitMediator>("consumer");
        var sender = testApp.Services.GetRequiredKeyedService<IRabbitMediator>("sender");

        var sendResult = await sender.Send(new TestTargetedMessage
        {
            TargetInstance = new InstanceInformation
            {
                InstanceId = consumer.InstanceId,
                InstanceScope = Guid.NewGuid()
            }
        });
        Assert.False(sendResult.Success);
        Assert.True(sendResult.SendFailure);
        //Assert.Null(sender.GetConsumerInstance<TestTargetedMessageConsumer>());
        //Assert.Null(consumer.GetConsumerInstance<TestTargetedMessageConsumer>());
    }
}