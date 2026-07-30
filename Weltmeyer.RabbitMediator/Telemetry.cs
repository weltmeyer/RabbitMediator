using System.Diagnostics;
using System.Reflection;

namespace Weltmeyer.RabbitMediator;

internal static class Telemetry
{
    /// <summary>
    /// Named after this library, not after the hosting application: Assembly.GetEntryAssembly() is null in
    /// several hosts (test runners, native AOT, unmanaged callers), which used to make the static initializer
    /// throw, and a source named after the consumer is not something a consumer can subscribe to by name.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(
        typeof(Telemetry).Assembly.GetName().Name!,
        typeof(Telemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
}
