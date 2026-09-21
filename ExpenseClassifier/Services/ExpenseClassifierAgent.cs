using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExpenseClassifier.Models;
using ExpenseClassifier.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ExpenseClassifier.Agents;

/// <summary>
/// Core contract for the intelligent expense classification service.
/// </summary>
public interface IExpenseClassifierAgent
{
    /// <summary>
    /// Classifies a single expense description using a two-stage agentic workflow:
    /// 1. Primary Policy Agent (with tool calling: <see cref="CompanyPolicyTool"/> and <see cref="SpendingLimitTool"/>).
    /// 2. Fallback General Agent (without tools) if the primary stage results in "Other" or an unrecognized policy category.
    /// Incorporates cache-aside caching to avoid redundant LLM invocations.
    /// </summary>
    Task<ResponseDto> Classify(string expenseDescription, CancellationToken cancellationToken = default);

    /// <summary>
    /// Classifies a collection of expense line items concurrently with bounded parallelism
    /// and generates aggregate financial metrics and compliance statistics.
    /// </summary>
    Task<BatchExpenseResponse> ClassifyBatch(BatchExpenseRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Complete reference implementation of <see cref="IExpenseClassifierAgent"/> featuring:
/// - Cache-aside caching with normalized keys
/// - Two-stage multi-tool agentic pipeline with automated fallback
/// - Financial entity extraction (amount, ISO currency, merchant)
/// - Bounded-concurrency batch processing with summary aggregation
/// </summary>
public partial class ExpenseClassifierAgent : IExpenseClassifierAgent
{
    private readonly IChatClient _chatClient;
    private readonly CompanyPolicyTool _policyTool;
    private readonly SpendingLimitTool _spendingLimitTool;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ExpenseClassifierAgent> _logger;

    private readonly ChatOptions _primaryAgentOptions;
    private readonly ChatOptions _fallbackAgentOptions;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private const int MaxBatchConcurrency = 5;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private const string SystemPrompt = """
        You are an enterprise expense classifier and compliance auditor.
       
        GOAL
        Classify one employee expense claim into an official GL category, extract financial fields, and set complianceStatus using company tools.
       
        PROCEDURE (follow in order)
        1. Call GetPolicy.
        2. Call GetSpendingLimits.
        3. Map the claim to exactly one official category from GetPolicy, or "Other" if it does not fit.
        4. Extract: amount (number), currency (ISO 4217: NGN|USD|EUR|GBP when present), merchant, subcategory.
        5. Using GetSpendingLimits, set complianceStatus and a short complianceNotes reason.
        6. Return ONLY one JSON object matching the OUTPUT CONTRACT.
        
        HARD RULES
        - Do not invent policy or limits; use tool results only.
        - If amount is missing or unclear → complianceStatus = "Unverifiable".
        - If category is not an official policy category → category = "Other".
        - confidenceScore: 0.9–1.0 for clear matches; 0.6–0.8 if ambiguous; never invent certainty.
        - Output raw JSON only. No markdown. No prose before/after.
        
        OUTPUT CONTRACT
        {
          "category": "string",
          "subCategory": "string",
          "extractedAmount": 0.0,
          "currency": "string",
          "merchant": "string",
          "confidenceScore": 0.95,
          "complianceStatus": "Compliant" | "RequiresManagerApproval" | "PolicyViolation" | "Unverifiable",
          "complianceNotes": "string"
        }
        """;
    private const string FallbackPrompt = """
        You are the fallback expense classifier for claims that did not match official policy categories.
        
        GOAL
        Assign a broader business category and extract financial fields when the policy agent returned "Other" or failed.
        
        PROCEDURE
        1. Do NOT call tools (none are available).
        2. Choose the best broader category, for example:
           Health & Wellness, Training & Education, Marketing, Equipment, Travel Ancillary, Personal Care, Donations/Charitable, or Other.
        3. Prefer a specific broader label over "Other" when evidence exists.
        4. Extract amount, currency (ISO 4217 when present), merchant, subcategory.
        5. Infer complianceStatus conservatively:
           - missing/unclear amount → "Unverifiable"
           - otherwise "Compliant" unless the text clearly implies excess or prohibited spend.
        6. Return ONLY one JSON object matching the OUTPUT CONTRACT.
        
        HARD RULES
        - Do not force official policy GL categories; this stage is for non-policy / edge cases.
        - Do not invent amounts or currencies not present in the text.
        - confidenceScore: typically 0.7–0.85; lower if the description is vague.
        - Output raw JSON only. No markdown. No prose before/after.
        
        OUTPUT CONTRACT
        {
          "category": "string",
          "subCategory": "string",
          "extractedAmount": 0.0,
          "currency": "string",
          "merchant": "string",
          "confidenceScore": 0.85,
          "complianceStatus": "Compliant" | "RequiresManagerApproval" | "PolicyViolation" | "Unverifiable",
          "complianceNotes": "string"
        }
        """;

    public ExpenseClassifierAgent(
        IChatClient chatClient,
        CompanyPolicyTool policyTool,
        SpendingLimitTool spendingLimitTool,
        IMemoryCache cache,
        ILogger<ExpenseClassifierAgent> logger)
    {
        _chatClient = chatClient;
        _policyTool = policyTool;
        _spendingLimitTool = spendingLimitTool;
        _cache = cache;
        _logger = logger;

        // Pre-build reusable tool definitions & ChatOptions to eliminate per-request reflection overhead
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(_policyTool.GetPolicy, "GetPolicy"),
            AIFunctionFactory.Create(_spendingLimitTool.GetSpendingLimits, "GetSpendingLimits")
        };

        _primaryAgentOptions = new ChatOptions
        {
            Tools = tools
        };

        _fallbackAgentOptions = new ChatOptions();
    }

    /// <inheritdoc/>
    public async Task<ResponseDto> Classify(string expenseDescription, CancellationToken cancellationToken = default)
    {
        // TODO: Candidate Implementation:
        // 1. Validate input and normalize cache key.
        // 2. Check IMemoryCache for existing classification. If hit, return with ClassificationStage = "CacheHit".
        // 3. Stage 1: Invoke Primary Agent configured with CompanyPolicyTool & SpendingLimitTool.
        // 4. Stage 2: If primary returns "Other" or fails to match policy categories, invoke Fallback General Agent (no tools).
        // 5. Evaluate spending limits and compliance status (Compliant, RequiresManagerApproval, PolicyViolation, Unverifiable).
        // 6. Cache valid structured result with appropriate TTL.
        // 7. Return structured ResponseDto.

        if (string.IsNullOrWhiteSpace(expenseDescription))
        {
            throw new ArgumentException("Expense description must not be null or empty.", nameof(expenseDescription));
        }

        var normalizedKey = DescriptionNormalization(expenseDescription);
        var cacheKey = $"expenses_{normalizedKey}";

        // Check Cache-Aside Layer
        if (_cache.TryGetValue(cacheKey, out ResponseDto? cachedResult) && cachedResult != null)
        {
            _logger.LogInformation("fetch from Cache to get the expense: {Description}", expenseDescription);
            return MapSource(cachedResult, "FetchFromCache", expenseDescription);
        }

        _logger.LogInformation("Cache miss. Executing agent classification for: {Description}", expenseDescription);

        // Stage 1: Primary Policy-Driven Agent with Tool Calling
        var primaryResult = await ExecuteAgentAsync(
            SystemPrompt,
            expenseDescription,
            _primaryAgentOptions,
            cancellationToken);

        ResponseDto finalResult;

        // Stage 2: Fallback General Agent if Primary returned "Other" or failed to match
        if (primaryResult == null || string.Equals(primaryResult.Category, "Other", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Primary agent returned 'Other'. Triggering Fallback General Agent for: {Description}", expenseDescription);

            var fallbackResult = await ExecuteAgentAsync(
                FallbackPrompt,
                expenseDescription,
                _fallbackAgentOptions,
                cancellationToken);

            finalResult = fallbackResult ?? primaryResult ?? CreateAutomaticFallBackResponse(expenseDescription, "Other", "FallbackGeneralAgent");
            finalResult.ClassificationStage = "FallbackGeneralAgent";
        }
        else
        {
            finalResult = primaryResult;
            finalResult.ClassificationStage = "PolicyAgentWithTools";
        }

        finalResult.ExpenseDescription = expenseDescription.Trim();

        // Store valid result in cache
        _cache.Set(cacheKey, finalResult, CacheTtl);

        return finalResult;
    }

    /// <inheritdoc/>
    public async Task<BatchExpenseResponse> ClassifyBatch(BatchExpenseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Items == null || request.Items.Count == 0)
        {
            throw new ArgumentException("Batch expense items list cannot be null or empty.", nameof(request));
        }

        var stopwatch = Stopwatch.StartNew();
        var results = new ResponseDto[request.Items.Count];

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxBatchConcurrency,
            CancellationToken = cancellationToken
        };

        var indexedItems = request.Items.Select((item, index) => (item, index));

        await Parallel.ForEachAsync(indexedItems, parallelOptions, async (entry, ct) =>
        {
            var classified = await Classify(entry.item.Description, ct);
            results[entry.index] = classified;
        });

        stopwatch.Stop();

        // Calculate aggregate metrics
        var summary = new BatchSummary
        {
            TotalItems = results.Length,
            CompliantCount = results.Count(r => string.Equals(r.ComplianceStatus, "Compliant", StringComparison.OrdinalIgnoreCase)),
            RequiresApprovalCount = results.Count(r => string.Equals(r.ComplianceStatus, "RequiresManagerApproval", StringComparison.OrdinalIgnoreCase)),
            PolicyViolationCount = results.Count(r => string.Equals(r.ComplianceStatus, "PolicyViolation", StringComparison.OrdinalIgnoreCase)),
            ProcessingTimeMs = stopwatch.ElapsedMilliseconds
        };

        foreach (var r in results)
        {
            if (r.ExtractedAmount.HasValue && !string.IsNullOrWhiteSpace(r.Currency))
            {
                var currency = r.Currency.ToUpperInvariant();
                summary.TotalAmountByCurrency.TryAdd(currency, 0m);
                summary.TotalAmountByCurrency[currency] += r.ExtractedAmount.Value;
            }
        }

        return new BatchExpenseResponse
        {
            Summary = summary,
            Results = [.. results]
        };
    }

    



    private static string CleanUpJsonResponse(string response)
    {
        var trimmed = response.Trim();
        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..];
        }
        else if (trimmed.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[3..];
        }

        if (trimmed.EndsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^3];
        }

        return trimmed.Trim();
    }

    private static ResponseDto MapSource(ResponseDto source, string stage, string description)
    {
        return new ResponseDto
        {
            Category = source.Category,
            SubCategory = source.SubCategory,
            ExtractedAmount = source.ExtractedAmount,
            Currency = source.Currency,
            Merchant = source.Merchant,
            ConfidenceScore = source.ConfidenceScore,
            ComplianceStatus = source.ComplianceStatus,
            ComplianceNotes = source.ComplianceNotes,
            ClassificationStage = stage,
            ExpenseDescription = description.Trim()
        };
    }

    private static ResponseDto CreateAutomaticFallBackResponse(string description, string category, string stage)
    {
        return new ResponseDto
        {
            Category = category,
            SubCategory = "General Expense",
            ConfidenceScore = 0.5,
            ComplianceStatus = "Unverifiable",
            ComplianceNotes = "Automated fallback classification.",
            ClassificationStage = stage,
            ExpenseDescription = description.Trim()
        };
    }

    private static string DescriptionNormalization(string description)
    {
        var trimmed = description.Trim().ToLowerInvariant();
        return SpacesRegex().Replace(trimmed, " ");
    }

    private async Task<ResponseDto?> ExecuteAgentAsync(
        string systemPrompt,
        string userPrompt,
        ChatOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, userPrompt)
            };

            var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken);
            var responseText = response.Messages.FirstOrDefault()?.Text;

            if (string.IsNullOrWhiteSpace(responseText))
            {
                return null;
            }

            // Strip any accidental markdown formatting
            var cleanJson = CleanUpJsonResponse(responseText);
            return JsonSerializer.Deserialize<ResponseDto>(cleanJson, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent execution encountered an error for description: {Description}", userPrompt);
            return null;
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();
}
