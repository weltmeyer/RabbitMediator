using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Weltmeyer.RabbitMediator.Contracts.ConsumerBases;
using Weltmeyer.RabbitMediator.Contracts.MessageBases;


namespace Weltmeyer.RabbitMediator;

internal class RabbitMediator : IRabbitMediator, IAsyncDisposable, IDisposable
{


    internal RabbitMediator(RabbitMediatorMultiplexer multiplexer)
    {
        _multiplexer = multiplexer;
    }
    public Guid InstanceId => _multiplexer.InstanceId;


    public async Task<TResponse> Request<TRequest, TResponse>(TRequest request, TimeSpan? responseTimeOut = null)
        where TRequest : Request<TResponse> where TResponse : Response
    {
        await EnsureConfigured();
        return await this._multiplexer.Request<TRequest, TResponse>(this, request,
            responseTimeOut);
    }

    public async Task<SendResult> Send<TMessageType>(TMessageType message, bool confirmPublish = true,
        TimeSpan? confirmTimeOut = null) where TMessageType : Message
    {
        await EnsureConfigured();
        return await this._multiplexer.Send(this, message, confirmPublish, confirmTimeOut);
    }

    public T? GetConsumerInstance<T>() where T : IConsumer
    {
        //var consumer = this._consumers.Values.FirstOrDefault(k => k.GetType() == typeof(T));
        var consumer = GetConsumer(typeof(T));
        return (T?)consumer;
    }


    private readonly ConcurrentDictionary<string, IConsumer> _consumers = new();

    public IConsumer? GetConsumer(Type consumerType)
    {
        return this._multiplexer.GetConsumer(this,consumerType);
    }

    public Guid ScopeId { get; } = Guid.NewGuid();



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
        

        return;

      
    }

    public void Dispose()
    {
        Task.Run(DisposeAsync).GetAwaiter().GetResult(); //bah! 
    }
}