using System;
using System.ComponentModel.DataAnnotations;

namespace BDSKhanhHoa.Models
{
    public class Banner
    {
        [Key]
        public int BannerID { get; set; }

        [StringLength(200)]
        [Display(Name = "Tiêu đề chính")]
        public string? Title { get; set; } // Đã chuyển thành Nullable (Tùy chọn)

        [StringLength(500)]
        [Display(Name = "Mô tả ngắn")]
        public string? SubTitle { get; set; } // Tùy chọn

        [StringLength(500)]
        public string? ImageURL { get; set; }

        [StringLength(500)]
        [Display(Name = "Liên kết điều hướng")]
        public string? LinkURL { get; set; }

        [Display(Name = "Thứ tự")]
        public int DisplayOrder { get; set; } = 0;

        [Display(Name = "Trạng thái")]
        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}