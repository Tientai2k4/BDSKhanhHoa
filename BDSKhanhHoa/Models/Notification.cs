using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("Notifications")]
    public class Notification
    {
        [Key]
        public int NotificationID { get; set; }

        public int UserID { get; set; }

        [Required]
        [StringLength(255)]
        public string Title { get; set; }

        [Required]
        public string Content { get; set; }

        // BỔ SUNG: Đường dẫn để người dùng click vào "Xử lý"
        [StringLength(500)]
        public string? ActionUrl { get; set; }

        // BỔ SUNG: Tên nút bấm (VD: "Sửa tin ngay", "Xem vi phạm")
        [StringLength(100)]
        public string? ActionText { get; set; }

        public bool IsRead { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [ForeignKey("UserID")]
        public virtual User? User { get; set; }
    }
}