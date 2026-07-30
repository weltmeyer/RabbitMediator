using System.Diagnostics;
using Weltmeyer.RabbitMediator.Contracts;
using Weltmeyer.RabbitMediator.Contracts.Contracts;
using Weltmeyer.RabbitMediator.Contracts.MessageBases;
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
        {
            tasks.Add(Task.Run(async () =>
            {
                var message = new TestBroadcastMessage();
                var sendResult = await mediator.Send(message);
                Assert.True(sendResult.Success);
            }));
        }

        await Task.WhenAll(tasks);
        var requiredMessageCount = allMediators.Length * allMediators.Length;

        // Send returns once *one* consumer acked, so the remaining fanout receivers may still be working.
        var sumReceived = 0L;
        for (var i = 0; i < 10 && sumReceived < requiredMessageCount; i++)
        {
            sumReceived = allMediators.Sum(m =>
                m.GetConsumerInstance<TestBroadCastMessageConsumer>()!.ReceivedMessages);
            if (sumReceived < requiredMessageCount)
                await Task.Delay(TimeSpan.FromSeconds(1));
        }

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


    /// <summary>
    /// A message handed to Send through a base-typed variable has to reach the same consumer as the same
    /// message with its concrete static type: the exchange follows the runtime type, not TMessageType.
    /// </summary>
    [Fact]
    public async Task TestTargeted_SentThroughBaseTypedVariable()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);

        foreach (var mediator in allMediators)
        {
            mediator.GetConsumerInstance<TestTargetedMessageConsumer>()!.ReceivedMessages = 0;
        }

        var sender = allMediators.First();
        var target = allMediators.Skip(1).First();

        Message message = new TestTargetedMessage { TargetInstance = target.GetInstanceInformation() };
        var sendResult = await sender.Send(message);

        Assert.True(sendResult.Success);
        Assert.False(sendResult.SendFailure);
        Assert.Equal(1, target.GetConsumerInstance<TestTargetedMessageConsumer>()!.ReceivedMessages);
        await testApp.StopAsync();
    }

    /// <summary>
    /// Publishing on a connection that is already closed is a send failure like any other, not an exception
    /// the caller has to catch on top of inspecting the result.
    /// </summary>
    [Fact]
    public async Task TestSendOnClosedConnectionReportsSendFailure()
    {
        using var host = await _aspireHostFixture.PrepareHost();
        var target = host.Services.GetAllMediators(_aspireHostFixture).First();

        var connectionFactory = new RabbitMQ.Client.ConnectionFactory
        {
            Uri = new Uri(_aspireHostFixture.RabbitMQConnectionString!),
            AutomaticRecoveryEnabled = false,
        };
        var connection = await connectionFactory.CreateConnectionAsync();
        var multiplexer = new RabbitMediatorMultiplexer(_aspireHostFixture.RabbitMQConnectionString!,
            customConnection: connection);
        await using var _ = multiplexer;
        await multiplexer.Configure(CancellationToken.None);
        var sender = multiplexer.CreateRabbitMediator(host.Services, new RabbitMediatorConfiguration());
        await sender.EnsureConfigured();

        await connection.CloseAsync(200, "closed by test", TimeSpan.FromSeconds(5), false);

        var sendResult = await sender.Send(new TestTargetedMessage
            { TargetInstance = target.GetInstanceInformation() });

        Assert.False(sendResult.Success);
        Assert.True(sendResult.SendFailure);
    }

    /// <summary>
    /// A burst far larger than the prefetch window still has to arrive completely - the limit only bounds how
    /// much the broker hands out at once.
    /// </summary>
    [Fact]
    public async Task TestBurstIsDeliveredCompletelyWithSmallPrefetch()
    {
        using var testApp = await _aspireHostFixture.PrepareEmptyHost(builder =>
        {
            builder.Services.AddRabbitMediator(cfg =>
            {
                cfg.ConsumerTypes = [typeof(TestAnyTargetedMessageConsumer)];
                cfg.ConnectionString = _aspireHostFixture.RabbitMQConnectionString!;
                cfg.ConsumerDispatchConcurrency = 2;
                cfg.PrefetchCount = 2;
                cfg.ServiceKey = "burstconsumer";
            });
            builder.Services.AddRabbitMediator(cfg =>
            {
                cfg.ConnectionString = _aspireHostFixture.RabbitMQConnectionString!;
                cfg.ServiceKey = "burstsender";
            });
        });

        var consumer = testApp.Services.GetRequiredKeyedService<IRabbitMediator>("burstconsumer");
        var sender = testApp.Services.GetRequiredKeyedService<IRabbitMediator>("burstsender");
        const int messageCount = 100;

        for (var i = 0; i < messageCount; i++)
        {
            var sendResult = await sender.Send(new TestAnyTargetedMessage(), confirmPublish: false);
            Assert.True(sendResult.Success);
        }

        var received = 0L;
        for (var i = 0; i < 30 && received < messageCount; i++)
        {
            received = consumer.GetConsumerInstance<TestAnyTargetedMessageConsumer>()!.ReceivedMessages;
            if (received < messageCount)
                await Task.Delay(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(messageCount, received);
    }

    [Fact]
    public async Task TestAnyTargeted_Small()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);

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
                { TargetInstance = new InstanceInformation(string.Empty, string.Empty) });
        });
    }

    [Fact]
    public async Task TestNonExistingTarget()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);

        var result =
            await allMediators.First().Send(new TestTargetedMessage
                { TargetInstance = new InstanceInformation(Guid.NewGuid().ToString(), Guid.NewGuid().ToString()) }); //should fail
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
                    TargetInstance = new InstanceInformation(Guid.NewGuid().ToString(), Guid.NewGuid().ToString())
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
                cfg =>
                {
                    cfg.ConsumerTypes.Add(typeof(TestTargetedMessageConsumer));
                    cfg.ConnectionString = connectionString!;
                    cfg.ServiceKey = "consumer";
                });

            builder.Services.AddRabbitMediator(
                cfg =>
                {
                    cfg.ConnectionString = connectionString!;
                    cfg.ServiceKey = "sender";
                });
            
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
            builder.Services.AddRabbitMediator(
                cfg =>
                {
                    cfg.ConnectionString = connectionString!;
                    cfg.ServiceKey = "consumer";
                });

            builder.Services.AddRabbitMediator(
                cfg =>
                {
                    cfg.ConnectionString = connectionString!;
                    cfg.ServiceKey = "sender";
                });

        });

        var consumer = testApp.Services.GetRequiredKeyedService<IRabbitMediator>("consumer");
        var sender = testApp.Services.GetRequiredKeyedService<IRabbitMediator>("sender");

        var sendResult = await sender.Send(new TestTargetedMessage
        {
            TargetInstance = new InstanceInformation
            {
                InstanceId = consumer.InstanceId,
                InstanceScope = Guid.NewGuid().ToString()
            }
        });
        Assert.False(sendResult.Success);
        Assert.True(sendResult.SendFailure);
        //Assert.Null(sender.GetConsumerInstance<TestTargetedMessageConsumer>());
        //Assert.Null(consumer.GetConsumerInstance<TestTargetedMessageConsumer>());
    }

    [Fact]
    public async Task TestNoBroadcastConsumer()
    {
        var connectionString = await _aspireHostFixture.AspireAppHost.GetConnectionStringAsync("rabbitmq");

        using var testApp = await _aspireHostFixture.PrepareEmptyHost(builder =>
        {
            builder.Services.AddRabbitMediator(cfg =>
            {
                cfg.ConsumerTypes.Add(typeof(TestBroadCastMessageConsumer));
                cfg.ConnectionString = connectionString!;
                cfg.ServiceKey = "receiverWithConsumer";
            });

            builder.Services.AddRabbitMediator(cfg =>
            {
                
                cfg.ConnectionString = connectionString!;
                cfg.ServiceKey = "receiverWithoutConsumer";
            });

            builder.Services.AddRabbitMediator(cfg =>
            {
                
                cfg.ConnectionString = connectionString!;
                cfg.ServiceKey = "sender";
            });
        
        });
        
        
        //var receiverWithConsumer = testApp.Services.GetRequiredKeyedService<IRabbitMediator>("receiverWithConsumer");
        var receiverWithoutConsumer = testApp.Services.GetRequiredKeyedService<IRabbitMediator>("receiverWithoutConsumer");
        var sender = testApp.Services.GetRequiredKeyedService<IRabbitMediator>("sender");


        var sendResult = await sender.Send(new TestBroadcastMessage());
        await Task.Delay(1000);
    }
}