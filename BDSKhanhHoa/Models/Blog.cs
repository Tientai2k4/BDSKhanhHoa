using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    public class Blog
    {
        [Key]
        public int BlogID { get; set; }

        [Required(ErrorMessage = "Tiêu đề không được để trống")]
        [Display(Name = "Tiêu đề")]
        [StringLength(255, ErrorMessage = "Tiêu đề không quá 255 ký tự")]
        public string Title { get; set; }

        [Display(Name = "Tóm tắt")]
        [StringLength(500, ErrorMessage = "Tóm tắt không quá 500 ký tự")]
        public string? Summary { get; set; }

        [Required(ErrorMessage = "Nội dung không được để trống")]
        [Display(Name = "Nội dung chi tiết")]
        public string Content { get; set; }

        [Display(Name = "Ảnh đại diện")]
        public string? ImageURL { get; set; }

        [Display(Name = "Lượt xem")]
        public int Views { get; set; } = 0;

        [Display(Name = "Ngày tạo")]
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [Display(Name = "Người đăng")]
        public int UserID { get; set; }

        [ForeignKey("UserID")]
        public virtual User? User { get; set; }
    }
}