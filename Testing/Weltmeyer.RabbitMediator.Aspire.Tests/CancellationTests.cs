using System.Diagnostics;
using Weltmeyer.RabbitMediator.TestTool.Messages;

namespace Weltmeyer.RabbitMediator.Aspire.Tests;

[Collection("AspireHostCollection")]
public class CancellationTests
{
    private readonly AspireHostFixture _aspireHostFixture;

    public CancellationTests(AspireHostFixture aspireHostFixture)
    {
        _aspireHostFixture = aspireHostFixture;
    }

    [Fact]
    public async Task Request_CancelledWhileWaiting_ThrowsWithoutRunningIntoTheTimeout()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);
        var requester = allMediators.First();
        var responder = allMediators.Skip(1).First();

        using var cancellation = new CancellationTokenSource();
        var pending = requester.Request(new TestTargetedRequest
        {
            TargetInstance = responder.GetInstanceInformation(),
            Delay = TimeSpan.FromSeconds(10),
        }, responseTimeOut: TimeSpan.FromSeconds(30), cancellationToken: cancellation.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        var stopwatch = Stopwatch.StartNew();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"cancelling took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Request_AlreadyCancelledToken_ThrowsBeforePublishing()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await allMediators.First().Request(new TestTargetedRequest
            {
                TargetInstance = allMediators.Skip(1).First().GetInstanceInformation(),
            }, cancellationToken: cancellation.Token));
    }

    /// <summary>TryRequest swallows timeouts and publish failures - a cancellation is neither.</summary>
    [Fact]
    public async Task TryRequest_CancelledWhileWaiting_StillThrows()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var pending = allMediators.First().TryRequest(new TestTargetedRequest
        {
            TargetInstance = allMediators.Skip(1).First().GetInstanceInformation(),
            Delay = TimeSpan.FromSeconds(10),
        }, responseTimeOut: TimeSpan.FromSeconds(30), cancellationToken: cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }

    [Fact]
    public async Task Send_CancelledWhileWaitingForTheAck_Throws()
    {
        using var testApp = await _aspireHostFixture.PrepareHost();
        var allMediators = testApp.Services.GetAllMediators(_aspireHostFixture);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var pending = allMediators.First().Send(new TestTargetedMessage
        {
            TargetInstance = allMediators.Skip(1).First().GetInstanceInformation(),
            Delay = TimeSpan.FromSeconds(10),
        }, confirmTimeOut: TimeSpan.FromSeconds(30), cancellationToken: cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }
}
