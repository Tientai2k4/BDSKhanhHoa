using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("Consultations")]
    public class Consultation
    {
        [Key]
        public int ConsultID { get; set; }

        [StringLength(255)]
        public string FullName { get; set; }

        [StringLength(20)]
        public string Phone { get; set; }

        [StringLength(255)]
        public string? Email { get; set; }

        public string? Note { get; set; }

        // Ghi chú của môi giới/chủ nhà sau khi gọi cho khách (Lưu lịch sử chăm sóc)
        public string? SellerNote { get; set; }

        public int? PropertyID { get; set; }
        public int? ProjectID { get; set; }
        public int? AssignedToUserID { get; set; }

        // ID của Người Mua (Nếu họ đã đăng nhập)
        public int? SenderID { get; set; }

        [StringLength(50)]
        public string? LeadType { get; set; } // "Property" hoặc "Project"

        // Các trạng thái: New, Contacted, Closed, Spam, Cancelled
        [StringLength(50)]
        public string Status { get; set; } = "New";

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? UpdatedAt { get; set; }

        [ForeignKey("ProjectID")]
        public virtual Project? Project { get; set; }

        [ForeignKey("AssignedToUserID")]
        public virtual User? AssignedUser { get; set; }

        [ForeignKey("PropertyID")]
        public virtual Property? Property { get; set; }

        [ForeignKey("SenderID")]
        public virtual User? Sender { get; set; }
    }
}