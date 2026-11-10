using A2A.Client;
using A2A.Client.Outbox;
using A2A.Client.Push;

namespace Snippets.ClientOutbox;

public static class Samples
{
    #region Webhook
    public static Task OnWebhookAsync(ClientOutboxDispatcher dispatcher, PushDelivery delivery) =>
        dispatcher.NotifyAsync("billing-agent", delivery);   // a hint: the dispatcher still asks GetTask
    #endregion

    #region InMemory
    public static ClientOutboxStore ForTests() => new InMemoryClientOutboxStore();
    #endregion
}
