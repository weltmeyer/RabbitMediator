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
  
### Usage ###
  With DI Container:

  ```
 builder.Services.AddRabbitMediator(typeof(MyConsumer).Assembly, RabbitMQConnectionString);
```
  Without DI Container(Not working yet, as we need a logger, *todo*):
  ```
var mediator = new RabbitMediator(
            host.Services.GetRequiredService<ILogger<RabbitMediator>>(),
            [typeof(TestTargetedRequestConsumer)],
            _aspireHostFixture.RabbitMQConnectionString!, null, 10);
        mediator.DefaultConfirmTimeOut = TimeSpan.FromSeconds(1);
        mediator.DefaultResponseTimeOut = TimeSpan.FromSeconds(1);
        await mediator.ConfigureBus(host.Services);
```
