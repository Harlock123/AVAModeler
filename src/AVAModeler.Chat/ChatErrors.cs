using System.Net;
using Anthropic.Exceptions;

namespace AVAModeler.Chat;

/// <summary>
/// What went wrong with a request, as one plain line for the chat. The API key never appears: the lines are written
/// here, and only the API's own short error message is quoted (it never holds the key).
/// </summary>
public static class ChatErrors
{
    /// <summary>The line to show. Anything at all is described, so a failed request never takes the app down.</summary>
    public static string Describe(Exception e) => e switch
    {
        AnthropicUnauthorizedException => "The Anthropic API didn't accept the key (401). Check it with the Key… button, or the ANTHROPIC_API_KEY environment variable, then Retry.",
        AnthropicForbiddenException => "The Anthropic API refused the request (403): the key may not have access to this model. Try another model, or check the account, then Retry.",
        AnthropicRateLimitException => "The Anthropic API is limiting requests (429): too many, or too many tokens, for now. Wait a minute, then Retry.",
        AnthropicApiException api when (int?)Status(api) == 529 => "Claude is overloaded just now (529). Wait a little, then Retry.",
        AnthropicSseException sse when sse.Message.Contains("overloaded", StringComparison.OrdinalIgnoreCase) => "Claude is overloaded just now. Wait a little, then Retry.",
        AnthropicApiException api when (int?)Status(api) >= 500 => $"The Anthropic API had a problem ({(int?)Status(api)}). Retry in a moment.",
        AnthropicApiException api => $"The Anthropic API couldn't take the request ({(int?)Status(api)}): {Short(api.Message)}",
        AnthropicSseException sse => $"The reply broke off: {Short(sse.Message)} Retry to try again.",
        AnthropicIOException or HttpRequestException or IOException => "Couldn't reach the Anthropic API. Check the internet connection, then Retry.",
        TimeoutException or TaskCanceledException => "The Anthropic API took too long to answer. Retry to try again.",
        _ => $"The request failed: {Short(e.Message)} Retry to try again.",
    };

    private static HttpStatusCode? Status(AnthropicApiException e) => e.StatusCode;

    private static string Short(string message)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length > 300 ? line[..300] + "…" : line;
    }
}
