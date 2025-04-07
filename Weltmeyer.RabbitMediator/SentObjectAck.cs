using Weltmeyer.RabbitMediator.Contracts;

namespace Weltmeyer.RabbitMediator;

internal class SentObjectAck
{
    public Guid CorrelationId { get; set; }
    public bool Success { get; set; }
    
    public ExceptionData? ExceptionData { get; set; }
    
    public Guid Target { get; set; }
}