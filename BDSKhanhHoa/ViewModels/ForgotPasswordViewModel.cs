using System.ComponentModel.DataAnnotations;

namespace BDSKhanhHoa.ViewModels
{
    public class ForgotPasswordViewModel
    {
        [Required(ErrorMessage = "Vui lòng nhập Email để lấy lại mật khẩu")]
        [EmailAddress(ErrorMessage = "Email không hợp lệ")]
        public string Email { get; set; }
    }
}