using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("Appointments")]
    public class Appointment
    {
        [Key] public int AppointmentID { get; set; }
        public int PropertyID { get; set; }
        public int BuyerID { get; set; }
        public int SellerID { get; set; }
        public DateTime AppointmentDate { get; set; }
        public string Note { get; set; }
        public string Status { get; set; } = "Pending"; // Pending, Confirmed, Cancelled
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [ForeignKey("PropertyID")] public virtual Property? Property { get; set; }
        [ForeignKey("BuyerID")] public virtual User? Buyer { get; set; }
        [ForeignKey("SellerID")] public virtual User? Seller { get; set; }
    }
}