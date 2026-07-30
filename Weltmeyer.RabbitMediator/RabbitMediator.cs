using Weltmeyer.RabbitMediator.Contracts.ConsumerBases;
using Weltmeyer.RabbitMediator.Contracts.MessageBases;


namespace Weltmeyer.RabbitMediator;

internal class RabbitMediator : IRabbitMediator, IAsyncDisposable, IDisposable
{
    private const string IdCharacters = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>
    /// A random 10 character id. Only ever used inside exchange, queue and routing key names, so it has to
    /// stay short and free of AMQP-special characters.
    /// </summary>
    internal static string GenerateId() =>
        string.Create(10, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = IdCharacters[Random.Shared.Next(IdCharacters.Length)];
        });


    internal RabbitMediator(RabbitMediatorMultiplexer multiplexer)
    {
        _multiplexer = multiplexer;
    }
    public string InstanceId => _multiplexer.InstanceId;


    public async Task<TResponse> Request<TRequest, TResponse>(TRequest request, TimeSpan? responseTimeOut = null,
        CancellationToken cancellationToken = default)
        where TRequest : Request<TResponse> where TResponse : Response
    {
        await EnsureConfigured();
        return await this._multiplexer.Request(this, request,
            responseTimeOut, throwOnFailure: true, cancellationToken);
    }

    public async Task<TResponse> Request<TResponse>(Request<TResponse> request, TimeSpan? responseTimeOut = null,
        CancellationToken cancellationToken = default) where TResponse : Response
    {
        await EnsureConfigured();
        return await this._multiplexer.Request(this, request,
            responseTimeOut, throwOnFailure: true, cancellationToken);
    }

    public async Task<TResponse> TryRequest<TRequest, TResponse>(TRequest request, TimeSpan? responseTimeOut = null,
        CancellationToken cancellationToken = default)
        where TRequest : Request<TResponse> where TResponse : Response
    {
        await EnsureConfigured();
        return await this._multiplexer.Request(this, request,
            responseTimeOut, throwOnFailure: false, cancellationToken);
    }

    public async Task<TResponse> TryRequest<TResponse>(Request<TResponse> request, TimeSpan? responseTimeOut = null,
        CancellationToken cancellationToken = default) where TResponse : Response
    {
        await EnsureConfigured();
        return await this._multiplexer.Request(this, request,
            responseTimeOut, throwOnFailure: false, cancellationToken);
    }

    public async Task<SendResult> Send<TMessageType>(TMessageType message, bool confirmPublish = true,
        TimeSpan? confirmTimeOut = null, CancellationToken cancellationToken = default) where TMessageType : Message
    {
        await EnsureConfigured();
        return await this._multiplexer.Send(this, message, confirmPublish, confirmTimeOut, cancellationToken);
    }

    public T? GetConsumerInstance<T>() where T : IConsumer
    {
        var consumer = GetConsumer(typeof(T));
        return (T?)consumer;
    }

    public IConsumer? GetConsumer(Type consumerType)
    {
        return this._multiplexer.GetConsumer(this, consumerType);
    }

    public string ScopeId { get; } = GenerateId();

    private readonly RabbitMediatorMultiplexer _multiplexer;


    internal bool Disposed;

    internal bool ConfigureDone;

    private readonly SemaphoreSlim _configureLock = new(1, 1);
    private readonly ManualResetEventSlim _configureEvent = new(false);
    public async Task EnsureConfigured()
    {
        if (ConfigureDone)
            return;//we are done configuring
        await _configureLock.WaitAsync();
        try
        {
            if (ConfigureDone)
                return; //we are done configuring
            await this._multiplexer.ConfigureRabbitMediator(this);
            ConfigureDone = true;
            _configureEvent.Set();
        }
        finally
        {
            _configureLock.Release();
        }        
    }

    public bool WaitReady(TimeSpan maxWait)
    {
        return _configureEvent.Wait(maxWait);
    }

    public async ValueTask DisposeAsync()
    {
        Disposed = true;

        await _multiplexer.DisposeRabbitMediatorConnection(this);
    }

    public void Dispose()
    {
        // Task.Run to get off any SynchronizationContext, and AsTask so the ValueTask is actually awaited -
        // Task.Run(DisposeAsync) binds to Task.Run(Func<TResult>) and hands back a Task<ValueTask> whose inner
        // ValueTask nobody ever awaited, which made this method return before the teardown had happened.
        Task.Run(() => DisposeAsync().AsTask()).GetAwaiter().GetResult();
    }
}