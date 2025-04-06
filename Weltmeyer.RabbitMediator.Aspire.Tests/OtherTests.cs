using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weltmeyer.RabbitMediator.MessageBases;
using Weltmeyer.RabbitMediator.TestTool.Consumers;
using Weltmeyer.RabbitMediator.TestTool.Messages;

namespace Weltmeyer.RabbitMediator.Aspire.Tests;

public class OtherTests
{
    [Fact]
    void TestErrorOnBadConsumerType()
    {
        var builder = Host.CreateApplicationBuilder();

        Assert.Throws<InvalidCastException>(() =>
        {
            builder.Services.AddRabbitMediator(
                consumerTypes:
                [typeof(int), typeof(TestTargetedMessageConsumer), typeof(TestTargetedRequestConsumer)],
                connectionString: "unNeeded");
        });
    }


    [Fact]
    async Task TestSerializationHelper()
    {
        var helper = new JsonSerializerHelper();
        var testTypes = new[] { typeof(TestTargetedRequest), typeof(TestTargetedResponse) };

        foreach (var testType in testTypes)
        {
            var typeInstance = Activator.CreateInstance(testType)!;
            await helper.Serialize(typeInstance, async data =>
            {
                var jsonString = Encoding.UTF8.GetString(data.Span);
                Assert.Contains(testType.FullName!, jsonString);

                var deserialized = await helper.Deserialize<ISentObject>(data);
                Assert.IsType(testType, deserialized);
            });
        }
    }

    
}