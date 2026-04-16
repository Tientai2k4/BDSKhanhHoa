using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("Users")]
    public class User
    {
        [Key]
        public int UserID { get; set; }

        [Required(ErrorMessage = "Tên đăng nhập là bắt buộc")]
        public string Username { get; set; } = string.Empty;

        [Required]
        public string Password { get; set; } = string.Empty;

        [Required(ErrorMessage = "Email là bắt buộc")]
        [EmailAddress(ErrorMessage = "Email không hợp lệ")]
        public string Email { get; set; } = string.Empty;

        public string? FullName { get; set; }
        public string? Phone { get; set; }
        public string? Address { get; set; }
        // Thêm 2 dòng này vào dưới thuộc tính Address
        public string? Zalo { get; set; }
        public string? Facebook { get; set; }

        [Required]
        public int RoleID { get; set; }

       
        public bool IsActive { get; set; } = true;

        // Tính năng xóa tạm thời
        public bool IsDeleted { get; set; } = false;

        public bool? IsEmailVerified { get; set; }
        public DateTime? CreatedAt { get; set; }
        public string? Avatar { get; set; }
    }
}