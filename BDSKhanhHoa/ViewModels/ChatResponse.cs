namespace BDSKhanhHoa.ViewModels
{
    public class ChatResponse
    {
        public string Message { get; set; } = string.Empty;

        public List<object> SuggestedProperties { get; set; } = new();

        public bool ShouldShowSuggestions { get; set; } = false;

        public string Intent { get; set; } = "General";
    }
}