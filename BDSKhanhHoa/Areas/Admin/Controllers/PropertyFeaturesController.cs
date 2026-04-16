using BDSKhanhHoa.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Staff")]
    public class PropertyFeaturesController : Controller
    {
        private readonly ApplicationDbContext _context;

        public PropertyFeaturesController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string keyword = "")
        {
            var query = _context.PropertyFeatures
                .Include(f => f.Property)
                .AsQueryable();

            if (!string.IsNullOrEmpty(keyword))
            {
                query = query.Where(f => f.FeatureName.Contains(keyword) || f.FeatureValue.Contains(keyword));
            }

            var features = await query
                .OrderByDescending(f => f.PropertyID)
                .ToListAsync();

            ViewBag.Keyword = keyword;
            return View(features);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var feature = await _context.PropertyFeatures.FindAsync(id);
            if (feature != null)
            {
                _context.PropertyFeatures.Remove(feature);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã xóa tiện ích thành công!";
            }
            return RedirectToAction(nameof(Index));
        }
    }
}