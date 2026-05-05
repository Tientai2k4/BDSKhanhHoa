using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("BusinessProfiles")]
    public class BusinessProfile
    {
        [Key]
        public int BusinessProfileID { get; set; }

        [Required]
        public int UserID { get; set; }

        [Required(ErrorMessage = "Tên doanh nghiệp là bắt buộc")]
        [StringLength(255)]
        public string BusinessName { get; set; }

        [Required(ErrorMessage = "Mã số thuế là bắt buộc")]
        [StringLength(50)]
        public string TaxCode { get; set; }

        [Required(ErrorMessage = "Tên người đại diện pháp luật là bắt buộc")]
        [StringLength(100)]
        public string RepresentativeName { get; set; }

        [Required(ErrorMessage = "Số điện thoại liên hệ là bắt buộc")]
        [StringLength(20)]
        public string RepresentativePhone { get; set; }

        [EmailAddress]
        [StringLength(255)]
        public string? BusinessEmail { get; set; } // Dùng để gửi mail liên hệ trong Admin

        [Required(ErrorMessage = "Địa chỉ doanh nghiệp là bắt buộc")]
        [StringLength(500)]
        public string BusinessAddress { get; set; }

        // --- HỒ SƠ CHỨNG THỰC ---
        public string? LicenseImage { get; set; } // Ảnh Giấy phép KD
        public string? TaxCertificateImage { get; set; } // Ảnh Chứng nhận MST

        [StringLength(50)]
        public string VerificationStatus { get; set; } = "Pending"; // Pending, Approved, Rejected

        // --- GHI VẾT KIỂM DUYỆT ---
        public int? ReviewedByUserID { get; set; }
        public string? RejectionReason { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? UpdatedAt { get; set; }

        // --- NAVIGATION PROPERTIES ---
        [ForeignKey("UserID")]
        public virtual User? User { get; set; }

        [ForeignKey("ReviewedByUserID")]
        public virtual User? Reviewer { get; set; }
    }
}