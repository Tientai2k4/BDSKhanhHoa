using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("Comments")]
    public class Comment
    {
        [Key]
        public int CommentID { get; set; }

        public int PropertyID { get; set; }
        public int UserID { get; set; }

        public int? ParentID { get; set; }

        [Required(ErrorMessage = "Nội dung bình luận không được để trống")]
        public string Content { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public bool IsHidden { get; set; } = false;

        // --- Navigation Properties ---
        [ForeignKey("PropertyID")]
        public virtual Property? Property { get; set; }

        [ForeignKey("UserID")]
        public virtual User? User { get; set; }

        [ForeignKey("ParentID")]
        public virtual Comment? ParentComment { get; set; }

        public virtual ICollection<Comment> Replies { get; set; } = new List<Comment>();

        [NotMapped]
        public string TimeAgo
        {
            get
            {
                var span = DateTime.Now - CreatedAt;
                if (span.Days > 365) return $"{span.Days / 365} năm trước";
                if (span.Days > 30) return $"{span.Days / 30} tháng trước";
                if (span.Days > 0) return $"{span.Days} ngày trước";
                if (span.Hours > 0) return $"{span.Hours} giờ trước";
                if (span.Minutes > 0) return $"{span.Minutes} phút trước";
                return "Vừa xong";
            }
        }
    }
}