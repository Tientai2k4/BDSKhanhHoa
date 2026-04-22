using BDSKhanhHoa.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class PackageController : Controller
    {
        private readonly ApplicationDbContext _context;

        public PackageController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> Buy()
        {
            // Lấy danh sách gói tin, sắp xếp ưu tiên hiển thị từ cao xuống thấp
            var packages = await _context.PostServicePackages
                .OrderByDescending(p => p.PriorityLevel)
                .ToListAsync();

            return View(packages);
        }
    }
}