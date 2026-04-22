using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    public class UserMessage
    {
        [Key]
        public int MessageID { get; set; }

        public int SenderID { get; set; }
        [ForeignKey("SenderID")]
        public User? Sender { get; set; }

        public int ReceiverID { get; set; }
        [ForeignKey("ReceiverID")]
        public User? Receiver { get; set; }

        public int PropertyID { get; set; }
        [ForeignKey("PropertyID")]
        public Property? Property { get; set; }

        [Required]
        public string MessageContent { get; set; }

        public bool IsRead { get; set; }
        public DateTime? CreatedAt { get; set; }
    }
}