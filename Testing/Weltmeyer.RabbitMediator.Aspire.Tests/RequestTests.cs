using RabbitMQ.Client.Exceptions;
using Weltmeyer.RabbitMediator.Contracts.Contracts;
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
            mediator.GetConsumerInstance<TestTargetedRequestConsumer>()!.ReceivedMessages = 0;
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
            //var response = await requester.Request<TestTargetedRequest, TestTargetedResponse>(message);
            var response = await requester.Request(message);
            requiredMessageCount++;
            Assert.True(response.Success);
            Assert.Equal(response.CorrelationId, message.CorrelationId);
        }


        var sumReceived = allMediators.Sum(m =>
            m.GetConsumerInstance<TestTargetedRequestConsumer>()!.ReceivedMessages);

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
            mediator.GetConsumerInstance<TestTargetedRequestConsumer>()!.ReceivedMessages = 0;
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
            m.GetConsumerInstance<TestTargetedRequestConsumer>()!.ReceivedMessages);

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
            mediator.GetConsumerInstance<TestTargetedRequestConsumer>()!.ReceivedMessages = 0;
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
                    var timeOut = TimeSpan.FromSeconds(0.5);
                    var exception = await Assert.ThrowsAsync<RabbitMediatorTimeoutException>(async () =>
                        await mediator.Request<TestTargetedRequest, TestTargetedResponse>(message,
                            responseTimeOut: timeOut));
                    Assert.Equal(message.CorrelationId, exception.CorrelationId);
                    Assert.Equal(typeof(TestTargetedRequest), exception.RequestType);
                    Assert.Equal(timeOut, exception.Timeout);
                }));
            }
        }

        await Task.WhenAll(tasks);

        // Requests whose timeout ran out before a consumer could start on them are dropped, so not all of them
        // arrive any more - with a prefetch window matching the dispatch concurrency the backlog waits in the
        // broker's queue, where the per-message expiry takes care of it. What must not happen is a request
        // arriving that nobody sent, or one arriving twice.
        await Task.Delay(TimeSpan.FromSeconds(3));
        var requiredMessageCount = allMediators.Length * allMediators.Length;
        var sumReceived = allMediators.Sum(m =>
            m.GetConsumerInstance<TestTargetedRequestConsumer>()!.ReceivedMessages);

        Assert.InRange(sumReceived, 1, requiredMessageCount);
        await testApp.StopAsync();
    }

    [Fact]
    public async Task TestTargeted_TimedOut_TryRequest()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);

        foreach (var mediator in allMediators)
        {
            mediator.GetConsumerInstance<TestTargetedRequestConsumer>()!.ReceivedMessages = 0;
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
                    var response = await mediator.TryRequest(message, responseTimeOut: TimeSpan.FromSeconds(0.5));
                    Assert.Equal(message.CorrelationId, response.CorrelationId);
                    Assert.Equal(InstanceInformation.Empty, response.SenderInstance);
                    Assert.False(response.Success);
                    Assert.True(response.TimedOut);
                    Assert.False(response.SendFailure);
                }));
            }
        }

        await Task.WhenAll(tasks);

        // Requests whose timeout ran out before a consumer could start on them are dropped, so not all of them
        // arrive any more - with a prefetch window matching the dispatch concurrency the backlog waits in the
        // broker's queue, where the per-message expiry takes care of it. What must not happen is a request
        // arriving that nobody sent, or one arriving twice.
        await Task.Delay(TimeSpan.FromSeconds(3));
        var requiredMessageCount = allMediators.Length * allMediators.Length;
        var sumReceived = allMediators.Sum(m =>
            m.GetConsumerInstance<TestTargetedRequestConsumer>()!.ReceivedMessages);

        Assert.InRange(sumReceived, 1, requiredMessageCount);
        await testApp.StopAsync();
    }

    [Fact]
    public async Task TestTargeted_TryRequest_Succeeds()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);

        var requester = allMediators.First();
        var responder = allMediators.Skip(1).First();

        var message = new TestTargetedRequest
        {
            TargetInstance = responder.GetInstanceInformation(),
        };
        var response = await requester.TryRequest(message);
        Assert.True(response.Success);
        Assert.False(response.TimedOut);
        Assert.False(response.SendFailure);
        Assert.Equal(message.CorrelationId, response.CorrelationId);
        Assert.Equal(message.TargetInstance, response.SenderInstance);
        await testApp.StopAsync();
    }

    [Fact]
    public async Task TestAnyTargeted()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);

        foreach (var mediator in allMediators)
        {
            mediator.GetConsumerInstance<TestAnyTargetedRequestConsumer>()!.ReceivedMessages = 0;
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
            m.GetConsumerInstance<TestAnyTargetedRequestConsumer>()!.ReceivedMessages);

        Assert.Equal(requiredMessageCount, sumReceived);
        await testApp.StopAsync();
    }
    
    [Fact]
    public async Task TestAnyTargetedDerivedConsumer()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);

        foreach (var mediator in allMediators)
        {
            mediator.GetConsumerInstance<TestAnyTargetedRequestDerivedConsumer>()!.ReceivedMessages = 0;
        }

        var tasks = new List<Task>();
        foreach (var mediator in allMediators)
        {
            foreach (var _ in allMediators)
            {
                tasks.Add(Task.Run(async () =>
                {
                    var message = new TestAnyTargetedRequestForAbstract();
                    //var response = await mediator.Request<TestAnyTargetedRequestForAbstract, TestAnyTargetedResponseForAbstract>(message);
                    var response = await mediator.Request(message);
                    Assert.Equal(message.CorrelationId, response.CorrelationId);
                    Assert.True(response.Success);
                }));
            }
        }

        await Task.WhenAll(tasks);
        var requiredMessageCount = allMediators.Length * allMediators.Length;
        var sumReceived = allMediators.Sum(m =>
            m.GetConsumerInstance<TestAnyTargetedRequestDerivedConsumer>()!.ReceivedMessages);

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
            mediator.GetConsumerInstance<TestAnyTargetedRequestConsumer>()!.ReceivedMessages = 0;
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
            m.GetConsumerInstance<TestAnyTargetedRequestConsumer>()!.ReceivedMessages);

        Assert.Equal(requiredMessageCount, sumReceived);
        await testApp.StopAsync();
    }

    /// <summary>
    /// A request arriving at an instance that has no consumer for it must come back as a failed response, not
    /// leave the requester waiting for its timeout. The mapping is removed here to produce the situation the
    /// receive loop used to hit a NullReferenceException on.
    /// </summary>
    [Fact]
    public async Task TestRequestWithoutConsumerAnswersInsteadOfTimingOut()
    {
        var connectionString = await _aspireHostFixture.AspireAppHost.GetConnectionStringAsync("rabbitmq");

        var multiplexer = new RabbitMediatorMultiplexer(connectionString!);
        await using var multiplexerLifetime = multiplexer;
        await multiplexer.Configure(CancellationToken.None);

        using var testApp = await _aspireHostFixture.PrepareEmptyHost(_ => { });
        var responder = multiplexer.CreateRabbitMediator(testApp.Services, new RabbitMediatorConfiguration
        {
            ConsumerTypes = [typeof(TestTargetedRequestConsumer)],
        });
        var requester = multiplexer.CreateRabbitMediator(testApp.Services, new RabbitMediatorConfiguration());
        await responder.EnsureConfigured();
        await requester.EnsureConfigured();

        //the queue stays bound, only the mapping to the consumer goes away
        Assert.True(multiplexer.TryGetConfiguration(responder)!.SentTypeToConsumerMapping
            .TryRemove(typeof(TestTargetedRequest), out _));

        var response = await requester.Request(new TestTargetedRequest
        {
            TargetInstance = ((IRabbitMediator)responder).GetInstanceInformation(),
        }, responseTimeOut: TimeSpan.FromSeconds(10));

        Assert.False(response.Success);
        Assert.False(response.TimedOut);
        Assert.Contains(nameof(TestTargetedRequest), response.ExceptionData?.ErrorMessage);
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
                TargetInstance = new(string.Empty, string.Empty),
            });
        });
    }

    [Fact]
    public async Task TestOneReceiverOneSender()
    {
        var connectionString = await _aspireHostFixture.AspireAppHost.GetConnectionStringAsync("rabbitmq");

        using var testApp = await _aspireHostFixture.PrepareEmptyHost(builder =>
        {
            builder.Services.AddRabbitMediator(cfg =>
            {
                cfg.ConsumerTypes =
                    [typeof(TestTargetedRequestConsumer)];
                cfg.ConnectionString = connectionString!;
                cfg.ServiceKey = "consumer";
            });
            builder.Services.AddRabbitMediator(cfg =>
            {
                cfg.ConnectionString = connectionString!;
                cfg.ServiceKey = "sender";
            });
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
        Assert.Null(sender.GetConsumerInstance<TestTargetedRequestConsumer>());
        Assert.Equal(1, consumer.GetConsumerInstance<TestTargetedRequestConsumer>()!.ReceivedMessages);
    }

    [Fact]
    public async Task TestNoReceiverOneSender()
    {
        var connectionString = await _aspireHostFixture.AspireAppHost.GetConnectionStringAsync("rabbitmq");

        using var testApp = await _aspireHostFixture.PrepareEmptyHost(builder =>
        {
            builder.Services.AddRabbitMediator(cfg =>
            {
                cfg.ConnectionString = connectionString!;
                cfg.ServiceKey = "consumer";
            });
            builder.Services.AddRabbitMediator(cfg =>
            {
                cfg.ConnectionString = connectionString!;
                cfg.ServiceKey = "sender";
            });
        });

        var consumer = testApp.Services.GetRequiredKeyedService<IRabbitMediator>("consumer");
        var sender = testApp.Services.GetRequiredKeyedService<IRabbitMediator>("sender");

        var request = new TestTargetedRequest
        {
            TargetInstance = consumer.GetInstanceInformation(),
        };
        var exception = await Assert.ThrowsAsync<RabbitMediatorSendFailureException>(async () =>
            await sender.Request<TestTargetedRequest, TestTargetedResponse>(request));
        Assert.Equal(typeof(TestTargetedRequest), exception.RequestType);
        Assert.Equal(request.CorrelationId, exception.CorrelationId);
        Assert.IsAssignableFrom<RabbitMQClientException>(exception.InnerException);
    }

    [Fact]
    public async Task TestNoReceiverOneSender_TryRequest()
    {
        var connectionString = await _aspireHostFixture.AspireAppHost.GetConnectionStringAsync("rabbitmq");

        using var testApp = await _aspireHostFixture.PrepareEmptyHost(builder =>
        {
            builder.Services.AddRabbitMediator(cfg =>
            {
                cfg.ConnectionString = connectionString!;
                cfg.ServiceKey = "consumer";
            });
            builder.Services.AddRabbitMediator(cfg =>
            {
                cfg.ConnectionString = connectionString!;
                cfg.ServiceKey = "sender";
            });
        });

        var consumer = testApp.Services.GetRequiredKeyedService<IRabbitMediator>("consumer");
        var sender = testApp.Services.GetRequiredKeyedService<IRabbitMediator>("sender");

        var request = new TestTargetedRequest
        {
            TargetInstance = consumer.GetInstanceInformation(),
        };
        var sendResult = await sender.TryRequest(request);
        Assert.False(sendResult.Success);
        Assert.True(sendResult.SendFailure);
        Assert.False(sendResult.TimedOut);
        Assert.Equal(request.CorrelationId, sendResult.CorrelationId);
    }

    [Fact]
    public async Task TestTryRequest_ObsoleteTwoGenericOverload()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);

        var requester = allMediators.First();
        var responder = allMediators.Skip(1).First();

        var message = new TestTargetedRequest
        {
            TargetInstance = responder.GetInstanceInformation(),
        };
#pragma warning disable CS0618 // kept working until the overload is removed
        var response = await requester.TryRequest<TestTargetedRequest, TestTargetedResponse>(message);
#pragma warning restore CS0618
        Assert.True(response.Success);
        Assert.Equal(message.CorrelationId, response.CorrelationId);
        await testApp.StopAsync();
    }
}