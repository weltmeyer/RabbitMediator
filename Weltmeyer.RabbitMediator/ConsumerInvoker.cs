using System.Collections.Concurrent;
using System.Reflection;
using Weltmeyer.RabbitMediator.Contracts.ConsumerBases;
using Weltmeyer.RabbitMediator.Contracts.MessageBases;

namespace Weltmeyer.RabbitMediator;

/// <summary>
/// Calls a consumer's Consume method. Consumers are found via their generic interface, so the call has to go
/// through reflection - but the lookup is cached per consumer/sent-object type pair instead of scanning all
/// methods of the consumer on every single received message.
/// </summary>
internal static class ConsumerInvoker
{
    private static readonly ConcurrentDictionary<(Type consumerType, Type sentObjectType), MethodInfo> ConsumeMethods =
        new();

    private static readonly ConcurrentDictionary<Type, PropertyInfo> TaskResultProperties = new();

    /// <summary>
    /// The <c>Consume</c> overload of <paramref name="consumerType"/> that takes exactly
    /// <paramref name="sentObjectType"/>. A consumer may implement the interface several times, so the
    /// parameter type - not just the method name - decides.
    /// </summary>
    public static MethodInfo GetConsumeMethod(Type consumerType, Type sentObjectType) =>
        ConsumeMethods.GetOrAdd((consumerType, sentObjectType), static key =>
            key.consumerType.GetMethods()
                .Where(m => m.Name == nameof(IMessageConsumer<Message>.Consume))
                .First(m => m.GetParameters() is { Length: 1 } parameters &&
                            parameters[0].ParameterType == key.sentObjectType));

    public static Task Invoke(object consumer, MethodInfo consumeMethod, ISentObject sentObject) =>
        (Task)consumeMethod.Invoke(consumer, [sentObject])!;

    /// <summary>Reads <c>Task&lt;TResponse&gt;.Result</c> off a completed consume task.</summary>
    public static Response GetResponse(Task completedConsumeTask) =>
        (Response)TaskResultProperties
            .GetOrAdd(completedConsumeTask.GetType(), static taskType => taskType.GetProperty("Result")!)
            .GetValue(completedConsumeTask)!;

    /// <summary>The TResponse of a <c>Task&lt;TResponse&gt;</c> returning consume method.</summary>
    public static Type GetResponseType(MethodInfo consumeMethod) =>
        consumeMethod.ReturnType.GenericTypeArguments.First();
}
