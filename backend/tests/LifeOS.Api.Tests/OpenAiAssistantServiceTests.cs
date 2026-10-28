using System.Net;
using LifeOS.Api.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LifeOS.Api.Tests;

public sealed class OpenAiAssistantServiceTests
{
    [Fact]
    public async Task ReplyAsync_ReturnsOutputTextFromResponsesPayload()
    {
        var service = CreateService(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"output":[{"type":"message","content":[{"type":"output_text","text":"Plan the next step."}]}]}
                """)
        });

        var reply = await service.ReplyAsync("What next?", CancellationToken.None);

        Assert.Equal("Plan the next step.", reply);
    }

    [Fact]
    public async Task ReplyAsync_ThrowsActionableProviderErrorForRejectedKey()
    {
        var service = CreateService(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var exception = await Assert.ThrowsAsync<AssistantApiException>(() =>
            service.ReplyAsync("Hello", CancellationToken.None));

        Assert.Equal((int)HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Contains("Check your API key", exception.Message);
    }

    [Fact]
    public async Task ReplyAsync_RejectsMissingApiKeyBeforeMakingRequest()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var service = new OpenAiAssistantService(new HttpClient(handler), configuration);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReplyAsync("Hello", CancellationToken.None));
        Assert.False(handler.WasCalled);
    }

    private static OpenAiAssistantService CreateService(HttpResponseMessage response)
    {
        var handler = new RecordingHandler(response);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENAI_API_KEY"] = "test-key",
                ["OpenAI:Model"] = "test-model"
            })
            .Build();
        return new OpenAiAssistantService(new HttpClient(handler), configuration);
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public bool WasCalled { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            return Task.FromResult(response);
        }
    }
}
