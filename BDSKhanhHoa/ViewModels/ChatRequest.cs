namespace BDSKhanhHoa.ViewModels
{
    public class ChatRequest
    {
        public string Message { get; set; } = string.Empty;

        public int UserId { get; set; }

        public string? SessionId { get; set; }

        public string? PageContext { get; set; }

        public string? PageUrl { get; set; }

        public string? PageType { get; set; }

        public string? PageTitle { get; set; }
    }
}
