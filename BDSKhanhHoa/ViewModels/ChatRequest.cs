namespace BDSKhanhHoa.ViewModels
{
    public class ChatRequest
    {
        public string Message { get; set; } = string.Empty;
        public int UserId { get; set; } // Giữ nguyên kiểu int (0 = Khách chưa đăng nhập)
        public string? PageContext { get; set; } // Chứa thông tin Meta Tag trang đang xem
    }
}