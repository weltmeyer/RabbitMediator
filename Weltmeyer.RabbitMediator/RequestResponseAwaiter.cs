using Weltmeyer.RabbitMediator.Contracts.MessageBases;

namespace Weltmeyer.RabbitMediator;

internal class RequestResponseAwaiter(Guid CorrelationId)
{
    public Guid CorrelationId { get; } = CorrelationId;
    public readonly TaskCompletionSource TaskCompletionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Response? Result;
}

internal class TargetAckAwaiter(Guid CorrelationId)
{
    public Guid CorrelationId { get; } = CorrelationId;
    public readonly TaskCompletionSource<SentObjectAck> TaskCompletionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

}