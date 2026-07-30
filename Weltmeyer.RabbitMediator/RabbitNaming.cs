namespace Weltmeyer.RabbitMediator;

/// <summary>
/// Every exchange, queue, routing key and consumer tag name the mediator puts on the wire.
/// Kept in one place because the same string used to be assembled in several methods - a targeted
/// routing key was built in four of them, and one of those getting it wrong silently broke every
/// targeted delivery after a connection recovery.
/// </summary>
internal static class RabbitNaming
{
    public const string KeySeparator = "::";

    private const string TargetedExchangeName = "TargetedExchange";
    private const string BroadCastExchangeName = "BroadCastExchange";
    private const string AnyTargetedExchangeName = "AnyTargetedExchange";

    /// <summary>Routing key of a fanout (broadcast) exchange. Fanout ignores it, it is only there to be non-empty.</summary>
    public const string BroadcastRoutingKey = "broadcast";

    /// <summary>
    /// Routing key of the shared queue behind an any-targeted exchange. Same literal as
    /// <see cref="BroadcastRoutingKey"/> - kept as its own constant because the two describe unrelated
    /// topologies (competing consumers on one direct-bound queue vs. fanout) and only happen to agree.
    /// </summary>
    public const string AnyTargetedRoutingKey = "broadcast";

    private const string SharedQueuePrefix = "shared";

    public const string InputQueuePrefixMessage = "inputMessage";
    public const string InputQueuePrefixRequest = "inputRequest";
    public const string InputQueuePrefixResponse = "inputResponse";

    public const string AckQueuePrefix = "ackqueue";

    /// <summary>
    /// The type name used inside exchange and queue names. Falls back to stripping the namespace when the
    /// full name would exceed 100 characters, and gives up beyond that.
    /// </summary>
    public static string TypeName(Type type)
    {
        var useName = type.FullName!;

        if (useName.Length > 100)
        {
            //strip namespace
            var ns = type.Namespace!;
            useName = useName.Replace(ns, "ns");
        }

        if (useName.Length > 100)
            throw new ArgumentException($"Type name too long: {type.FullName!}");

        return useName;
    }

    public static string TargetedExchange(string typeName) => TargetedExchangeName + KeySeparator + typeName;

    public static string BroadcastExchange(string typeName) => BroadCastExchangeName + KeySeparator + typeName;

    public static string AnyTargetedExchange(string typeName) => AnyTargetedExchangeName + KeySeparator + typeName;

    /// <summary>Routing key that addresses exactly one mediator (one scope of one multiplexer instance).</summary>
    public static string InstanceRoutingKey(string instanceId, string scopeId) => instanceId + "_" + scopeId;

    /// <summary>Queue owned by a single mediator, one per sent-object type.</summary>
    public static string InputQueue(string queuePrefix, string typeName, string instanceId, string scopeId) =>
        $"{queuePrefix}{KeySeparator}{typeName}{KeySeparator}{instanceId}{KeySeparator}{scopeId}";

    /// <summary>Queue shared by all mediators consuming an any-targeted type, so they compete for messages.</summary>
    public static string SharedQueue(string typeName) => $"{SharedQueuePrefix}{KeySeparator}{typeName}";

    public static string AckQueue(string instanceId) => $"{AckQueuePrefix}{KeySeparator}{instanceId}";

    public static string ConsumerTag(string exchangeName, string instanceId, string scopeId) =>
        exchangeName + KeySeparator + instanceId + KeySeparator + scopeId;
}
