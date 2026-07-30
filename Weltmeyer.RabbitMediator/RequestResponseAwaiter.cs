using Weltmeyer.RabbitMediator.Contracts.MessageBases;

namespace Weltmeyer.RabbitMediator;

/// <summary>A request waiting for its response. <see cref="Owner"/> is the mediator that sent it.</summary>
internal class RequestResponseAwaiter(Guid correlationId, RabbitMediator owner)
{
    public Guid CorrelationId { get; } = correlationId;
    public RabbitMediator Owner { get; } = owner;

    public readonly TaskCompletionSource TaskCompletionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Response? Result;
}

/// <summary>A sent message waiting for the ack of its consumer.</summary>
internal class TargetAckAwaiter(Guid correlationId, RabbitMediator owner)
{
    public Guid CorrelationId { get; } = correlationId;
    public RabbitMediator Owner { get; } = owner;

    public readonly TaskCompletionSource<SentObjectAck> TaskCompletionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
