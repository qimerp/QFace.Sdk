using Microsoft.Extensions.DependencyInjection.Extensions;
using QFace.Sdk.AI.Models;
using QFace.Sdk.AI.Services;

namespace QimErp.Shared.Common.Services.AI;

public sealed record AiFieldDraftPurpose(string Key, string Instruction);

/// <summary>A service's own list of draft purposes; permission checks stay with the service.</summary>
public sealed class AiFieldDraftCatalogue
{
    private readonly Dictionary<string, string> _instructions;

    /// <param name="systemInstruction">Opening instruction naming the system, for example "You draft text for an accounting system."</param>
    public AiFieldDraftCatalogue(string systemInstruction, IEnumerable<AiFieldDraftPurpose> purposes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemInstruction);
        ArgumentNullException.ThrowIfNull(purposes);
        SystemInstruction = systemInstruction.Trim();
        _instructions = purposes.ToDictionary(p => p.Key, p => p.Instruction, StringComparer.OrdinalIgnoreCase);
    }

    public string SystemInstruction { get; }

    public IReadOnlyCollection<string> Keys => _instructions.Keys;

    public bool TryGetInstruction(string? purpose, out string instruction)
    {
        instruction = string.Empty;
        return !string.IsNullOrWhiteSpace(purpose) && _instructions.TryGetValue(purpose.Trim(), out instruction!);
    }
}

/// <param name="Tone">neutral (default), formal or friendly.</param>
/// <param name="Length">short, medium (default) or long.</param>
/// <param name="Facts">Context the server read for this draft; never taken from the request body.</param>
public sealed record AiFieldDraftRequest(
    string? Purpose,
    string? Prompt,
    string? Tone = null,
    string? Length = null,
    IReadOnlyList<string>? Facts = null);

public sealed record AiFieldDraftResponse(string Text);

public sealed record AiFieldDraftValidationError(string Code, string Message, IReadOnlyDictionary<string, string[]> Fields)
    : Error(Code, Message);

public interface IAiFieldDraftService
{
    /// <summary>True when the resolved provider has an API key.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);

    /// <summary>
    /// Fails with Ai.ValidationFailed (400), Ai.NotConfigured (503) when no provider key is set, or Ai.Unavailable (503)
    /// when the provider errors or returns nothing. Text is at most <see cref="AiFieldDraftService.MaxTextLength"/> characters.
    /// </summary>
    Task<Result<AiFieldDraftResponse>> DraftAsync(AiFieldDraftCatalogue catalogue, AiFieldDraftRequest request, CancellationToken ct = default);
}

public sealed class AiFieldDraftService(
    ILLMService llm,
    IAIOptionsProvider optionsProvider,
    ILogger<AiFieldDraftService> logger) : IAiFieldDraftService
{
    public const int MaxPromptLength = 2000;
    public const int MaxTextLength = 2000;
    public const string ValidationFailedStatus = "400";
    public const string ServiceUnavailableStatus = "503";
    public const string ValidationFailedCode = "Ai.ValidationFailed";

    public static readonly Error NotConfigured = new("Ai.NotConfigured", "AI drafting is not set up.");
    public static readonly Error Unavailable = new("Ai.Unavailable", "AI drafting is not available right now. Try again later.");

    private const int MaxTokens = 1024;
    private const string DefaultTone = "neutral";
    private const string DefaultLength = "medium";

    private const string OutputRules =
        "Reply with the finished text only: plain text, no markdown, no surrounding quotes, no commentary. "
        + "Use only the facts the user gives; do not invent names, figures, dates or policies.";

    private static readonly Dictionary<string, string> Tones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["neutral"] = "Use a plain, neutral tone.",
        ["formal"] = "Use a formal, professional tone.",
        ["friendly"] = "Use a warm, friendly tone."
    };

    private static readonly Dictionary<string, string> Lengths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["short"] = "Write one or two sentences.",
        ["medium"] = "Write one short paragraph.",
        ["long"] = "Write two or three short paragraphs."
    };

    public async Task<Result<AiFieldDraftResponse>> DraftAsync(
        AiFieldDraftCatalogue catalogue, AiFieldDraftRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (!catalogue.TryGetInstruction(request.Purpose, out var purposeInstruction))
            errors[nameof(request.Purpose)] = [$"Purpose must be one of: {string.Join(", ", catalogue.Keys.Order(StringComparer.Ordinal))}."];

        var prompt = request.Prompt?.Trim() ?? string.Empty;
        if (prompt.Length == 0)
            errors[nameof(request.Prompt)] = ["Say what the text should be about."];
        else if (prompt.Length > MaxPromptLength)
            errors[nameof(request.Prompt)] = [$"Keep the prompt to {MaxPromptLength} characters or fewer."];

        var tone = string.IsNullOrWhiteSpace(request.Tone) ? DefaultTone : request.Tone.Trim();
        if (!Tones.TryGetValue(tone, out var toneInstruction))
            errors[nameof(request.Tone)] = ["Tone must be neutral, formal or friendly."];

        var length = string.IsNullOrWhiteSpace(request.Length) ? DefaultLength : request.Length.Trim();
        if (!Lengths.TryGetValue(length, out var lengthInstruction))
            errors[nameof(request.Length)] = ["Length must be short, medium or long."];

        if (errors.Count > 0)
        {
            return Result.WithFailure<AiFieldDraftResponse>(
                new AiFieldDraftValidationError(ValidationFailedCode, "Check the highlighted fields.", errors),
                code: ValidationFailedStatus);
        }

        if (!await IsConfiguredAsync(ct))
            return Result.WithFailure<AiFieldDraftResponse>(NotConfigured, code: ServiceUnavailableStatus);

        string? text;
        try
        {
            var response = await llm.GenerateChatCompletionAsync(new LLMRequest
            {
                Messages =
                [
                    new LLMMessage
                    {
                        Role = "system",
                        Content = $"{catalogue.SystemInstruction} {OutputRules} {purposeInstruction} {toneInstruction} {lengthInstruction}"
                    },
                    new LLMMessage { Role = "user", Content = UserContent(prompt, request.Facts) }
                ],
                MaxTokens = MaxTokens
            }, ct);

            text = response?.Content?.Trim().Trim('"').Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The exception is left out because its message could quote the prompt.
            logger.LogError("AI draft failed for purpose {Purpose}: {FailureType}", request.Purpose, ex.GetType().Name);
            return Result.WithFailure<AiFieldDraftResponse>(Unavailable, code: ServiceUnavailableStatus);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            logger.LogWarning("AI draft returned no text for purpose {Purpose}", request.Purpose);
            return Result.WithFailure<AiFieldDraftResponse>(Unavailable, code: ServiceUnavailableStatus);
        }

        return Result.WithSuccess(new AiFieldDraftResponse(text.Length > MaxTextLength ? text[..MaxTextLength].TrimEnd() : text));
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
    {
        AIOptions options;
        try
        {
            options = await optionsProvider.GetOptionsAsync(ct);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "AI provider configuration is missing");
            return false;
        }

        var apiKey = options.DefaultLLMProvider switch
        {
            "OpenAI" => options.OpenAI.ApiKey,
            "Anthropic" => options.Anthropic.ApiKey,
            "GoogleGemini" => options.GoogleGemini.ApiKey,
            "DeepSeek" => options.DeepSeek.ApiKey,
            _ => null
        };

        return !string.IsNullOrWhiteSpace(apiKey);
    }

    private static string UserContent(string prompt, IReadOnlyList<string>? facts)
    {
        var listed = facts?.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => "- " + f.Trim()).ToList();
        return listed is { Count: > 0 } ? $"{prompt}\n\nFacts:\n{string.Join('\n', listed)}" : prompt;
    }
}

public static class AiFieldDraftServiceCollectionExtensions
{
    /// <summary>Needs <c>AddAIServices</c> for <see cref="ILLMService"/> and <see cref="IAIOptionsProvider"/>.</summary>
    public static IServiceCollection AddAiFieldDrafting(this IServiceCollection services)
    {
        services.TryAddScoped<IAiFieldDraftService, AiFieldDraftService>();
        return services;
    }
}
