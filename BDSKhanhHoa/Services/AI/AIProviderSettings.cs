namespace BDSKhanhHoa.Services.AI
{
    public class AIProviderSettings
    {
        public string Provider { get; set; } = "Gemini";
        public GeminiSettings Gemini { get; set; } = new();
    }

    public class GeminiSettings
    {
        public string ApiKey { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/models";

        // Ổn định cho đồ án. Nếu AI Studio có model mới hơn/free hơn thì chỉ đổi trong appsettings.
        public string Model { get; set; } = "gemini-2.5-flash";
        public string AnswerModel { get; set; } = "gemini-2.5-flash";

        // Tác vụ rẻ/nhẹ nên dùng Flash-Lite để giảm quota.
        public string ExtractionModel { get; set; } = "gemini-2.5-flash-lite";
        public string FallbackAnswerModel { get; set; } = "gemini-2.5-flash-lite";

        public int TimeoutSeconds { get; set; } = 45;
        public int MaxRetryCount { get; set; } = 1;
        public double TopP { get; set; } = 0.85;
        public int TopK { get; set; } = 40;
    }
}