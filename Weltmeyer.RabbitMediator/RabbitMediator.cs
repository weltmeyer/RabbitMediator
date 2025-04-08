using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Weltmeyer.RabbitMediator.ConsumerBases;
using Weltmeyer.RabbitMediator.MessageBases;

namespace Weltmeyer.RabbitMediator;

internal class RabbitMediator : IRabbitMediator, IAsyncDisposable, IDisposable
{


    internal RabbitMediator(RabbitMediatorMultiplexer multiplexer)
    {
        _multiplexer = multiplexer;
    }
    public Guid InstanceId => _multiplexer.InstanceId;


    public Task<TResponse> Request<TRequest, TResponse>(TRequest request, TimeSpan? responseTimeOut = null)
        where TRequest : Request<TResponse> where TResponse : Response
    {
        return this._multiplexer.Request<TRequest, TResponse>(this, request,
            responseTimeOut);
    }

    public Task<SendResult> Send<TMessageType>(TMessageType message, bool confirmPublish = true,
        TimeSpan? confirmTimeOut = null) where TMessageType : Message
    {
        return this._multiplexer.Send(this, message, confirmPublish, confirmTimeOut);
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

    private readonly SemaphoreSlim _configureDone = new(0, 1);

 

    public async Task Configure()
    {
        await this._multiplexer.ConfigureRabbitMediator(this);
        _configureDone.Release();
    }

    public bool WaitReady(TimeSpan maxWait)
    {
        return _configureDone.Wait(maxWait);
    }

    public async ValueTask DisposeAsync()
    {
        Disposed = true;

        await _multiplexer.DisposeRabbitMediatorConnection(this);
        await CastAndDispose(_configureDone);

        return;

        static async ValueTask CastAndDispose(IDisposable resource)
        {
            if (resource is IAsyncDisposable resourceAsyncDisposable)
                await resourceAsyncDisposable.DisposeAsync();
            else
                resource.Dispose();
        }
    }

    public void Dispose()
    {
        Task.Run(DisposeAsync).GetAwaiter().GetResult(); //bah! 
    }
}