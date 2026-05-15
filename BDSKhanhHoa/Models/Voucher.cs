// Models/Voucher.cs
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("Vouchers")]
    public class Voucher
    {
        [Key]
        public int VoucherID { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập mã CODE")]
        [StringLength(50)]
        public string Code { get; set; }

        [Required]
        [Column(TypeName = "decimal(5,2)")]
        public decimal DiscountPercent { get; set; }

        [Required]
        [Column(TypeName = "decimal(18,2)")]
        public decimal MaxDiscountAmount { get; set; }

        // MỚI: Giá trị đơn hàng tối thiểu để được áp dụng mã
        [Required]
        [Column(TypeName = "decimal(18,2)")]
        public decimal MinOrderAmount { get; set; }

        [Required]
        public int Quantity { get; set; }

        public int UsedCount { get; set; } = 0;

        // MỚI: Thời gian bắt đầu có hiệu lực
        [Required]
        public DateTime StartDate { get; set; }

        [Required]
        public DateTime ExpiryDate { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // MỚI: Mô tả hiển thị cho người dùng dễ hiểu
        [StringLength(255)]
        public string? Description { get; set; }
    }
}