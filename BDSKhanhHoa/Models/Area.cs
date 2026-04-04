using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("Areas")]
    public class Area
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int AreaID { get; set; }

        [Required(ErrorMessage = "Tên khu vực là bắt buộc.")]
        [StringLength(100)]
        public string? AreaName { get; set; } // Thêm dấu ? để binding linh hoạt

        [StringLength(255)]
        public string? Description { get; set; }

        public virtual ICollection<Ward>? Wards { get; set; }
    }
}