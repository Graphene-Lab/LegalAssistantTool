using System.Text;
using System.Text.Json;

namespace AIOrchestrator.API.Legal;

/// <summary>
/// Thin, failure-tolerant wrapper around the host LLM. Every call is optional: when no provider
/// is configured, or the call fails or times out, the caller gets <c>null</c> and carries on with
/// the deterministic path, so the tool always returns a grounded answer.
/// </summary>
internal static class LegalLlm
{
    /// <summary>Characters per token assumed for Italian prose (conservative).</summary>
    public const int CharsPerToken = 3;

    /// <summary>Character budget derived from the provider context window.</summary>
    public static int ContextChars
    {
        get
        {
            var window = Setup.ProviderConfig?.ContextWindow ?? 32000;
            if (window <= 0) window = 32000;
            return window * CharsPerToken;
        }
    }

    /// <summary>Character budget for a single attachment once several are attached.</summary>
    /// <param name="attachmentCount">How many attachments share the budget.</param>
    /// <returns>Characters allowed per attachment.</returns>
    public static int AttachmentBudget(int attachmentCount)
    {
        var share = Math.Max(1, attachmentCount);
        return Math.Max(4000, ContextChars / 3 / share);
    }

    private static DateTime _started = DateTime.UtcNow;
    private static readonly object Gate = new();
    private static bool _poisoned;

    /// <summary>
    /// Total time the whole opinion may spend talking to the model. A local model can take minutes
    /// per answer, and an agent tool cannot block a conversation for half an hour: past this budget
    /// every further call is skipped and the deterministic answer is returned instead.
    /// </summary>
    public static int BudgetSeconds
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("LEGALASSISTANTTOOL_LLM_BUDGET_SECONDS");
            return int.TryParse(raw, out var seconds) && seconds > 0 ? seconds : 240;
        }
    }

    /// <summary>Starts a new time budget. Called at the beginning of every tool call.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _started = DateTime.UtcNow;
            _poisoned = false;
        }
    }
    /// <summary>Seconds left in the budget of this call (never negative).</summary>
    public static int RemainingSeconds =>
        Math.Max(0, BudgetSeconds - (int)(DateTime.UtcNow - _started).TotalSeconds);

    /// <summary>True when the model already blew its budget or a call had to be abandoned.</summary>
    public static bool Exhausted => _poisoned || RemainingSeconds <= 0;

    /// <summary>Sends one prompt and returns the text, or <c>null</c> when the LLM is unavailable.</summary>
    /// <param name="prompt">Prompt text.</param>
    /// <param name="role">System role to use.</param>
    /// <param name="maxTokens">Maximum response tokens.</param>
    /// <param name="temperature">Sampling temperature.</param>
    /// <param name="label">Short label used in the log.</param>
    /// <param name="timeoutSeconds">Maximum time to wait for this single answer.</param>
    /// <returns>The response text, or <c>null</c>.</returns>
    public static string? Ask(string prompt, LLMUtility.SystemRole role = LLMUtility.SystemRole.None,
        int? maxTokens = null, double temperature = 0.0, string label = "llm", int timeoutSeconds = 90)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return null;
        lock (Gate)
        {
            if (Exhausted)
            {
                Log.LogStep($"LLM call '{label}' skipped: time budget exhausted ({BudgetSeconds}s).");
                return null;
            }

            var wait = Math.Min(timeoutSeconds, RemainingSeconds);
            try
            {
                using var llm = new LLMUtility();
                var task = Task.Run(() => llm.SendQuery(prompt, useHistory: false, role: role,
                    maxToken: maxTokens, temperature: temperature));
                if (!task.Wait(TimeSpan.FromSeconds(wait)))
                {
                    // The abandoned call keeps the provider busy: no further call can be trusted.
                    _poisoned = true;
                    Log.LogStep($"LLM call '{label}' exceeded {wait}s and was abandoned; using the deterministic answer.");
                    return null;
                }

                var (response, hResult) = task.Result;
                if (string.IsNullOrWhiteSpace(response))
                {
                    Log.LogStep($"LLM call '{label}' returned nothing (hResult={hResult}).");
                    return null;
                }

                Log.LogStep($"LLM call '{label}': {response.Length} chars in {RemainingSeconds}s left (hResult={hResult}).");
                // Shared helper: strips a stray Markdown fence and the padding models add around prose.
                Utility.RemoveFencesEncapsulationAndFixTrim(ref response);
                return string.IsNullOrWhiteSpace(response) ? null : response.Trim();
            }
            catch (Exception ex)
            {
                Log.LogStep($"LLM call '{label}' failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>Sends one prompt and parses the JSON object it returns.</summary>
    /// <typeparam name="T">Type to deserialize.</typeparam>
    /// <param name="prompt">Prompt text.</param>
    /// <param name="label">Short label used in the log.</param>
    /// <param name="maxTokens">Maximum response tokens.</param>
    /// <param name="timeoutSeconds">Maximum time to wait for this single answer.</param>
    /// <returns>The parsed object, or <c>null</c>.</returns>
    public static T? AskJson<T>(string prompt, string label, int? maxTokens = 800, int timeoutSeconds = 90) where T : class
    {
        var response = Ask(prompt + "\n\nRispondi SOLO con un oggetto JSON valido, senza testo aggiuntivo.",
            LLMUtility.SystemRole.None, maxTokens, 0.0, label, timeoutSeconds);
        if (response == null) return null;

        var json = ExtractJson(response);
        if (json == null)
        {
            Log.LogStep($"LLM call '{label}': no JSON object in the response.");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            Log.LogStep($"LLM call '{label}': invalid JSON ({ex.Message}).");
            return null;
        }
    }

    /// <summary>Condenses a long text, keeping the legally salient facts.</summary>
    /// <param name="text">Text to condense.</param>
    /// <param name="targetChars">Target length.</param>
    /// <param name="label">Short label used in the log.</param>
    /// <returns>The condensed text, or <c>null</c> when the LLM is unavailable.</returns>
    public static string? Summarize(string text, int targetChars, string label)
    {
        if (text.Length <= targetChars) return text;
        var prompt =
            "Riassumi il documento seguente mantenendo in modo rigoroso: date, importi, nomi dei ruoli " +
            "(attore, convenuto, imputato, parte offesa), numeri di articolo e di sentenza citati, " +
            "condotte contestate, elementi probatori e qualsiasi circostanza che possa cambiare l'esito " +
            $"giudiziario. Non inventare nulla e non aggiungere valutazioni. Massimo {targetChars / CharsPerToken} token.\n\n" +
            "DOCUMENTO:\n" + ItalianText.Clip(text, ContextChars) + "\n\nRIASSUNTO:";
        var summary = Ask(prompt, LLMUtility.SystemRole.DocumentSummarizer, targetChars / CharsPerToken, 0.0, label);
        if (string.IsNullOrWhiteSpace(summary)) return null;

        // Never let a summary grow past what it replaces.
        return summary.Length <= text.Length ? summary : ItalianText.Clip(summary, text.Length);
    }

    /// <summary>Extracts the first complete JSON object from a model response.</summary>
    /// <param name="response">Model response.</param>
    /// <returns>The JSON text, or <c>null</c>.</returns>
    private static string? ExtractJson(string response)
    {
        var start = response.IndexOf('{');
        if (start < 0) return null;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < response.Length; index++)
        {
            var character = response[index];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inString = false;
                continue;
            }

            switch (character)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return response.Substring(start, index - start + 1);
                    break;
            }
        }

        return null;
    }
}