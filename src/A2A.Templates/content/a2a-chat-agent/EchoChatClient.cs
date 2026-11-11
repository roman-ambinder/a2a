using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace A2AChatAgent1;

/// <summary>A stand-in for a real model: it repeats the last user message, word by word. Replace it in Program.cs.</summary>
internal sealed class EchoChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Answer(messages))));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var words = Answer(messages).Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, (i == 0 ? "" : " ") + words[i]);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private static string Answer(IEnumerable<ChatMessage> messages) =>
        $"You said: {messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text}";
}
