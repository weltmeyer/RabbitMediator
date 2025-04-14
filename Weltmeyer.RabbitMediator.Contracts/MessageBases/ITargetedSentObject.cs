using Weltmeyer.RabbitMediator.Contracts.Contracts;

namespace Weltmeyer.RabbitMediator.Contracts.MessageBases;

internal interface ITargetedSentObject : ISentObject
{
    public InstanceInformation TargetInstance { get; }
  
}