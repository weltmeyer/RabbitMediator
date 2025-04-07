using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Weltmeyer.RabbitMediator.ConsumerBases;
using Weltmeyer.RabbitMediator.MessageBases;

namespace Weltmeyer.RabbitMediator;

internal class MultiplexedRabbitMediator : IRabbitMediator, IAsyncDisposable,IDisposable
{
    public readonly IServiceProvider ServiceProvider;
    
    public Guid InstanceId => _multiplexer.InstanceId;

    public TimeSpan DefaultConfirmTimeOut { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan DefaultResponseTimeOut { get; set; } = TimeSpan.FromSeconds(10);

    public Task<TResponse> Request<TRequest, TResponse>(TRequest request, TimeSpan? responseTimeOut = null)
        where TRequest : Request<TResponse> where TResponse : Response
    {
        return this._multiplexer.Request<TRequest, TResponse>(this, request,
            responseTimeOut ?? this.DefaultResponseTimeOut);
    }

    public Task<SendResult> Send<TMessageType>(TMessageType message, bool confirmPublish = true,
        TimeSpan? confirmTimeOut = null) where TMessageType : Message
    {
        return this._multiplexer.Send(this, message, confirmPublish, confirmTimeOut ?? this.DefaultConfirmTimeOut);
    }

    public T? GetRequestConsumerInstance<T>() where T : IConsumer
    {
        //var consumer = this._consumers.Values.FirstOrDefault(k => k.GetType() == typeof(T));
        var consumer = GetConsumer(typeof(T));
        return (T?)consumer;
    }

    public T? GetMessageConsumerInstance<T>() where T : IConsumer
    {
        //var consumer = this._consumers.Values.FirstOrDefault(k => k.GetType() == typeof(T));
        var consumer = GetConsumer(typeof(T));
        return (T?)consumer;
    }

    public MultiplexedRabbitMediator(IServiceProvider serviceProvider, Type[] consumerTypes,
        RabbitMediatorMultiplexer multiplexer)
    {
        ServiceProvider = serviceProvider;
        ConsumerTypes = consumerTypes;
        _multiplexer = multiplexer;
    }

    private readonly ConcurrentDictionary<string, IConsumer> _consumers = new();

    public IConsumer? GetConsumer(Type consumerType)
    {
        if (!consumerType.IsAssignableTo(typeof(IConsumer)))
            throw new ArgumentException($"The type {consumerType.FullName} does not implement {nameof(IConsumer)}");
        if (!this.ConsumerTypes.Contains(consumerType))
            return null;
        return _consumers.GetOrAdd(consumerType.FullName!, static (_, serviceProviderAndType) =>
            (IConsumer)ActivatorUtilities.CreateInstance(serviceProviderAndType.ServiceProvider,
                serviceProviderAndType.consumerType), (ServiceProvider, consumerType));
    }

    public Guid ScopeId { get; } = Guid.NewGuid();

    internal readonly Dictionary<string, AsyncEventingBasicConsumer> RabbitMQConsumers = new();
    internal readonly Dictionary<string, IChannel> ConsumerTags = new();

    internal readonly SemaphoreSlim EnsureReceiverSemaphore = new(1, 1);

    internal readonly ConcurrentDictionary<Type, Type> SentTypeToConsumerMapping = new();
    internal readonly ConcurrentDictionary<string, IChannel> OwnedQueues = new();

    internal readonly Type[] ConsumerTypes;
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

        await _multiplexer.RemoveQueues(this);
        RabbitMQConsumers.Clear();
        SentTypeToConsumerMapping.Clear();

        await CastAndDispose(EnsureReceiverSemaphore);
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
        Task.Run(DisposeAsync).GetAwaiter().GetResult();//bah! 
    }
}