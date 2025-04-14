using System.Text;
using Microsoft.Extensions.Hosting;
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

        Assert.Throws<ArgumentException>(() =>
        {
            builder.Services.AddRabbitMediator(cfg =>
            {
                cfg.ConsumerTypes =
                    [typeof(int), typeof(TestTargetedMessageConsumer), typeof(TestTargetedRequestConsumer)];
                cfg.ConnectionString = "unNeeded";
            });
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