using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("BankAccounts")]
    public class BankAccount
    {
        [Key]
        public int BankID { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập tên ngân hàng")]
        [StringLength(100)]
        public string BankName { get; set; } // Ví dụ: Vietcombank, MBBank

        [Required(ErrorMessage = "Vui lòng nhập số tài khoản")]
        [StringLength(50)]
        public string AccountNumber { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập tên chủ tài khoản")]
        [StringLength(100)]
        public string AccountName { get; set; }

        public string? Branch { get; set; }

        // Mã BIN của ngân hàng dùng để tạo VietQR (Ví dụ Vietcombank là 970436)
        [StringLength(20)]
        public string? BinCode { get; set; }

        public bool IsActive { get; set; } = true;
    }
}