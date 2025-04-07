using Weltmeyer.RabbitMediator.Contracts;
using Weltmeyer.RabbitMediator.TestTool;
using Weltmeyer.RabbitMediator.TestTool.Consumers;
using Weltmeyer.RabbitMediator.TestTool.Messages;

namespace Weltmeyer.RabbitMediator.Aspire.Tests;

[Collection("AspireHostCollection")]
public class RequestTests
{
    private readonly AspireHostFixture _aspireHostFixture;


    public RequestTests(AspireHostFixture aspireHostFixture)
    {
        _aspireHostFixture = aspireHostFixture;
    }


    [Fact]
    public async Task TestSingleTargeted()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);

        foreach (var mediator in allMediators)
        {
            mediator.GetRequestConsumerInstance<TestTargetedRequestConsumer>()!.ReceivedMessages = 0;
        }

        var requester = allMediators.First();
        var responder = allMediators.Skip(1).First();

        var message = new TestTargetedRequest
        {
            TargetInstance = responder.GetInstanceInformation(),
        };
        var requiredMessageCount = 0;
        for (int i = 0; i < 1; i++)
        {
            var response = await requester.Request<TestTargetedRequest, TestTargetedResponse>(message);
            requiredMessageCount++;
            Assert.True(response.Success);
            Assert.Equal(response.CorrelationId, message.CorrelationId);
        }


        var sumReceived = allMediators.Sum(m =>
            m.GetRequestConsumerInstance<TestTargetedRequestConsumer>()!.ReceivedMessages);

        Assert.Equal(requiredMessageCount, sumReceived);
        await testApp.StopAsync();
    }

    [Fact]
    public async Task TestTargeted()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);

        foreach (var mediator in allMediators)
        {
            mediator.GetRequestConsumerInstance<TestTargetedRequestConsumer>()!.ReceivedMessages = 0;
        }

        var tasks = new List<Task>();
        foreach (var mediator in allMediators)
        {
            foreach (var target in allMediators)
            {
                tasks.Add(Task.Run(async () =>
                {
                    var message = new TestTargetedRequest
                    {
                        TargetInstance = target.GetInstanceInformation(),
                    };
                    var response = await mediator.Request<TestTargetedRequest, TestTargetedResponse>(message);
                    Assert.Equal(message.CorrelationId, response.CorrelationId);
                    Assert.Equal(message.TargetInstance, response.SenderInstance);
                    Assert.True(response.Success);
                }));
            }
        }

        await Task.WhenAll(tasks);
        var requiredMessageCount = allMediators.Length * allMediators.Length;
        var sumReceived = allMediators.Sum(m =>
            m.GetRequestConsumerInstance<TestTargetedRequestConsumer>()!.ReceivedMessages);

        Assert.Equal(requiredMessageCount, sumReceived);
        await testApp.StopAsync();
    }


    [Fact]
    public async Task TestTargeted_TimedOut()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);

        foreach (var mediator in allMediators)
        {
            mediator.GetRequestConsumerInstance<TestTargetedRequestConsumer>()!.ReceivedMessages = 0;
        }

        var tasks = new List<Task>();
        foreach (var mediator in allMediators)
        {
            foreach (var target in allMediators)
            {
                tasks.Add(Task.Run(async () =>
                {
                    var message = new TestTargetedRequest
                    {
                        TargetInstance = target.GetInstanceInformation(),

                        Delay = TimeSpan.FromSeconds(1),
                    };
                    var response =
                        await mediator.Request<TestTargetedRequest, TestTargetedResponse>(message,
                            responseTimeOut: TimeSpan.FromSeconds(0.5));
                    Assert.Equal(message.CorrelationId, response.CorrelationId);
                    Assert.Equal(InstanceInformation.Empty, response.SenderInstance);
                    Assert.False(response.Success);
                    Assert.True(response.TimedOut);
                }));
            }
        }

        await Task.WhenAll(tasks);
        var requiredMessageCount = allMediators.Length * allMediators.Length;
        var sumReceived = 0L;
        for (int i = 0; i < 10 && sumReceived < requiredMessageCount; i++)
        {
            sumReceived = allMediators.Sum(m =>
                m.GetRequestConsumerInstance<TestTargetedRequestConsumer>()!.ReceivedMessages);
            await Task.Delay(TimeSpan.FromSeconds(1)); //need a raise in tests maybe...
        }


        Assert.Equal(requiredMessageCount, sumReceived);
        await testApp.StopAsync();
    }

    [Fact]
    public async Task TestAnyTargeted()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);

        foreach (var mediator in allMediators)
        {
            mediator.GetRequestConsumerInstance<TestAnyTargetedRequestConsumer>()!.ReceivedMessages = 0;
        }

        var tasks = new List<Task>();
        foreach (var mediator in allMediators)
        {
            foreach (var _ in allMediators)
            {
                tasks.Add(Task.Run(async () =>
                {
                    var message = new TestAnyTargetedRequest();
                    var response = await mediator.Request<TestAnyTargetedRequest, TestAnyTargetedResponse>(message);
                    Assert.Equal(message.CorrelationId, response.CorrelationId);
                    Assert.True(response.Success);
                }));
            }
        }

        await Task.WhenAll(tasks);
        var requiredMessageCount = allMediators.Length * allMediators.Length;
        var sumReceived = allMediators.Sum(m =>
            m.GetRequestConsumerInstance<TestAnyTargetedRequestConsumer>()!.ReceivedMessages);

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
            mediator.GetRequestConsumerInstance<TestAnyTargetedRequestConsumer>()!.ReceivedMessages = 0;
        }

        var tasks = new List<Task>();
        foreach (var mediator in allMediators)
        {
            foreach (var _ in allMediators)
            {
                tasks.Add(Task.Run(async () =>
                {
                    var message = new TestAnyTargetedRequest { CrashPlease = true };
                    var response = await mediator.Request<TestAnyTargetedRequest, TestAnyTargetedResponse>(message);
                    Assert.Equal(message.CorrelationId, response.CorrelationId);
                    Assert.Equal(response.ExceptionData?.TypeFullName, typeof(TestException).FullName);
                    Assert.False(response.Success);
                }));
            }
        }

        await Task.WhenAll(tasks);
        var requiredMessageCount = allMediators.Length * allMediators.Length;
        var sumReceived = allMediators.Sum(m =>
            m.GetRequestConsumerInstance<TestAnyTargetedRequestConsumer>()!.ReceivedMessages);

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
            await allMediators.First().Request<TestTargetedRequest, TestTargetedResponse>(new TestTargetedRequest
            {
                TargetInstance = new(Guid.Empty, Guid.Empty),
            });
        });
    }

    [Fact]
    public async Task TestOneReceiverOneSender()
    {
        var connectionString = await _aspireHostFixture.AspireAppHost.GetConnectionStringAsync("rabbitmq");

        using var testApp = await _aspireHostFixture.PrepareEmptyHost(builder =>
        {
            builder.Services.AddRabbitMediator([typeof(TestTargetedRequestConsumer)],
                connectionString!, "consumer");
            builder.Services.AddRabbitMediator(Array.Empty<Type>(),
                connectionString!, "sender");
        });

        var consumer = testApp.Services.GetRequiredKeyedService<IRabbitMediator>("consumer");
        var sender = testApp.Services.GetRequiredKeyedService<IRabbitMediator>("sender");

        var sendResult = await sender.Request<TestTargetedRequest, TestTargetedResponse>(new TestTargetedRequest
        {
            TargetInstance = consumer.GetInstanceInformation(),
        });
        Assert.True(sendResult.Success);
        Assert.False(sendResult.SendFailure);
        Assert.Equal(sendResult.SenderInstance.InstanceId, consumer.InstanceId);
        Assert.Null(sender.GetRequestConsumerInstance<TestTargetedRequestConsumer>());
        Assert.Equal(1, consumer.GetRequestConsumerInstance<TestTargetedRequestConsumer>()!.ReceivedMessages);
    }

    [Fact]
    public async Task TestNoReceiverOneSender()
    {
        var connectionString = await _aspireHostFixture.AspireAppHost.GetConnectionStringAsync("rabbitmq");

        using var testApp = await _aspireHostFixture.PrepareEmptyHost(builder =>
        {
            builder.Services.AddRabbitMediator(Array.Empty<Type>(), connectionString!, "consumer");
            builder.Services.AddRabbitMediator(Array.Empty<Type>(), connectionString!, "sender");
        });

        var consumer = testApp.Services.GetRequiredKeyedService<IRabbitMediator>("consumer");
        var sender = testApp.Services.GetRequiredKeyedService<IRabbitMediator>("sender");

        var sendResult = await sender.Request<TestTargetedRequest, TestTargetedResponse>(new TestTargetedRequest()
        {
            TargetInstance = consumer.GetInstanceInformation(),
        });
        Assert.False(sendResult.Success);
        Assert.True(sendResult.SendFailure);
    }
}