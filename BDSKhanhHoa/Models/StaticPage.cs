using System.ComponentModel.DataAnnotations;

namespace BDSKhanhHoa.Models
{
    public class StaticPage
    {
        [Key]
        public int PageID { get; set; }

        [Required]
        [StringLength(50)]
        public string PageKey { get; set; } // Ví dụ: 'privacy', 'faq', 'contact'

        [Required]
        [StringLength(255)]
        public string Title { get; set; } // Tiêu đề trang

        [StringLength(500)]
        public string Description { get; set; } // Mô tả ngắn (dùng cho SEO/Hiển thị)

        public string Content { get; set; } // Nội dung HTML của trang

        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}