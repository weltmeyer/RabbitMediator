RabbitMediator
===

![dotnet](https://github.com/weltmeyer/RabbitMediator/actions/workflows/dotnet.yml/badge.svg)
[![NuGet Version](https://img.shields.io/nuget/dt/Weltmeyer.RabbitMediator)](https://www.nuget.org/packages/Weltmeyer.RabbitMediator) 
[![NuGet](https://img.shields.io/nuget/vpre/Weltmeyer.RabbitMediator)](https://www.nuget.org/packages/Weltmeyer.RabbitMediator)

Basic mediator implementation in .NET using RabbitMQ as a transport

Implements asynchronous messaging and request/response between different hosts and processes.


### Install ###
via terminal:

    dotnet add package Weltmeyer.RabbitMediator
  
or use your IDE, search for Weltmeyer.RabbitMediator and press install

### Register ###

```csharp
builder.Services.AddRabbitMediator(cfg =>
{
    cfg.ConnectionString = "amqp://guest:guest@localhost:5672";
    cfg.ConsumerAssemblies.Add(typeof(MyConsumer).Assembly); //or cfg.ConsumerTypes = [typeof(MyConsumer)];
});
```

Inject `IRabbitMediator` wherever you need it. Register it several times with different
`cfg.ServiceKey` values to talk to several brokers, and resolve those with
`GetRequiredKeyedService<IRabbitMediator>(key)`.

### Message kinds ###

| Base class | Delivered to | Answer |
|---|---|---|
| `BroadcastMessage` | every instance consuming that type | none |
| `AnyTargetedMessage` | exactly one of the instances consuming that type | none |
| `TargetedMessage` | the instance named in `TargetInstance` | none |
| `AnyTargetedRequest<TResponse>` | exactly one of the instances consuming that type | `TResponse` |
| `TargetedRequest<TResponse>` | the instance named in `TargetInstance` | `TResponse` |

Address a targeted message or request with the receiver's
`IRabbitMediator.GetInstanceInformation()`.

### Consume ###

```csharp
public class MyConsumer : IMessageConsumer<MyMessage>, IRequestConsumer<MyRequest, MyResponse>
{
    public Task Consume(MyMessage message) => Task.CompletedTask;

    public Task<MyResponse> Consume(MyRequest request) => Task.FromResult(new MyResponse());
}
```

Consumers are created through the service provider, so they can take dependencies.
Only one consumer per message type is allowed; registering two throws at startup.

`Response.Success` and `Response.ExceptionData` describe whether the request could be
answered at all, not what the answer says. They belong to the mediator and a consumer
cannot set them: an exception thrown inside a consumer arrives as `ExceptionData` with
`Success == false` (as `SendResult.ExceptionData` for a message sent with
`confirmPublish: true`). A business "no" is yours to model - put it in a property of your
own response type:

```csharp
public class TakeSnapshotResponse : Response
{
    public bool CameraReady { get; set; }
    public string? Snapshot { get; set; }
}
```

Check `Success` before reading those properties. Whenever the mediator has to invent a
response - a consumer threw, nothing consumes the type, or `TryRequest` ran into a timeout
or a failed publish - it creates an empty instance of your response type, so everything you
declared on it is at its default and means nothing.

### Send and request ###

```csharp
//fire and forget, or wait for a consumer to acknowledge it
SendResult result = await mediator.Send(new MyMessage(), confirmPublish: true);

//request/response, throws on timeout or if the request cannot be routed
MyResponse response = await mediator.Request(new MyRequest { TargetInstance = target });

//same, but hands back a synthetic response with TimedOut / SendFailure set instead
MyResponse tried = await mediator.TryRequest(new MyRequest { TargetInstance = target });
```

`Request` throws `RabbitMediatorTimeoutException` when no response arrives in time and
`RabbitMediatorSendFailureException` when the request cannot be published or routed.
`TryRequest` returns instead - inspect `Response.TimedOut` and `Response.SendFailure`.
Both of them, and `Send`, take a `CancellationToken`; cancelling always throws.

### Configuration ###

| Setting | Default | Meaning |
|---|---|---|
| `ConnectionString` | required | AMQP URI of the broker |
| `ConsumerTypes` / `ConsumerAssemblies` | empty | where to look for consumers |
| `ServiceKey` | `null` | DI service key, one mediator and connection per key |
| `ServiceLifetime` | `Singleton` | `Scoped` gives every scope its own mediator |
| `DefaultResponseTimeOut` | 10 s | how long `Request` waits for a response |
| `DefaultConfirmTimeOut` | 10 s | how long `Send(confirmPublish: true)` waits for the ack |
| `ConsumerDispatchConcurrency` | 10 | parallel consumer dispatch per receive channel |
| `PrefetchCount` | 100 | unacknowledged messages the broker hands out per receive channel, 0 for unlimited |
| `WaitReadyTimeOut` | 10 s | how long resolving the mediator waits for its topology |

`ConsumerDispatchConcurrency` and `PrefetchCount` apply to the connection, so the first
mediator registered for a service key decides them for everyone sharing that connection.

### Telemetry ###

```csharp
builder.Services.AddRabbitMediatorTelemetry();
```

Adds the `Weltmeyer.RabbitMediator` activity source to OpenTelemetry tracing. Traces are
propagated across instances, so a request and the work its consumer does end up in one trace.

### Examples ###

`Examples/BlazorChat` - a chat with broadcast and targeted messages between instances.
