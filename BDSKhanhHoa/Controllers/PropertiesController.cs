using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data; // Đảm bảo đúng namespace tới ApplicationDbContext

namespace BDSKhanhHoa.Controllers
{
    public class PropertiesController : Controller
    {
        private readonly ApplicationDbContext _context;

        public PropertiesController(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index(int? typeId, int? wardId)
        {
            // 1. Lấy danh sách BĐS (Model chính)
            var query = _context.Properties
                .Include(p => p.PropertyType)
                .Include(p => p.Ward)
                .AsQueryable();

            if (typeId.HasValue) query = query.Where(p => p.TypeID == typeId);
            if (wardId.HasValue) query = query.Where(p => p.WardID == wardId);

            var model = await query.ToListAsync();

            // 2. Lấy dữ liệu cho bộ lọc (Dùng cho các vòng lặp foreach trong View)
            ViewBag.Categories = await _context.PropertyTypes
                .Where(t => t.ParentID == null)
                .ToListAsync();

            // Lấy toàn bộ loại con để tránh truy vấn trong vòng lặp ở View
            ViewBag.AllSubTypes = await _context.PropertyTypes
                .Where(t => t.ParentID != null)
                .ToListAsync();

            ViewBag.Wards = await _context.Wards.ToListAsync();

            return View(model);
        }
    }
}