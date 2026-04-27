using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("Consultations")]
    public class Consultation
    {
        [Key] public int ConsultID { get; set; }
        public string FullName { get; set; }
        public string Phone { get; set; }
        public string Email { get; set; }
        public string Note { get; set; }
        public int? PropertyID { get; set; }
        public string Status { get; set; } = "New";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public int? ProjectID { get; set; }
        public int? AssignedToUserID { get; set; }
        [StringLength(50)]
        public string? LeadType { get; set; } // "Property" hoặc "Project"

        [ForeignKey("ProjectID")]
        public virtual Project? Project { get; set; }

        [ForeignKey("AssignedToUserID")]
        public virtual User? AssignedUser { get; set; }
        [ForeignKey("PropertyID")] public virtual Property? Property { get; set; }
    }
}