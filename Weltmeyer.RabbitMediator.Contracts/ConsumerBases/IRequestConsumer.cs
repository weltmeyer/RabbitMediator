using Weltmeyer.RabbitMediator.Contracts.MessageBases;

namespace Weltmeyer.RabbitMediator.Contracts.ConsumerBases;

public interface IRequestConsumer<in TRequest, TResponse> : IConsumer
    where TResponse : Response
    where TRequest : Request<TResponse>
{
    public Task<TResponse> Consume(TRequest message);
}

/*
public interface IRequestConsumer2<TResponse> : IConsumer

    where TResponse : Response
{
    public Task<Response> Consume( Request<TResponse> message);
}*/