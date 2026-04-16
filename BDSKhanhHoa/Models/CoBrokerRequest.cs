using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("CoBrokerRequests")]
    public class CoBrokerRequest
    {
        [Key] public int RequestID { get; set; }
        public int PropertyID { get; set; }
        public int OwnerID { get; set; }
        public int RequesterID { get; set; }
        public string Message { get; set; }
        public string Status { get; set; } = "Pending";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        [ForeignKey("PropertyID")] public virtual Property? Property { get; set; }
        [ForeignKey("OwnerID")] public virtual User? Owner { get; set; }
        [ForeignKey("RequesterID")] public virtual User? Requester { get; set; }
    }
}