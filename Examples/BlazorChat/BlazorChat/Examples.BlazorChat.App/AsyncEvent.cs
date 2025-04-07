namespace Examples.BlazorChat.App;

public static class AsyncEvent
{
    public delegate Task AsyncEventHandler<TEventArgs>(object? sender, TEventArgs e);
}