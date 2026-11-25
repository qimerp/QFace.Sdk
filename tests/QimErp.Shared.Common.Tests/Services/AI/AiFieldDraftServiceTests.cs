using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using QFace.Sdk.AI.Models;
using QFace.Sdk.AI.Services;
using QimErp.Shared.Common.Services.AI;
using Xunit;

namespace QimErp.Shared.Common.Tests.Services.AI;

public sealed class AiFieldDraftServiceTests
{
    private static readonly AiFieldDraftCatalogue Catalogue = new(
        "You draft text for an accounting system.",
        [new AiFieldDraftPurpose("journal-description", "Write a short journal description.")]);

    private readonly Mock<ILLMService> _llm = new();
    private readonly Mock<IAIOptionsProvider> _options = new();

    private AiFieldDraftService Service(string apiKey = "key")
    {
        _options.Setup(o => o.GetOptionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AIOptions { DefaultLLMProvider = "OpenAI", OpenAI = new OpenAIOptions { ApiKey = apiKey } });
        return new AiFieldDraftService(_llm.Object, _options.Object, NullLogger<AiFieldDraftService>.Instance);
    }

    [Fact]
    public async Task Unknown_purpose_blank_prompt_and_bad_tone_fail_validation_without_calling_the_provider()
    {
        var result = await Service().DraftAsync(Catalogue, new AiFieldDraftRequest("payslip-note", " ", Tone: "angry"));

        result.IsSuccess.Should().BeFalse();
        result.Code.Should().Be("400");
        var error = result.Error.Should().BeOfType<AiFieldDraftValidationError>().Subject;
        error.Fields.Keys.Should().BeEquivalentTo("Purpose", "Prompt", "Tone");
        _llm.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Missing_provider_key_is_not_configured()
    {
        var service = Service(apiKey: "");

        var result = await service.DraftAsync(Catalogue, new AiFieldDraftRequest("journal-description", "Accrue rent"));

        result.Code.Should().Be("503");
        result.Error!.Code.Should().Be("Ai.NotConfigured");
        (await service.IsConfiguredAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Server_facts_reach_the_provider_and_the_text_is_capped()
    {
        LLMRequest? sent = null;
        _llm.Setup(l => l.GenerateChatCompletionAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((r, _) => sent = r)
            .ReturnsAsync(new LLMResponse { Content = "\"" + new string('a', 2500) + "\"" });

        var result = await Service().DraftAsync(Catalogue,
            new AiFieldDraftRequest("JOURNAL-DESCRIPTION", "Accrue rent", Facts: ["Journal JE-1", " "]));

        result.IsSuccess.Should().BeTrue();
        result.Data.Text.Should().HaveLength(AiFieldDraftService.MaxTextLength);
        sent!.Messages![0].Content.Should().StartWith("You draft text for an accounting system.").And.Contain("Write a short journal description.");
        sent.Messages![1].Content.Should().Be("Accrue rent\n\nFacts:\n- Journal JE-1");
    }

    [Fact]
    public async Task Provider_failure_is_unavailable()
    {
        _llm.Setup(l => l.GenerateChatCompletionAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Accrue rent"));

        var result = await Service().DraftAsync(Catalogue, new AiFieldDraftRequest("journal-description", "Accrue rent"));

        result.Code.Should().Be("503");
        result.Error!.Code.Should().Be("Ai.Unavailable");
    }
}
