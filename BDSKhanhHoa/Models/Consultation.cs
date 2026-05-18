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

        [Required(ErrorMessage = "Họ tên là bắt buộc")]
        [StringLength(255)]
        public string FullName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Số điện thoại là bắt buộc")]
        [StringLength(20)]
        public string Phone { get; set; } = string.Empty;

        [StringLength(255)]
        public string? Email { get; set; }

        public string? Note { get; set; }

        // Ghi chú nội bộ của người bán/môi giới sau khi chăm sóc khách
        public string? SellerNote { get; set; }

        public int? PropertyID { get; set; }

        public int? ProjectID { get; set; }

        public int? AssignedToUserID { get; set; }

        // ID người mua nếu đã đăng nhập. Khách vãng lai thì null.
        public int? SenderID { get; set; }

        [StringLength(50)]
        public string? LeadType { get; set; } // Property hoặc Project

        /*
            Luồng trạng thái chuẩn:
            New        : Khách vừa gửi, người bán chưa xử lý.
            Contacted  : Người bán đã gọi/đang chăm sóc.
            Closed     : Đã chốt/hoàn tất.
            Spam       : Lead ảo, sai số, không liên hệ được.
            Cancelled  : Người mua tự hủy trước khi người bán tiếp nhận.
        */
        [Required]
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