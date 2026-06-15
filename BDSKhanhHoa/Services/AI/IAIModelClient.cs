namespace BDSKhanhHoa.Services.AI
{
    public interface IAIModelClient
    {
        Task<AIChatCompletionResult> GenerateAsync(
            AIChatCompletionRequest request,
            CancellationToken cancellationToken = default);
    }

    public class AIChatCompletionRequest
    {
        public string SystemPrompt { get; set; } = string.Empty;
        public string UserPrompt { get; set; } = string.Empty;
        public double Temperature { get; set; } = 0.2;
        public int MaxOutputTokens { get; set; } = 2048;

        public string? ModelOverride { get; set; }
        public bool UseAnswerModel { get; set; }
        public bool UseExtractionModel { get; set; }
        public bool UseFallbackModel { get; set; }

        // Bật khi câu hỏi cần kiến thức ngoài website: pháp lý, thủ tục, lãi suất, quy định, dự án, thị trường.
        // GeminiAIClient sẽ tự thêm tools: google_search nếu Gemini.EnableGoogleSearchGrounding = true.
        public bool UseGoogleSearchGrounding { get; set; }
    }

    public class AIChatCompletionResult
    {
        public bool Success { get; set; }
        public string? Text { get; set; }
        public string? RawResponse { get; set; }
        public string? ErrorMessage { get; set; }

        public bool IsQuotaExceeded { get; set; }
        public bool IsTimeout { get; set; }
        public bool IsSafetyBlocked { get; set; }

        public string? ModelUsed { get; set; }
        public string? FinishReason { get; set; }
    }
}
