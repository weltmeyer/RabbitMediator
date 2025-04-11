namespace Weltmeyer.RabbitMediator.Aspire.Tests;

public static class ExtensionMethods
{
    public static IRabbitMediator[] GetAllMediators(this IServiceProvider serviceProvider, AspireHostFixture fixture)
    {
        var allMediators = fixture.MediatorKeys.Select(t =>
            t == null
                ? serviceProvider.GetRequiredService<IRabbitMediator>()
                : serviceProvider.GetRequiredKeyedService<IRabbitMediator>(t)).ToArray();

        return allMediators;
    }
}