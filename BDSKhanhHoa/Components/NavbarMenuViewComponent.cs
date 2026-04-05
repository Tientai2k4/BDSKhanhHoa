using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using System.Threading.Tasks;
using System.Linq;

namespace BDSKhanhHoa.Components
{
    public class NavbarMenuViewComponent : ViewComponent
    {
        private readonly ApplicationDbContext _context;

        public NavbarMenuViewComponent(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IViewComponentResult> InvokeAsync()
        {
            var allTypes = await _context.PropertyTypes.ToListAsync();

            // ID 1: Mua bán, ID 2: Cho thuê (như logic database bạn đã tạo)
            var model = new NavbarMenuViewModel
            {
                BuyCategories = allTypes.Where(t => t.ParentID == 1).ToList(),
                RentCategories = allTypes.Where(t => t.ParentID == 2).ToList()
            };

            return View(model);
        }
    }

    public class NavbarMenuViewModel
    {
        public List<BDSKhanhHoa.Models.PropertyType> BuyCategories { get; set; }
        public List<BDSKhanhHoa.Models.PropertyType> RentCategories { get; set; }
    }
}