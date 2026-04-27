using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("ProjectLeads")]
    public class ProjectLead
    {
        [Key]
        public int LeadID { get; set; }

        public int ProjectID { get; set; }

        [Required]
        [StringLength(100)]
        public string Name { get; set; }

        [Required]
        [StringLength(20)]
        public string Phone { get; set; }

        [StringLength(100)]
        public string? Email { get; set; }

        public string? Message { get; set; }

        [StringLength(50)]
        public string LeadStatus { get; set; } = "New"; // New, Contacted, Resolved

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // Ai là người xử lý (Chính là OwnerUserID của dự án)
        public int HandledByUserID { get; set; }

        public string? Note { get; set; } // Ghi chú cá nhân của người xử lý

        [ForeignKey("ProjectID")]
        public virtual Project? Project { get; set; }

        [ForeignKey("HandledByUserID")]
        public virtual User? Handler { get; set; }
    }
}