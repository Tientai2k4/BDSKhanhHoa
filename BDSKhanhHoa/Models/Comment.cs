using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("Comments")]
    public class Comment
    {
        [Key] public int CommentID { get; set; }
        public int PropertyID { get; set; }
        public int UserID { get; set; }
        public string Content { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public bool IsHidden { get; set; } = false;

        [ForeignKey("PropertyID")] public virtual Property? Property { get; set; }
        [ForeignKey("UserID")] public virtual User? User { get; set; }
    }
}