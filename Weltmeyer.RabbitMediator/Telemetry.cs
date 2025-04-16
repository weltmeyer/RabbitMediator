using System.Diagnostics;
using System.Reflection;

namespace Weltmeyer.RabbitMediator;

internal static class Telemetry
{
    public static readonly ActivitySource ActivitySource = new ActivitySource(Assembly.GetEntryAssembly().FullName);
    //public static readonly ActivitySource ActivitySourceMediator = new ActivitySource("RabbitMediator");
}