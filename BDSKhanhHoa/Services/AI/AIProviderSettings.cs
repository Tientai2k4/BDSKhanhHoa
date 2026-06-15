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

        // Ổn định cho đồ án. Có thể đổi model trong appsettings mà không sửa code.
        public string Model { get; set; } = "gemini-2.5-flash";
        public string AnswerModel { get; set; } = "gemini-2.5-flash";

        // Tác vụ nhẹ/chiết xuất dùng Flash-Lite để tiết kiệm quota.
        public string ExtractionModel { get; set; } = "gemini-2.5-flash-lite";
        public string FallbackAnswerModel { get; set; } = "gemini-2.5-flash-lite";

        public int TimeoutSeconds { get; set; } = 45;
        public int MaxRetryCount { get; set; } = 1;
        public double TopP { get; set; } = 0.85;
        public int TopK { get; set; } = 40;

        // Cho phép Gemini tham khảo nguồn ngoài qua Google Search Grounding.
        // Dùng cho pháp lý cơ bản, giao dịch, vay vốn, dự án, thị trường.
        // Không dùng để bịa tin BĐS; tìm tin vẫn dựa trên SQL nội bộ.
        public bool EnableGoogleSearchGrounding { get; set; } = true;
    }
}
