namespace Weltmeyer.RabbitMediator;

/// <summary>
/// Base type for all failures surfaced by <see cref="IRabbitMediator.Request{TResponse}"/>.
/// The throwing <c>Request</c> overloads raise a derived exception instead of returning a
/// synthetic <see cref="Contracts.MessageBases.Response"/>. Use the <c>TryRequest</c> overloads
/// when the caller wants to inspect <see cref="Contracts.MessageBases.Response.TimedOut"/> /
/// <see cref="Contracts.MessageBases.Response.SendFailure"/> instead.
/// </summary>
public class RabbitMediatorException : Exception
{
    /// <summary>The CLR type of the request that failed.</summary>
    public Type RequestType { get; }

    /// <summary>The correlation id assigned to the failed request.</summary>
    public Guid CorrelationId { get; }

    public RabbitMediatorException(string message, Type requestType, Guid correlationId, Exception? innerException = null)
        : base(message, innerException)
    {
        RequestType = requestType;
        CorrelationId = correlationId;
    }
}

/// <summary>
/// Thrown by the throwing <c>Request</c> overloads when no response arrived within the timeout.
/// Previously this was silently returned as a synthetic <c>Response{ TimedOut = true }</c> with all
/// other fields at their default values.
/// </summary>
public sealed class RabbitMediatorTimeoutException : RabbitMediatorException
{
    /// <summary>The timeout that elapsed before giving up.</summary>
    public TimeSpan Timeout { get; }

    public RabbitMediatorTimeoutException(Type requestType, Guid correlationId, TimeSpan timeout)
        : base(
            $"Request '{requestType.Name}' (correlation {correlationId}) timed out after {timeout.TotalMilliseconds:0}ms with no response.",
            requestType, correlationId)
    {
        Timeout = timeout;
    }
}

/// <summary>
/// Thrown by the throwing <c>Request</c> overloads when the request could not be published or routed
/// to a consumer (e.g. the target exchange/queue does not exist, or the broker rejected the publish).
/// Previously this was silently returned as a synthetic <c>Response{ SendFailure = true }</c>.
/// </summary>
public sealed class RabbitMediatorSendFailureException : RabbitMediatorException
{
    public RabbitMediatorSendFailureException(Type requestType, Guid correlationId, Exception? innerException = null)
        : base(
            $"Request '{requestType.Name}' (correlation {correlationId}) could not be published or routed to a consumer.",
            requestType, correlationId, innerException)
    {
    }
}
