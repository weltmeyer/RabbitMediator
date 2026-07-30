RabbitMediator
===

![dotnet](https://github.com/weltmeyer/RabbitMediator/actions/workflows/dotnet.yml/badge.svg)


Basic mediator implementation in .NET using RabbitMQ as a transport

Implements asynchronous messaging and request/response between different hosts and processes.

### Register ###

```csharp
builder.Services.AddRabbitMediator(cfg =>
{
    cfg.ConnectionString = "amqp://guest:guest@localhost:5672";
    cfg.ConsumerAssemblies.Add(typeof(MyConsumer).Assembly);
});
```

Then inject `IRabbitMediator`.

### Consume ###

```csharp
public class MyConsumer : IMessageConsumer<MyMessage>, IRequestConsumer<MyRequest, MyResponse>
{
    public Task Consume(MyMessage message) => Task.CompletedTask;

    public Task<MyResponse> Consume(MyRequest request) => Task.FromResult(new MyResponse());
}
```

Messages derive from `BroadcastMessage` (everyone), `AnyTargetedMessage` (one of the
consumers) or `TargetedMessage` (one named instance), requests from
`AnyTargetedRequest<TResponse>` or `TargetedRequest<TResponse>`.

### Send and request ###

```csharp
SendResult result = await mediator.Send(new MyMessage(), confirmPublish: true);

//throws RabbitMediatorTimeoutException / RabbitMediatorSendFailureException on failure
MyResponse response = await mediator.Request(new MyRequest { TargetInstance = target });

//returns a synthetic response with TimedOut / SendFailure set instead of throwing
MyResponse tried = await mediator.TryRequest(new MyRequest { TargetInstance = target });
```

An exception inside a consumer travels back to the caller as `ExceptionData`.

Full documentation: https://github.com/weltmeyer/RabbitMediator
