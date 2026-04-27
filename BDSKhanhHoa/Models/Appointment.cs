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

        // Có thể gắn theo Property hoặc theo Project, tùy ngữ cảnh tạo lịch hẹn
        public int? PropertyID { get; set; }

        public int? ProjectID { get; set; }

        // Người đang thao tác trong hệ thống
        [Required]
        public int BuyerID { get; set; }

        // Chủ dự án / người phụ trách lịch
        [Required]
        public int SellerID { get; set; }

        // Nếu lịch hẹn được tạo từ lead CRM
        public int? LeadID { get; set; }

        [StringLength(255)]
        public string? CustomerName { get; set; }

        [StringLength(20)]
        public string? CustomerPhone { get; set; }

        [StringLength(255)]
        public string? CustomerEmail { get; set; }

        [Required]
        public DateTime AppointmentDate { get; set; }

        [StringLength(255)]
        public string? MeetingLocation { get; set; }

        [StringLength(255)]
        public string? AssignedStaffName { get; set; }

        [StringLength(20)]
        public string? AssignedStaffPhone { get; set; }

        public string? Note { get; set; }

        [StringLength(50)]
        public string Status { get; set; } = "Pending"; // Pending, Confirmed, Cancelled, Completed, Rescheduled, NoShow

        [StringLength(50)]
        public string? ResultStatus { get; set; } // Interested, NotInterested, DepositPending, FollowUp

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