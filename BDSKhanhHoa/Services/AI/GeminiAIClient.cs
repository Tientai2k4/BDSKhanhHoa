using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BDSKhanhHoa.Services.AI
{
    public class GeminiAIClient : IAIModelClient
    {
        private readonly HttpClient _httpClient;
        private readonly AIProviderSettings _settings;
        private readonly ILogger<GeminiAIClient> _logger;

        public GeminiAIClient(
            HttpClient httpClient,
            IOptions<AIProviderSettings> options,
            ILogger<GeminiAIClient> logger)
        {
            _httpClient = httpClient;
            _settings = options.Value;
            _logger = logger;

            _httpClient.Timeout = TimeSpan.FromSeconds(
                Math.Clamp(_settings.Gemini.TimeoutSeconds, 15, 120));
        }

        public async Task<AIChatCompletionResult> GenerateAsync(
            AIChatCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            string apiKey = ResolveApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return new AIChatCompletionResult
                {
                    Success = false,
                    ErrorMessage = "Chưa cấu hình Gemini ApiKey. Hãy dùng User Secrets hoặc biến môi trường GEMINI_API_KEY."
                };
            }

            string primaryModel = ResolveModel(request);
            AIChatCompletionResult primary = await SendToGeminiAsync(
                request,
                primaryModel,
                apiKey,
                cancellationToken);

            if (primary.Success) return primary;

            // Nếu lỗi do công cụ Google Search Grounding chưa bật/không khả dụng ở tài khoản,
            // thử lại cùng model nhưng không dùng grounding để chatbot vẫn trả lời được.
            if (request.UseGoogleSearchGrounding)
            {
                AIChatCompletionRequest noGroundingRequest = new()
                {
                    SystemPrompt = request.SystemPrompt,
                    UserPrompt = request.UserPrompt,
                    Temperature = request.Temperature,
                    MaxOutputTokens = request.MaxOutputTokens,
                    ModelOverride = request.ModelOverride,
                    UseAnswerModel = request.UseAnswerModel,
                    UseExtractionModel = request.UseExtractionModel,
                    UseFallbackModel = request.UseFallbackModel,
                    UseGoogleSearchGrounding = false
                };

                AIChatCompletionResult noGrounding = await SendToGeminiAsync(
                    noGroundingRequest,
                    primaryModel,
                    apiKey,
                    cancellationToken);

                if (noGrounding.Success) return noGrounding;
            }

            bool canFallback =
                primary.IsQuotaExceeded &&
                !request.UseFallbackModel &&
                !string.IsNullOrWhiteSpace(_settings.Gemini.FallbackAnswerModel) &&
                !_settings.Gemini.FallbackAnswerModel.Equals(primaryModel, StringComparison.OrdinalIgnoreCase);

            if (!canFallback) return primary;

            _logger.LogWarning(
                "Gemini model {Model} bị quota/lỗi giới hạn. Thử fallback model {FallbackModel}.",
                primaryModel,
                _settings.Gemini.FallbackAnswerModel);

            AIChatCompletionRequest fallbackRequest = new()
            {
                SystemPrompt = request.SystemPrompt,
                UserPrompt = request.UserPrompt,
                Temperature = Math.Min(request.Temperature, 0.2),
                MaxOutputTokens = Math.Min(request.MaxOutputTokens, 4096),
                UseFallbackModel = true
            };

            AIChatCompletionResult fallback = await SendToGeminiAsync(
                fallbackRequest,
                _settings.Gemini.FallbackAnswerModel.Trim(),
                apiKey,
                cancellationToken);

            if (fallback.Success) return fallback;

            return primary;
        }

        private async Task<AIChatCompletionResult> SendToGeminiAsync(
            AIChatCompletionRequest request,
            string model,
            string apiKey,
            CancellationToken cancellationToken)
        {
            string baseUrl = string.IsNullOrWhiteSpace(_settings.Gemini.BaseUrl)
                ? "https://generativelanguage.googleapis.com/v1beta/models"
                : _settings.Gemini.BaseUrl.TrimEnd('/');

            string url = $"{baseUrl}/{model}:generateContent";

            object body;

            bool enableGoogleSearchGrounding =
                request.UseGoogleSearchGrounding &&
                _settings.Gemini.EnableGoogleSearchGrounding;

            if (enableGoogleSearchGrounding)
            {
                // Google Search Grounding: cho phép Gemini tự tìm kiếm nguồn ngoài khi câu hỏi cần thông tin rộng/mới.
                // Không dùng grounding để bịa tin BĐS trong website; ChatbotService vẫn chặn việc đề xuất tin nếu không có SQL.
                body = new
                {
                    systemInstruction = new
                    {
                        parts = new[] { new { text = request.SystemPrompt ?? string.Empty } }
                    },
                    contents = new[]
                    {
                        new
                        {
                            role = "user",
                            parts = new[] { new { text = request.UserPrompt ?? string.Empty } }
                        }
                    },
                    tools = new[]
                    {
                        new
                        {
                            google_search = new { }
                        }
                    },
                    generationConfig = new
                    {
                        temperature = Math.Clamp(request.Temperature, 0.0, 1.0),
                        maxOutputTokens = Math.Clamp(request.MaxOutputTokens, 256, 8192),
                        topP = Math.Clamp(_settings.Gemini.TopP, 0.1, 1.0),
                        topK = Math.Clamp(_settings.Gemini.TopK, 1, 100)
                    },
                    safetySettings = new[]
                    {
                        new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_MEDIUM_AND_ABOVE" },
                        new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "BLOCK_MEDIUM_AND_ABOVE" },
                        new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_MEDIUM_AND_ABOVE" },
                        new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_MEDIUM_AND_ABOVE" }
                    }
                };
            }
            else
            {
                body = new
                {
                    systemInstruction = new
                    {
                        parts = new[] { new { text = request.SystemPrompt ?? string.Empty } }
                    },
                    contents = new[]
                    {
                        new
                        {
                            role = "user",
                            parts = new[] { new { text = request.UserPrompt ?? string.Empty } }
                        }
                    },
                    generationConfig = new
                    {
                        temperature = Math.Clamp(request.Temperature, 0.0, 1.0),
                        maxOutputTokens = Math.Clamp(request.MaxOutputTokens, 256, 8192),
                        topP = Math.Clamp(_settings.Gemini.TopP, 0.1, 1.0),
                        topK = Math.Clamp(_settings.Gemini.TopK, 1, 100)
                    },
                    safetySettings = new[]
                    {
                        new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_MEDIUM_AND_ABOVE" },
                        new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "BLOCK_MEDIUM_AND_ABOVE" },
                        new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_MEDIUM_AND_ABOVE" },
                        new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_MEDIUM_AND_ABOVE" }
                    }
                };
            }

            int maxRetry = Math.Max(0, _settings.Gemini.MaxRetryCount);

            for (int attempt = 0; attempt <= maxRetry; attempt++)
            {
                try
                {
                    using HttpRequestMessage message = new(HttpMethod.Post, url);
                    message.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
                    message.Content = JsonContent.Create(body);

                    using HttpResponseMessage response = await _httpClient.SendAsync(message, cancellationToken);
                    string raw = await response.Content.ReadAsStringAsync(cancellationToken);

                    if (!response.IsSuccessStatusCode)
                    {
                        bool quota = IsQuotaError(response.StatusCode, raw);

                        _logger.LogWarning(
                            "Gemini API lỗi {Status}. QuotaExceeded={Quota}. Model={Model}",
                            response.StatusCode,
                            quota,
                            model);

                        if (quota)
                        {
                            return new AIChatCompletionResult
                            {
                                Success = false,
                                RawResponse = raw,
                                ErrorMessage = "Gemini API hết quota hoặc bị giới hạn lượt gọi.",
                                IsQuotaExceeded = true,
                                ModelUsed = model
                            };
                        }

                        bool canRetry =
                            attempt < maxRetry &&
                            response.StatusCode is HttpStatusCode.RequestTimeout
                                or HttpStatusCode.InternalServerError
                                or HttpStatusCode.BadGateway
                                or HttpStatusCode.ServiceUnavailable
                                or HttpStatusCode.GatewayTimeout;

                        if (canRetry)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(700 * (attempt + 1)), cancellationToken);
                            continue;
                        }

                        return new AIChatCompletionResult
                        {
                            Success = false,
                            RawResponse = raw,
                            ErrorMessage = $"Gemini API lỗi: {response.StatusCode}",
                            ModelUsed = model
                        };
                    }

                    GeminiTextResult parsed = ExtractText(raw);

                    return new AIChatCompletionResult
                    {
                        Success = !string.IsNullOrWhiteSpace(parsed.Text),
                        Text = parsed.Text,
                        RawResponse = raw,
                        ErrorMessage = string.IsNullOrWhiteSpace(parsed.Text)
                            ? BuildEmptyTextError(parsed)
                            : null,
                        IsSafetyBlocked = parsed.IsSafetyBlocked,
                        FinishReason = parsed.FinishReason,
                        ModelUsed = model
                    };
                }
                catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Gemini timeout. Model={Model}", model);

                    return new AIChatCompletionResult
                    {
                        Success = false,
                        ErrorMessage = "Gemini phản hồi quá lâu.",
                        IsTimeout = true,
                        ModelUsed = model
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi gọi Gemini. Model={Model}", model);

                    if (attempt < maxRetry)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(700 * (attempt + 1)), cancellationToken);
                        continue;
                    }

                    return new AIChatCompletionResult
                    {
                        Success = false,
                        ErrorMessage = ex.Message,
                        ModelUsed = model
                    };
                }
            }

            return new AIChatCompletionResult
            {
                Success = false,
                ErrorMessage = "Gemini không phản hồi.",
                ModelUsed = model
            };
        }

        private string ResolveApiKey()
        {
            if (!string.IsNullOrWhiteSpace(_settings.Gemini.ApiKey))
                return _settings.Gemini.ApiKey.Trim();

            return Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                   ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY")
                   ?? string.Empty;
        }

        private string ResolveModel(AIChatCompletionRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.ModelOverride))
                return request.ModelOverride.Trim();

            if (request.UseExtractionModel && !string.IsNullOrWhiteSpace(_settings.Gemini.ExtractionModel))
                return _settings.Gemini.ExtractionModel.Trim();

            if (request.UseFallbackModel && !string.IsNullOrWhiteSpace(_settings.Gemini.FallbackAnswerModel))
                return _settings.Gemini.FallbackAnswerModel.Trim();

            if (request.UseAnswerModel && !string.IsNullOrWhiteSpace(_settings.Gemini.AnswerModel))
                return _settings.Gemini.AnswerModel.Trim();

            if (!string.IsNullOrWhiteSpace(_settings.Gemini.Model))
                return _settings.Gemini.Model.Trim();

            return "gemini-2.5-flash";
        }

        private static bool IsQuotaError(HttpStatusCode statusCode, string raw)
        {
            return statusCode == HttpStatusCode.TooManyRequests ||
                   raw.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase) ||
                   raw.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
                   raw.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildEmptyTextError(GeminiTextResult parsed)
        {
            if (parsed.IsSafetyBlocked)
                return "Gemini đã chặn nội dung vì safety policy.";

            if (!string.IsNullOrWhiteSpace(parsed.FinishReason))
                return $"Gemini không trả về text. FinishReason={parsed.FinishReason}.";

            return "Gemini không trả về nội dung.";
        }

        private static GeminiTextResult ExtractText(string raw)
        {
            GeminiTextResult result = new();

            if (string.IsNullOrWhiteSpace(raw))
                return result;

            using JsonDocument doc = JsonDocument.Parse(raw);

            if (!doc.RootElement.TryGetProperty("candidates", out JsonElement candidates) ||
                candidates.ValueKind != JsonValueKind.Array ||
                candidates.GetArrayLength() == 0)
            {
                return result;
            }

            JsonElement candidate = candidates[0];

            if (candidate.TryGetProperty("finishReason", out JsonElement finishReason))
                result.FinishReason = finishReason.GetString();

            if (candidate.TryGetProperty("safetyRatings", out JsonElement safetyRatings) &&
                safetyRatings.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement rating in safetyRatings.EnumerateArray())
                {
                    if (rating.TryGetProperty("blocked", out JsonElement blocked) &&
                        blocked.ValueKind == JsonValueKind.True)
                    {
                        result.IsSafetyBlocked = true;
                        break;
                    }
                }
            }

            if (!candidate.TryGetProperty("content", out JsonElement content))
                return result;

            if (!content.TryGetProperty("parts", out JsonElement parts) ||
                parts.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            List<string> texts = new();

            foreach (JsonElement part in parts.EnumerateArray())
            {
                if (!part.TryGetProperty("text", out JsonElement textElement))
                    continue;

                string? text = textElement.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    texts.Add(text);
            }

            result.Text = string.Join("\n", texts).Trim();

            // Lấy nguồn groundingMetadata nếu Gemini trả về, rồi gắn vào cuối câu trả lời.
            // Widget đã hỗ trợ markdown link nên người dùng bấm được nguồn tham khảo.
            if (candidate.TryGetProperty("groundingMetadata", out JsonElement groundingMetadata) &&
                groundingMetadata.TryGetProperty("groundingChunks", out JsonElement groundingChunks) &&
                groundingChunks.ValueKind == JsonValueKind.Array)
            {
                List<string> sources = new();

                foreach (JsonElement chunk in groundingChunks.EnumerateArray())
                {
                    if (!chunk.TryGetProperty("web", out JsonElement web)) continue;

                    string? uri = web.TryGetProperty("uri", out JsonElement uriElement)
                        ? uriElement.GetString()
                        : null;

                    string? title = web.TryGetProperty("title", out JsonElement titleElement)
                        ? titleElement.GetString()
                        : null;

                    if (string.IsNullOrWhiteSpace(uri)) continue;

                    title = string.IsNullOrWhiteSpace(title)
                        ? "Nguồn tham khảo"
                        : title.Trim();

                    string line = $"- [{title}]({uri})";

                    if (!sources.Contains(line, StringComparer.OrdinalIgnoreCase))
                        sources.Add(line);

                    if (sources.Count >= 5) break;
                }

                if (sources.Count > 0 && !string.IsNullOrWhiteSpace(result.Text))
                {
                    result.Text += "\n\nNguồn tham khảo:\n" + string.Join("\n", sources);
                }
            }

            return result;
        }

        private sealed class GeminiTextResult
        {
            public string Text { get; set; } = string.Empty;
            public string? FinishReason { get; set; }
            public bool IsSafetyBlocked { get; set; }
        }
    }
}