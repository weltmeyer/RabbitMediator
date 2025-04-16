using System.Diagnostics;
using Weltmeyer.RabbitMediator.Contracts.MessageBases;

namespace Weltmeyer.RabbitMediator;

internal static class ActivityEnricher
{
    public static void Enrich(this Activity activity, ISentObject sentObject)
    {
        activity.SetTag("SentObject.CorrelationId", sentObject.CorrelationId.ToString());
        activity.SetTag("SentObject.TypeName", sentObject.GetType().Name);
        activity.SetTag("SentObject.SenderInstance", sentObject.SenderInstance);
        if (sentObject is ITargetedSentObject targetedSentObject)
        {
            activity.SetTag("SentObject.TargetInstance", targetedSentObject.TargetInstance);
        }

    }
}