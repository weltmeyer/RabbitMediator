using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Weltmeyer.RabbitMediator.Contracts.ConsumerBases;
using Weltmeyer.RabbitMediator.Contracts.MessageBases;

namespace Weltmeyer.RabbitMediator.Aspire.Tests;

public class SlowResponse : Response
{
    public bool CompletedNormally { get; set; }
}

public class SlowRequest : TargetedRequest<SlowResponse>;

/// <summary>Waits for the token, so the test can see when the mediator cancels it.</summary>
public class SlowRequestConsumer : IRequestConsumer<SlowRequest, SlowResponse>
{
    public static readonly TaskCompletionSource<TimeSpan> Cancelled =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static int Invocations;

    public Task<SlowResponse> Consume(SlowRequest message) =>
        throw new InvalidOperationException("the cancellable overload should have been called");

    public async Task<SlowResponse> Consume(SlowRequest message, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Invocations);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Cancelled.TrySetResult(stopwatch.Elapsed);
            throw;
        }

        return new SlowResponse { CompletedNormally = true };
    }
}

public class PatientResponse : Response;

public class PatientRequest : TargetedRequest<PatientResponse>;

/// <summary>Never looks at the token - the old style of consumer, which has to keep working.</summary>
public class PatientRequestConsumer : IRequestConsumer<PatientRequest, PatientResponse>
{
    public async Task<PatientResponse> Consume(PatientRequest message)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        return new PatientResponse();
    }
}

public class BusyResponse : Response;

public class BusyRequest : TargetedRequest<BusyResponse>;

/// <summary>Occupies the dispatcher for a while and ignores the token, so the next request has to wait locally.</summary>
public class BusyRequestConsumer : IRequestConsumer<BusyRequest, BusyResponse>
{
    public static int Invocations;

    public async Task<BusyResponse> Consume(BusyRequest message)
    {
        Interlocked.Increment(ref Invocations);
        await Task.Delay(TimeSpan.FromSeconds(3));
        return new BusyResponse();
    }
}

[Collection("AspireHostCollection")]
public class ConsumerCancellationTests
{
    private readonly AspireHostFixture _aspireHostFixture;

    public ConsumerCancellationTests(AspireHostFixture aspireHostFixture)
    {
        _aspireHostFixture = aspireHostFixture;
    }

    private Task<IHost> PrepareHost(params Type[] consumerTypes) =>
        _aspireHostFixture.PrepareEmptyHost(builder => builder.Services.AddRabbitMediator(cfg =>
        {
            cfg.ConnectionString = _aspireHostFixture.RabbitMQConnectionString!;
            cfg.ServiceKey = "cancellation";
            cfg.ConsumerTypes = consumerTypes.ToList();
        }));

    /// <summary>
    /// The sender's timeout keeps running while the consumer works, and cancels the token it was handed once
    /// it elapses - so nobody keeps working on an answer nobody is waiting for any more.
    /// </summary>
    [Fact]
    public async Task ConsumerIsCancelledWhenTheSendersTimeoutElapses()
    {
        using var testApp = await PrepareHost(typeof(SlowRequestConsumer));
        var mediator = testApp.Services.GetRequiredKeyedService<IRabbitMediator>("cancellation");

        var timeOut = TimeSpan.FromSeconds(1);
        await Assert.ThrowsAsync<RabbitMediatorTimeoutException>(async () =>
            await mediator.Request(new SlowRequest { TargetInstance = mediator.GetInstanceInformation() },
                responseTimeOut: timeOut));

        var cancelledAfter = await SlowRequestConsumer.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.InRange(cancelledAfter, timeOut - TimeSpan.FromMilliseconds(200), timeOut + TimeSpan.FromSeconds(3));
    }

    /// <summary>A consumer that never took a token has to keep working exactly as before.</summary>
    [Fact]
    public async Task ConsumerWithoutTheCancellableOverloadStillWorks()
    {
        using var testApp = await PrepareHost(typeof(PatientRequestConsumer));
        var mediator = testApp.Services.GetRequiredKeyedService<IRabbitMediator>("cancellation");

        var response = await mediator.Request(new PatientRequest
        {
            TargetInstance = mediator.GetInstanceInformation(),
        }, responseTimeOut: TimeSpan.FromSeconds(10));

        Assert.True(response.Success);
    }

    /// <summary>
    /// A request that ran out of time while queued behind a busy consumer never reaches that consumer. This
    /// works because the default prefetch window matches the dispatch concurrency, so the backlog waits in the
    /// broker's queue, where the per-message expiry applies. Widen the window and the backlog moves into this
    /// process instead, where nothing can expire it - the client hands a delivery over only once a dispatcher
    /// is free, so the waiting is not observable from here.
    /// </summary>
    [Fact]
    public async Task RequestThatExpiredWhileWaitingForADispatcherNeverReachesTheConsumer()
    {
        BusyRequestConsumer.Invocations = 0;

        using var testApp = await _aspireHostFixture.PrepareEmptyHost(builder =>
            builder.Services.AddRabbitMediator(cfg =>
            {
                cfg.ConnectionString = _aspireHostFixture.RabbitMQConnectionString!;
                cfg.ServiceKey = "busy";
                cfg.ConsumerTypes = [typeof(BusyRequestConsumer)];
                cfg.ConsumerDispatchConcurrency = 1; //one at a time, so the second request has to wait
                //PrefetchCount is left at its default, which follows the dispatch concurrency
            }));
        var mediator = testApp.Services.GetRequiredKeyedService<IRabbitMediator>("busy");
        var target = mediator.GetInstanceInformation();

        //the first occupies the only dispatcher for 3s, the second waits behind it with a 1s timeout
        var first = mediator.TryRequest(new BusyRequest { TargetInstance = target },
            responseTimeOut: TimeSpan.FromSeconds(1));
        var second = mediator.TryRequest(new BusyRequest { TargetInstance = target },
            responseTimeOut: TimeSpan.FromSeconds(1));
        await Task.WhenAll(first, second);

        Assert.True((await first).TimedOut);
        Assert.True((await second).TimedOut);

        await Task.Delay(TimeSpan.FromSeconds(5)); //let the busy consumer finish and the second request be picked up
        Assert.Equal(1, BusyRequestConsumer.Invocations);
    }

    /// <summary>The request carries the timeout the sender used, that is what the receiving side measures.</summary>
    [Fact]
    public async Task RequestCarriesTheSendersTimeout()
    {
        using var testApp = await PrepareHost(typeof(PatientRequestConsumer));
        var mediator = testApp.Services.GetRequiredKeyedService<IRabbitMediator>("cancellation");
        var request = new PatientRequest { TargetInstance = mediator.GetInstanceInformation() };

        await mediator.Request(request, responseTimeOut: TimeSpan.FromSeconds(7));

        Assert.Equal(TimeSpan.FromSeconds(7), request.TimeOut);
    }
}
