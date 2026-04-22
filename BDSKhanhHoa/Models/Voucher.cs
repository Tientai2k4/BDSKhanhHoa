using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("Vouchers")]
    public class Voucher
    {
        [Key] public int VoucherID { get; set; }

        [Required]
        [StringLength(50)]
        public string Code { get; set; } // Ví dụ: TET2026

        public decimal DiscountPercent { get; set; } // Phầm trăm giảm (VD: 20%)
        public decimal MaxDiscountAmount { get; set; } // Giảm tối đa (VD: 500,000đ)

        public int Quantity { get; set; }
        public int UsedCount { get; set; } = 0;

        public DateTime ExpiryDate { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}