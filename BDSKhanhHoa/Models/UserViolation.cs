using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("UserViolations")]
    public class UserViolation
    {
        [Key]
        public int ViolationID { get; set; }

        public int UserID { get; set; } // Người vi phạm (Chủ tin đăng)

        [Required]
        [StringLength(255)]
        public string Reason { get; set; } // Lý do vi phạm

        public string? Description { get; set; } // Phân tích chi tiết của Admin

        public int ReportedBy { get; set; } // ID của Admin/Staff xử lý

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [StringLength(50)]
        public string Status { get; set; } = "Active"; // Active (Đang tính lỗi), Expired (Đã xóa án tích)

        [ForeignKey("UserID")]
        public virtual User? User { get; set; }

        [ForeignKey("ReportedBy")]
        public virtual User? AdminContext { get; set; }
    }
}