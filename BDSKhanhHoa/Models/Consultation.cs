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

        [ForeignKey("PropertyID")] public virtual Property? Property { get; set; }
    }
}