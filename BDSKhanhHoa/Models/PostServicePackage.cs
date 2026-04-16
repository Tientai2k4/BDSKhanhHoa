using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("PostServicePackages")]
    public class PostServicePackage
    {
        [Key]
        public int PackageID { get; set; }

        [Required(ErrorMessage = "Vui lòng chọn loại gói.")]
        [Display(Name = "Phân loại gói")]
        [StringLength(50)]
        public string PackageType { get; set; } // Kim Cương, Vàng, Bạc, Đồng, Tin Thường

        [Required(ErrorMessage = "Vui lòng nhập tên hiển thị cho gói.")]
        [StringLength(100, ErrorMessage = "Tên gói không được vượt quá 100 ký tự")]
        [Display(Name = "Tên hiển thị của gói")]
        public string PackageName { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập giá gói.")]
        [Range(0, double.MaxValue, ErrorMessage = "Giá gói không hợp lệ")]
        [Display(Name = "Giá tiền (VNĐ)")]
        public decimal Price { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập số ngày hiển thị.")]
        [Range(1, 365, ErrorMessage = "Thời hạn từ 1 đến 365 ngày")]
        [Display(Name = "Thời gian hiển thị (Ngày)")]
        public int DurationDays { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập mức độ ưu tiên.")]
        [Display(Name = "Mức độ ưu tiên")]
        public int PriorityLevel { get; set; } // Số càng cao, tin càng nằm trên Top

        [Display(Name = "Mô tả chi tiết")]
        [StringLength(500)]
        public string? Description { get; set; }
    }
}