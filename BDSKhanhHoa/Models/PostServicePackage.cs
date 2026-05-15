using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("PostServicePackages")]
    public class PostServicePackage
    {
        [Key]
        public int PackageID { get; set; }

        [Required(ErrorMessage = "Vui lòng chọn phân loại gói.")]
        [Display(Name = "Phân loại gói")]
        [StringLength(50)]
        public string PackageType { get; set; } = string.Empty;

        [Required(ErrorMessage = "Vui lòng nhập tên hiển thị của gói.")]
        [StringLength(100, ErrorMessage = "Tên gói không được vượt quá 100 ký tự.")]
        [Display(Name = "Tên hiển thị")]
        public string PackageName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Vui lòng nhập giá gói.")]
        [Range(0, double.MaxValue, ErrorMessage = "Giá gói không hợp lệ.")]
        [Display(Name = "Giá tiền")]
        public decimal Price { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập thời hạn sử dụng.")]
        [Range(0, 3650, ErrorMessage = "Thời hạn không hợp lệ. Tin thường dùng 0 ngày, gói VIP phải lớn hơn 0 ngày.")]
        [Display(Name = "Thời hạn sử dụng")]
        public int DurationDays { get; set; }

        [Required]
        [Range(1, 999, ErrorMessage = "Hạng hiển thị không hợp lệ.")]
        [Display(Name = "Hạng hiển thị")]
        public int PriorityLevel { get; set; }

        [Display(Name = "Mô tả đặc quyền")]
        [StringLength(500, ErrorMessage = "Mô tả không được vượt quá 500 ký tự.")]
        public string? Description { get; set; }
    }
}