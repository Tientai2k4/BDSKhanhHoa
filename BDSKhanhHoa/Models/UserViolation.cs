using System.ComponentModel.DataAnnotations;

namespace BDSKhanhHoa.Models
{
    public class UserViolation
    {
        [Key]
        public int ViolationID { get; set; }
        public int UserID { get; set; }
        public string? Reason { get; set; }
        public string? Description { get; set; }
        public int? ReportedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? Status { get; set; }
        public virtual User? User { get; set; }
    }

}
