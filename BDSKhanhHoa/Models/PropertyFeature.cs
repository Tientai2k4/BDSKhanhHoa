using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("PropertyFeatures")]
    public class PropertyFeature
    {
        [Key]
        public int FeatureID { get; set; }

        [Required]
        public int PropertyID { get; set; }

        [StringLength(255)]
        public string? FeatureName { get; set; }

        [StringLength(255)]
        public string? FeatureValue { get; set; }

        // Khóa ngoại liên kết ngược lại với bảng Properties (Tin đăng)
        [ForeignKey("PropertyID")]
        public virtual Property? Property { get; set; }
    }
}