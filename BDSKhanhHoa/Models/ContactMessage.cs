using System;
using System.ComponentModel.DataAnnotations;

namespace BDSKhanhHoa.Models
{
    public class ContactMessage
    {
        [Key]
        public int ContactID { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập họ tên")]
        public string FullName { get; set; }

        public string? Phone { get; set; }

        [EmailAddress(ErrorMessage = "Email không hợp lệ")]
        public string? Email { get; set; }

        public string? Subject { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập nội dung")]
        public string Message { get; set; }

        public string Status { get; set; } = "Chưa xử lý";

        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}