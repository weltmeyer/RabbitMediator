using RabbitMQ.Client;

namespace Weltmeyer.RabbitMediator;

/// <summary>
/// A channel used only for publishing, replaced when it is no longer open.
/// </summary>
/// <remarks>
/// A channel-level error - publishing to an exchange that does not exist is the common one - makes the broker
/// close that channel, and the client's automatic recovery does not bring it back: it only covers connection
/// failures. A single publish could therefore leave every later publish of every mediator sharing the
/// connection failing forever. Channels carrying consumers are deliberately not managed here, replacing one
/// would silently drop its subscriptions.
/// </remarks>
internal sealed class PublishChannel(Func<Task<IChannel>> channelFactory) : IAsyncDisposable
{
    private readonly SemaphoreSlim _replaceLock = new(1, 1);
    private IChannel? _channel;

    public async Task<IChannel> GetAsync(CancellationToken cancellationToken = default)
    {
        var current = _channel;
        if (current is { IsOpen: true })
            return current;

        await _replaceLock.WaitAsync(cancellationToken);
        try
        {
            if (_channel is { IsOpen: true })
                return _channel;

            var broken = _channel;
            _channel = await channelFactory();
            if (broken != null)
                await broken.DisposeAsync();
            return _channel;
        }
        finally
        {
            _replaceLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel != null)
            await _channel.DisposeAsync();
        _channel = null;
        _replaceLock.Dispose();
    }
}
