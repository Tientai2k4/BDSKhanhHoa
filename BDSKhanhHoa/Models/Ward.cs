using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    public class Ward
    {
        [Key]
        public int WardID { get; set; }

        [Required]
        public string WardName { get; set; }

        public int AreaID { get; set; }

        // Thiết lập mối quan hệ với bảng Area (Nếu cần)
        [ForeignKey("AreaID")]
        public virtual Area? Area { get; set; }
    }
}