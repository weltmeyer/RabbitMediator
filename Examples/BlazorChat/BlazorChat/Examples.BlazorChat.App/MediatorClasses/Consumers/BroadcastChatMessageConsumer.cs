using Examples.BlazorChat.App.MediatorClasses.Messages;

namespace Examples.BlazorChat.App.MediatorClasses.Consumers;

public class
    BroadcastChatMessageConsumer(AppState appState, UserState userState)
    : Weltmeyer.RabbitMediator.Contracts.ConsumerBases.IMessageConsumer<BroadcastChatMessage>
{
    public async Task Consume(BroadcastChatMessage message)
    {
        await appState.AddUserIfUnknown(message.SenderName, message.SenderInstance);
        await userState.AddMessage(message.Message, message.SenderInstance);
    }
}