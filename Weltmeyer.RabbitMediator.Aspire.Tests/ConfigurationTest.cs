using Microsoft.Extensions.Logging;
using Weltmeyer.RabbitMediator.TestTool.Consumers;

namespace Weltmeyer.RabbitMediator.Aspire.Tests;

[Collection("AspireHostCollection")]
public class ConfigurationTest
{
    private readonly AspireHostFixture _aspireHostFixture;


    public ConfigurationTest(AspireHostFixture aspireHostFixture)
    {
        _aspireHostFixture = aspireHostFixture;
    }
/*
    [Fact]
    async Task RecoverConnection()
    {
        using var host = await _aspireHostFixture.PrepareHost();

        var connectionFactory = new RabbitMQ.Client.ConnectionFactory
        {
            Uri = new Uri(_aspireHostFixture.RabbitMQConnectionString!),
            AutomaticRecoveryEnabled = true,
        };

        var connection = await connectionFactory.CreateConnectionAsync();
        
        
        var mediator = new RabbitMediator(
            host.Services.GetRequiredService<ILogger<RabbitMediator>>(),
            [typeof(TestBroadCastMessageConsumer)],
            _aspireHostFixture.RabbitMQConnectionString!, null, 10);
        mediator.DefaultConfirmTimeOut = TimeSpan.FromSeconds(1);
        mediator.DefaultResponseTimeOut = TimeSpan.FromSeconds(1);
        await mediator.ConfigureBus(host.Services,connection);

        var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true,true));
        
        await Assert.ThrowsAsync<RabbitMQ.Client.Exceptions.AlreadyClosedException>(async () =>
        {
            //this crashes the channel
            await channel.BasicPublishAsync(
                "NonExistingExchange",
                "StrangeRoutingKey",
                true,
                new BasicProperties(),
                Array.Empty<byte>().AsMemory(),
                CancellationToken.None);
            
        });

        await mediator.Send(new TestBroadcastMessage());
        var received = mediator.GetMessageConsumerInstance<TestBroadCastMessageConsumer>()!;
        Assert.Equal(1,received.ReceivedMessages);
        
        
        await mediator.DisposeAsync();
    }
    */
    [Fact]
    async Task ConfigureBusCustomConnection()
    {
        using var host = await _aspireHostFixture.PrepareHost();

        var connectionFactory = new RabbitMQ.Client.ConnectionFactory
        {
            Uri = new Uri(_aspireHostFixture.RabbitMQConnectionString!),
            AutomaticRecoveryEnabled = true,
        };

        var connection = await connectionFactory.CreateConnectionAsync();
        
        
        var mediator = new RabbitMediator(
            host.Services.GetRequiredService<ILogger<RabbitMediator>>(),
            [typeof(TestTargetedRequestConsumer)],
            _aspireHostFixture.RabbitMQConnectionString!, null, 10);
        mediator.DefaultConfirmTimeOut = TimeSpan.FromSeconds(1);
        mediator.DefaultResponseTimeOut = TimeSpan.FromSeconds(1);
        await mediator.ConfigureBus(host.Services,connection);
        await mediator.DisposeAsync();
    }
 
    [Fact]
    async Task ConfigureBusGood()
    {
        using var host = await _aspireHostFixture.PrepareHost();

        var mediator = new RabbitMediator(
            host.Services.GetRequiredService<ILogger<RabbitMediator>>(),
            [typeof(TestTargetedRequestConsumer)],
            _aspireHostFixture.RabbitMQConnectionString!, null, 10);
        mediator.DefaultConfirmTimeOut = TimeSpan.FromSeconds(1);
        mediator.DefaultResponseTimeOut = TimeSpan.FromSeconds(1);
        await mediator.ConfigureBus(host.Services);

        await mediator.DisposeAsync();
    }

    [Fact]
    async Task ConfigureBusBadConnectionString()
    {
        using var host = await _aspireHostFixture.PrepareHost();


        Assert.Throws<UriFormatException>(() =>
        {
            _ = new RabbitMediator(
                host.Services.GetRequiredService<ILogger<RabbitMediator>>(),
                [typeof(TestTargetedRequestConsumer)],
                "notAnUrl", null, 10);
        });
    }

    [Fact]
    async Task ConfigureBusServerUnreach()
    {
        using var host = await _aspireHostFixture.PrepareHost();

        var mediator = new RabbitMediator(
            host.Services.GetRequiredService<ILogger<RabbitMediator>>(),
            [typeof(TestTargetedRequestConsumer)],
            "amqp://server", null, 10);
        await Assert.ThrowsAsync<RabbitMQ.Client.Exceptions.BrokerUnreachableException>(async () =>
        {
            await mediator.ConfigureBus(host.Services);
        });
        
        await mediator.DisposeAsync();
    }
}