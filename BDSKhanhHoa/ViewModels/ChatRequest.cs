namespace BDSKhanhHoa.ViewModels
{
    public class ChatRequest
    {
        public string Message { get; set; } = string.Empty;

        public int UserId { get; set; }

        // Frontend gửi ngữ cảnh trang hiện tại vào đây.
        // Ví dụ khi khách đang xem chi tiết BĐS:
        // Tiêu đề, giá, diện tích, vị trí, loại BĐS...
        public string? PageContext { get; set; }
    }
}