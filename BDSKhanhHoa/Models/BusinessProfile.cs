using System;
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
        [StringLength(255, ErrorMessage = "Tên doanh nghiệp không được vượt quá 255 ký tự")]
        [Display(Name = "Tên doanh nghiệp")]
        public string BusinessName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Mã số thuế là bắt buộc")]
        [StringLength(50, ErrorMessage = "Mã số thuế không được vượt quá 50 ký tự")]
        [Display(Name = "Mã số thuế")]
        public string TaxCode { get; set; } = string.Empty;

        [Required(ErrorMessage = "Tên người đại diện pháp luật là bắt buộc")]
        [StringLength(100, ErrorMessage = "Tên người đại diện không được vượt quá 100 ký tự")]
        [Display(Name = "Người đại diện")]
        public string RepresentativeName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Số điện thoại liên hệ là bắt buộc")]
        [StringLength(20, ErrorMessage = "Số điện thoại không được vượt quá 20 ký tự")]
        [Display(Name = "Số điện thoại đại diện")]
        public string RepresentativePhone { get; set; } = string.Empty;

        [EmailAddress(ErrorMessage = "Email doanh nghiệp không hợp lệ")]
        [StringLength(255, ErrorMessage = "Email doanh nghiệp không được vượt quá 255 ký tự")]
        [Display(Name = "Email doanh nghiệp")]
        public string? BusinessEmail { get; set; }

        [Required(ErrorMessage = "Địa chỉ doanh nghiệp là bắt buộc")]
        [StringLength(500, ErrorMessage = "Địa chỉ doanh nghiệp không được vượt quá 500 ký tự")]
        [Display(Name = "Địa chỉ doanh nghiệp")]
        public string BusinessAddress { get; set; } = string.Empty;

        [StringLength(500)]
        [Display(Name = "Giấy phép kinh doanh")]
        public string? LicenseImage { get; set; }

        [StringLength(500)]
        [Display(Name = "Giấy chứng nhận mã số thuế")]
        public string? TaxCertificateImage { get; set; }

        [StringLength(50)]
        [Display(Name = "Trạng thái xác minh")]
        public string VerificationStatus { get; set; } = "Pending";

        public int? ReviewedByUserID { get; set; }

        [Display(Name = "Lý do từ chối")]
        public string? RejectionReason { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? UpdatedAt { get; set; }

        [ForeignKey("UserID")]
        public virtual User? User { get; set; }

        [ForeignKey("ReviewedByUserID")]
        public virtual User? Reviewer { get; set; }
    }
}
