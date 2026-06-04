namespace BDSKhanhHoa.ViewModels
{
    public class ChatResponse
    {
        public string Message { get; set; } = string.Empty;

        public List<object> SuggestedProperties { get; set; } = new();

        public bool ShouldShowSuggestions { get; set; }

        public string Intent { get; set; } = "General";

        public string Scenario { get; set; } = "General";

        public string Stage { get; set; } = "Start";

        public string SessionId { get; set; } = string.Empty;

        public List<string> SuggestedReplies { get; set; } = new();

        public bool NeedHumanSupport { get; set; }

        public Dictionary<string, string> CollectedSlots { get; set; } = new();
    }
}
