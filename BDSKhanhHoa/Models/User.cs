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
        [EmailAddress(ErrorMessage = "Email không đúng định dạng (VD: tenban@gmail.com)")]
        public string Email { get; set; } = string.Empty;

        // ĐÃ BỎ [Required] - Để EF Core tự động check NULL an toàn, không bị crash
        [StringLength(100, ErrorMessage = "Họ tên không được vượt quá 100 ký tự")]
        public string? FullName { get; set; }

        // ĐÃ BỎ [Required] - Tài khoản Google Auth tạo ra sẽ bị NULL Phone nên không được đặt Required ở Model
        [RegularExpression(@"^0[35789][0-9]{8}$", ErrorMessage = "Số điện thoại phải gồm 10 chữ số và bắt đầu bằng số hợp lệ (VD: 09, 03...)")]
        public string? Phone { get; set; }

        public string? Address { get; set; }

        [RegularExpression(@"^0[35789][0-9]{8}$", ErrorMessage = "Số Zalo phải gồm 10 chữ số hợp lệ")]
        public string? Zalo { get; set; }

        [Url(ErrorMessage = "Link Facebook không đúng định dạng (VD: https://facebook.com/...)")]
        public string? Facebook { get; set; }

        [StringLength(500, ErrorMessage = "Giới thiệu bản thân không được vượt quá 500 ký tự")]
        public string? Bio { get; set; }

        [Required]
        public int RoleID { get; set; }

        public bool IsActive { get; set; } = true;
        public bool IsDeleted { get; set; } = false;
        public bool? IsEmailVerified { get; set; }
        public DateTime? CreatedAt { get; set; }
        public string? Avatar { get; set; }
    }
}