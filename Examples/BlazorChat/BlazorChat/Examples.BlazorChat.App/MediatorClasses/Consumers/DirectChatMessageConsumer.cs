using Examples.BlazorChat.App.MediatorClasses.Messages;
using Weltmeyer.RabbitMediator.Contracts.ConsumerBases;

namespace Examples.BlazorChat.App.MediatorClasses.Consumers;

public class DirectChatMessageConsumer(AppState appState, UserState userState) : IMessageConsumer<DirectChatMessage>
{
    public async Task Consume(DirectChatMessage message)
    {
        await appState.AddUserIfUnknown(message.SenderName, message.SenderInstance);
        await userState.AddMessage(message.Message, message.SenderInstance);
    }
}