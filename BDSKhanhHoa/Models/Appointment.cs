using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("Appointments")]
    public class Appointment
    {
        [Key]
        public int AppointmentID { get; set; }

        public int? PropertyID { get; set; }
        public int? ProjectID { get; set; }
        public int? LeadID { get; set; }

        [Required]
        public int BuyerID { get; set; }

        [Required]
        public int SellerID { get; set; }

        [StringLength(255)]
        public string? CustomerName { get; set; }

        [StringLength(20)]
        public string? CustomerPhone { get; set; }

        [StringLength(255)]
        public string? CustomerEmail { get; set; }

        [Required]
        public DateTime AppointmentDate { get; set; }

        // Ngày giờ đề xuất dời lịch (Nếu Seller dời lịch)
        public DateTime? ProposedAppointmentDate { get; set; }

        [StringLength(255)]
        public string? MeetingLocation { get; set; }

        [StringLength(255)]
        public string? AssignedStaffName { get; set; }

        [StringLength(20)]
        public string? AssignedStaffPhone { get; set; }

        public string? Note { get; set; }

        // Ghi chú khi thương lượng (Ví dụ: Lý do dời lịch, lý do hủy)
        public string? NegotiationNote { get; set; }

        // Các trạng thái: Pending, Confirmed, Rescheduled, Cancelled, Completed, NoShow
        [StringLength(50)]
        public string Status { get; set; } = "Pending";

        [StringLength(50)]
        public string? ResultStatus { get; set; }

        public string? ResultNote { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? UpdatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        [ForeignKey("PropertyID")]
        public virtual Property? Property { get; set; }

        [ForeignKey("ProjectID")]
        public virtual Project? Project { get; set; }

        [ForeignKey("BuyerID")]
        public virtual User? Buyer { get; set; }

        [ForeignKey("SellerID")]
        public virtual User? Seller { get; set; }

        [ForeignKey("LeadID")]
        public virtual ProjectLead? Lead { get; set; }
    }
}