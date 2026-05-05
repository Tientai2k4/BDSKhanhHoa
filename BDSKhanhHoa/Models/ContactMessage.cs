using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("ContactMessages")]
    public class ContactMessage
    {
        [Key]
        public int ContactID { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập họ tên")]
        [StringLength(255)]
        public string FullName { get; set; }

        [StringLength(20)]
        public string? Phone { get; set; }

        [EmailAddress(ErrorMessage = "Email không hợp lệ")]
        [StringLength(255)]
        public string? Email { get; set; }

        [StringLength(255)]
        public string? Subject { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập nội dung")]
        public string Message { get; set; }

        // Lưu đường dẫn file đính kèm (Ảnh/Hồ sơ)
        public string? AttachmentPath { get; set; }

        [StringLength(50)]
        public string Status { get; set; } = "Pending";

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // Bổ sung trường UpdateAt để lưu ngày Admin xử lý xong
        public DateTime? UpdatedAt { get; set; }

        // Liên kết với User (CĐT) để Admin biết ai gửi
        public int? UserID { get; set; }
        [ForeignKey("UserID")]
        public virtual User? User { get; set; }

        // Liên kết với Dự án
        public int? ProjectID { get; set; }
        [ForeignKey("ProjectID")]
        public virtual Project? Project { get; set; }
    }
}