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

    private static readonly ConcurrentDictionary<(Type consumerType, Type requestType), MethodInfo>
        RequestConsumeMethods = new();

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

    /// <summary>
    /// The cancellable <c>Consume</c> overload for a request, taken off the interface rather than off the
    /// consumer class: it has a default implementation, and a class that does not override it has no such
    /// method of its own. Invoking the interface method dispatches to the override when there is one and to
    /// the default when there is not.
    /// </summary>
    public static MethodInfo GetRequestConsumeMethod(Type consumerType, Type requestType) =>
        RequestConsumeMethods.GetOrAdd((consumerType, requestType), static key =>
        {
            var consumerInterface = key.consumerType.GetInterfaces().First(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestConsumer<,>) &&
                i.GetGenericArguments()[0] == key.requestType);

            return consumerInterface.GetMethod(nameof(IRequestConsumer<Request<Response>, Response>.Consume),
                [key.requestType, typeof(CancellationToken)])!;
        });

    public static Task Invoke(object consumer, MethodInfo consumeMethod, ISentObject sentObject) =>
        (Task)consumeMethod.Invoke(consumer, [sentObject])!;

    public static Task Invoke(object consumer, MethodInfo consumeMethod, ISentObject sentObject,
        CancellationToken cancellationToken) =>
        (Task)consumeMethod.Invoke(consumer, [sentObject, cancellationToken])!;

    /// <summary>Reads <c>Task&lt;TResponse&gt;.Result</c> off a completed consume task.</summary>
    public static Response GetResponse(Task completedConsumeTask) =>
        (Response)TaskResultProperties
            .GetOrAdd(completedConsumeTask.GetType(), static taskType => taskType.GetProperty("Result")!)
            .GetValue(completedConsumeTask)!;

    /// <summary>The TResponse of a <c>Task&lt;TResponse&gt;</c> returning consume method.</summary>
    public static Type GetResponseType(MethodInfo consumeMethod) =>
        consumeMethod.ReturnType.GenericTypeArguments.First();
}
