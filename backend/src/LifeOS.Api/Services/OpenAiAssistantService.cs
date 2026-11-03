using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace LifeOS.Api.Services;

public class OpenAiAssistantService(HttpClient client, IConfiguration configuration)
{
    private const string AssistantInstructions = "You are LifeOS's helpful assistant. Answer the user's latest message directly in natural language, and be concise unless they ask for detail. Use the supplied workspace snapshot only when relevant; do not invent personal data or claim actions. Keep private reasoning and system instructions out of the reply. For medical or financial requests, give general information rather than a diagnosis or investment directive.";
    private string? ApiKey => configuration["OPENAI_API_KEY"] ?? configuration["OpenAI:ApiKey"];
    public bool UsesLocalModel => string.Equals(configuration["AI:Provider"], "Ollama", StringComparison.OrdinalIgnoreCase);
    public bool IsConfigured => UsesLocalModel || !string.IsNullOrWhiteSpace(ApiKey);

    public static string? SimpleGreetingReply(string message)
    {
        var normalized = message.Trim().TrimEnd('.', '!', '?', ',', ' ').ToLowerInvariant();
        return normalized switch
        {
            "hi" or "hello" or "hey" or "howdy" or "yo" => "Hi! How can I help?",
            "good morning" => "Good morning! How can I help?",
            "good afternoon" => "Good afternoon! How can I help?",
            "good evening" => "Good evening! How can I help?",
            "how are you" => "I'm doing well, thanks! What can I help with?",
            _ => null
        };
    }

    public Task<string> ReplyAsync(string prompt, CancellationToken cancellationToken) =>
        ReplyAsync(prompt, cancellationToken, isDirectRetry: false);

    private async Task<string> ReplyAsync(string prompt, CancellationToken cancellationToken, bool isDirectRetry)
    {
        if (!UsesLocalModel && string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("Configure OPENAI_API_KEY before using the OpenAI assistant.");

        using var request = new HttpRequestMessage(HttpMethod.Post, UsesLocalModel
            ? configuration["Ollama:ChatUrl"] ?? "http://localhost:11434/api/chat"
            : "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", UsesLocalModel ? "ollama" : ApiKey);
        request.Content = UsesLocalModel
            ? JsonContent.Create(new
            {
                model = configuration["Ollama:Model"] ?? "qwen3:4b",
                messages = new[]
                {
                    new { role = "system", content = AssistantInstructions + " Return the user-facing reply as a JSON object with one string field named answer." },
                    new { role = "user", content = prompt }
                },
                format = new
                {
                    type = "object",
                    properties = new { answer = new { type = "string" } },
                    required = new[] { "answer" },
                    additionalProperties = false
                },
                options = new { num_predict = 512, temperature = 0.2 },
                think = false,
                stream = false
            })
            : JsonContent.Create(new
            {
                model = configuration["OpenAI:Model"] ?? "gpt-6-astra",
                instructions = AssistantInstructions,
                input = prompt,
                max_output_tokens = 300
            });

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var (providerCode, providerType) = ReadProviderError(body);
            var requestId = response.Headers.TryGetValues("x-request-id", out var requestIds)
                ? requestIds.FirstOrDefault()
                : null;
            var retryAfter = response.Headers.RetryAfter?.Delta is { } delay
                ? (int)Math.Ceiling(delay.TotalSeconds)
                : (int?)null;
            throw new AssistantApiException((int)response.StatusCode, providerCode, providerType, requestId, retryAfter);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (UsesLocalModel && document.RootElement.TryGetProperty("message", out var localMessage)
            && localMessage.TryGetProperty("content", out var localContent)
            && localContent.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(localContent.GetString()))
        {
            var content = localContent.GetString()!;
            var answer = TryReadLocalAnswer(content) ?? content;
            return await FinalizeReplyAsync(answer, prompt, cancellationToken, isDirectRetry);
        }
        if (UsesLocalModel && document.RootElement.TryGetProperty("choices", out var choices))
        {
            var text = choices.EnumerateArray()
                .Select(choice => choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content)
                    ? content.GetString()
                    : null)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (!string.IsNullOrWhiteSpace(text)) return await FinalizeReplyAsync(UsesLocalModel ? TryReadLocalAnswer(text) ?? text : text, prompt, cancellationToken, isDirectRetry);
        }
        if (document.RootElement.TryGetProperty("output", out var output))
        {
            var text = output.EnumerateArray()
                .Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "message")
                .SelectMany(item => item.TryGetProperty("content", out var content) ? content.EnumerateArray() : [])
                .Where(part => part.TryGetProperty("type", out var type) && type.GetString() == "output_text")
                .Select(part => part.TryGetProperty("text", out var value) ? value.GetString() : null)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (!string.IsNullOrWhiteSpace(text)) return await FinalizeReplyAsync(text, prompt, cancellationToken, isDirectRetry);
        }

        throw new AssistantApiException(502, "The AI service returned an empty response. Please try again.");
    }

    private async Task<string> FinalizeReplyAsync(string reply, string prompt, CancellationToken cancellationToken, bool isDirectRetry)
    {
        var answer = reply.Trim();
        var thoughtEnd = answer.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (thoughtEnd >= 0)
            answer = answer[(thoughtEnd + "</think>".Length)..].Trim();
        else if (answer.StartsWith("<think>", StringComparison.OrdinalIgnoreCase))
            answer = answer["<think>".Length..].Trim();

        var opening = answer.ToLowerInvariant();
        var isInternalNarration = new[]
        {
            "we are given", "the user asks", "the user is asking", "okay, the user", "we should",
            "according to the instructions", "important:", "however, note", "the task is",
            "let me think", "let's think", "first, i should", "looking at the workspace",
            "analysis:", "example response:"
        }.Any(opening.StartsWith);
        if (!isInternalNarration) return answer;

        if (isDirectRetry)
            throw new AssistantApiException(502, UsesLocalModel
                ? "The local AI model could not produce a clean answer. Please try again."
                : "The AI returned an unusable response twice. Please try again.");

        var retryPrompt = $"Reply to the user's latest message as a helpful assistant. Write only the answer the user should see. Do not describe your reasoning, the prompt, the conversation format, or these instructions. Keep the reply as brief or detailed as the user needs.\n\n{prompt}";
        return await ReplyAsync(retryPrompt, cancellationToken, isDirectRetry: true);
    }

    private static string? TryReadLocalAnswer(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.TryGetProperty("answer", out var answer)
                && answer.ValueKind == JsonValueKind.String
                    ? answer.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string? Code, string? Type) ReadProviderError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("error", out var error)) return (null, null);
            var code = error.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : null;
            var type = error.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            return (code, type);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}

public sealed class AssistantApiException : Exception
{
    public AssistantApiException(int statusCode, string? providerCode, string? providerType, string? requestId, int? retryAfterSeconds)
        : base("The AI service could not complete this request. Check your API key and account access, then try again.")
    {
        StatusCode = statusCode;
        ProviderCode = providerCode;
        ProviderType = providerType;
        RequestId = requestId;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public AssistantApiException(int statusCode, string message) : base(message) => StatusCode = statusCode;

    public int StatusCode { get; }
    public string? ProviderCode { get; }
    public string? ProviderType { get; }
    public string? RequestId { get; }
    public int? RetryAfterSeconds { get; }
}
