namespace BDSKhanhHoa.ViewModels
{
    public class ChatRequest
    {
        public string Message { get; set; } = string.Empty;

        public int UserId { get; set; }

        // Dùng để gửi thông tin tin BĐS hiện tại nếu khách đang xem trang chi tiết
        public string? PageContext { get; set; }
    }
}