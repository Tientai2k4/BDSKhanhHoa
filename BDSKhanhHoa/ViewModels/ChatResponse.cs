namespace BDSKhanhHoa.ViewModels
{
    public class ChatResponse
    {
        public string Message { get; set; } = string.Empty;

        // Chỉ có dữ liệu khi khách thật sự muốn tìm BĐS
        public List<object> SuggestedProperties { get; set; } = new();

        // Dùng để frontend biết có nên vẽ card hay không
        public bool ShouldShowSuggestions { get; set; } = false;

        public string Intent { get; set; } = "General";
    }
}