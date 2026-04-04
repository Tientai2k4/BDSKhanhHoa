using System.ComponentModel.DataAnnotations;

namespace BDSKhanhHoa.Models
{
    public class Transaction
    {
        [Key]
        public int TransactionID { get; set; }
        public int UserID { get; set; }
        public decimal Amount { get; set; }
        public string? Type { get; set; }
        public string? Description { get; set; }
        public DateTime CreatedAt { get; set; }
        public virtual User? User { get; set; }
    }

 
}
