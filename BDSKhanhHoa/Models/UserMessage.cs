using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    public class UserMessage
    {
        [Key]
        public int MessageID { get; set; }

        public int SenderID { get; set; }
        [ForeignKey("SenderID")]
        public User? Sender { get; set; }

        public int ReceiverID { get; set; }
        [ForeignKey("ReceiverID")]
        public User? Receiver { get; set; }

        public int PropertyID { get; set; }
        [ForeignKey("PropertyID")]
        public Property? Property { get; set; }

        public string? MessageContent { get; set; }

        // --- 2 TRƯỜNG MỚI THÊM VÀO ---
        public string? AttachmentUrl { get; set; } // Đường dẫn lưu file/ảnh
        public string MessageType { get; set; } = "Text"; // Giá trị: "Text", "Image", "File"

        public bool IsRead { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}