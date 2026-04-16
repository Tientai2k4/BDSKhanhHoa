using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("Blogs")]
    public class Blog
    {
        [Key]
        public int BlogID { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập tiêu đề bài viết.")]
        [Display(Name = "Tiêu đề")]
        [StringLength(255, ErrorMessage = "Tiêu đề không được vượt quá 255 ký tự.")]
        public string Title { get; set; }

        // --- CỘT DANH MỤC MỚI THÊM ---
        [Required(ErrorMessage = "Vui lòng chọn danh mục bài viết.")]
        [Display(Name = "Danh mục")]
        [StringLength(100)]
        public string Category { get; set; }

        [Display(Name = "Tóm tắt nội dung")]
        [StringLength(500, ErrorMessage = "Tóm tắt không được vượt quá 500 ký tự.")]
        public string? Summary { get; set; }

        [Required(ErrorMessage = "Nội dung bài viết không được để trống.")]
        [Display(Name = "Nội dung chi tiết")]
        public string Content { get; set; }

        [Display(Name = "Ảnh đại diện")]
        public string? ImageURL { get; set; }

        [Display(Name = "Lượt xem")]
        public int Views { get; set; } = 0;

        public bool IsDeleted { get; set; } = false;

        [Display(Name = "Ngày đăng")]
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [Display(Name = "Tác giả")]
        public int UserID { get; set; }

        [ForeignKey("UserID")]
        public virtual User? User { get; set; }
    }
}