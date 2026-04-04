using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    public class RoleUpgradeRequest
    {
        [Key]
        public int RequestID { get; set; }

        public int UserID { get; set; }

        public string PhoneNumber { get; set; }

        public string Address { get; set; }

        public string Status { get; set; } = "Pending"; // Pending, Approved, Rejected

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // --- PHẦN QUAN TRỌNG ĐỂ SỬA LỖI ---
        // Thuộc tính này cho phép EF Core tự động Join bảng Users khi bạn dùng .Include(r => r.User)
        [ForeignKey("UserID")]
        public virtual User? User { get; set; }

        // Thuộc tính phụ trợ (có thể giữ lại hoặc bỏ vì đã có virtual User ở trên)
        public string? Username { get; set; }
    }
}