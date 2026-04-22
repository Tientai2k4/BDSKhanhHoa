using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("Appointments")]
    public class Appointment
    {
        [Key]
        public int AppointmentID { get; set; }

        [Required]
        public int PropertyID { get; set; }

        [Required]
        public int BuyerID { get; set; }

        [Required]
        public int SellerID { get; set; }

        [Required(ErrorMessage = "Vui lòng chọn thời gian hẹn")]
        public DateTime AppointmentDate { get; set; }

        [StringLength(500, ErrorMessage = "Ghi chú không được vượt quá 500 ký tự")]
        public string? Note { get; set; }

        [StringLength(20)]
        public string Status { get; set; } = "Pending"; // Pending, Confirmed, Cancelled, Completed

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? UpdatedAt { get; set; }

        // Navigation Properties
        [ForeignKey("PropertyID")]
        public virtual Property? Property { get; set; }

        [ForeignKey("BuyerID")]
        public virtual User? Buyer { get; set; }

        [ForeignKey("SellerID")]
        public virtual User? Seller { get; set; }
    }
}