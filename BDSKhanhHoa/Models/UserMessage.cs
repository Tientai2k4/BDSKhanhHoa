using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    public class UserMessage
    {
        [Key]
        public int MessageID { get; set; }

        [Required]
        public int SenderID { get; set; }

        [ForeignKey(nameof(SenderID))]
        public User? Sender { get; set; }

        [Required]
        public int ReceiverID { get; set; }

        [ForeignKey(nameof(ReceiverID))]
        public User? Receiver { get; set; }

        [Required]
        public int PropertyID { get; set; }

        [ForeignKey(nameof(PropertyID))]
        public Property? Property { get; set; }

        [StringLength(3000)]
        public string? MessageContent { get; set; }

        [StringLength(500)]
        public string? AttachmentUrl { get; set; }

        [StringLength(50)]
        public string MessageType { get; set; } = "Text"; // Text, Image, File

        public bool IsRead { get; set; } = false;

        /*
            Xóa lịch sử theo từng phía người dùng:
            - Không xóa vật lý tin nhắn khỏi CSDL.
            - Người gửi/người nhận chỉ ẩn cuộc trò chuyện khỏi hộp thư cá nhân.
            - Admin/Staff vẫn xem được lịch sử đầy đủ khi có báo cáo hoặc yêu cầu cung cấp bằng chứng.
        */
        public bool IsDeletedBySender { get; set; } = false;
        public bool IsDeletedByReceiver { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
