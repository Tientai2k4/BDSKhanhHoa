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

        [Required]
        [StringLength(50)]
        public string AccountType { get; set; } = "Business";

        [Required(ErrorMessage = "Tên doanh nghiệp là bắt buộc")]
        [StringLength(255)]
        public string BusinessName { get; set; }

        [Required(ErrorMessage = "Mã số thuế là bắt buộc")]
        [StringLength(50)]
        public string TaxCode { get; set; }

        [StringLength(100)]
        public string? BusinessRegistrationNo { get; set; }

        [Required(ErrorMessage = "Tên người đại diện pháp luật là bắt buộc")]
        [StringLength(100)]
        public string RepresentativeName { get; set; }

        [Required(ErrorMessage = "Số điện thoại liên hệ là bắt buộc")]
        [StringLength(20)]
        public string RepresentativePhone { get; set; }

        [EmailAddress]
        [StringLength(255)]
        public string? BusinessEmail { get; set; } // Dùng để đối chiếu Domain công ty

        [Required(ErrorMessage = "Địa chỉ doanh nghiệp là bắt buộc")]
        [StringLength(500)]
        public string BusinessAddress { get; set; }

        // --- CÁC TRƯỜNG CHỐNG MẠO DANH KHÁCH HÀNG DOANH NGHIỆP ---
        public bool IsLegalRepresentative { get; set; } = true; // Tôi là người đại diện pháp luật?
        public string? AuthorizationFile { get; set; } // Giấy ủy quyền (Bắt buộc nếu IsLegalRepresentative = false)

        public string? LicenseImage { get; set; }
        public string? TaxCertificateImage { get; set; }

        [StringLength(50)]
        public string VerificationStatus { get; set; } = "Pending"; // Pending, NeedMoreInfo, Approved, Rejected

        public DateTime? SubmittedAt { get; set; } = DateTime.Now;

        // --- GHI VẾT KIỂM DUYỆT (AUDIT) ---
        public int? ReviewedByUserID { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public string? RejectionReason { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? UpdatedAt { get; set; }

        [ForeignKey("UserID")]
        public virtual User? User { get; set; }

        [ForeignKey("ReviewedByUserID")]
        public virtual User? Reviewer { get; set; }
    }
}