using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin, Staff")] // Chỉ có BQT mới được xem
    public class AdminBusinessProfileController : Controller
    {
        private readonly ApplicationDbContext _context;

        public AdminBusinessProfileController(ApplicationDbContext context)
        {
            _context = context;
        }

        // 1. DANH SÁCH CHỦ ĐẦU TƯ / PHÁP NHÂN
        public async Task<IActionResult> Index(string searchString)
        {
            var query = _context.BusinessProfiles
                .Include(b => b.User)
                .AsNoTracking();

            if (!string.IsNullOrEmpty(searchString))
            {
                query = query.Where(b => b.BusinessName.Contains(searchString) || b.TaxCode.Contains(searchString));
            }

            ViewData["CurrentFilter"] = searchString;
            return View(await query.OrderByDescending(b => b.CreatedAt).ToListAsync());
        }

        // 2. CHI TIẾT HỒ SƠ PHÁP NHÂN
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null) return NotFound();

            var profile = await _context.BusinessProfiles
                .Include(b => b.User)
                .Include(b => b.Reviewer)
                .FirstOrDefaultAsync(m => m.BusinessProfileID == id);

            if (profile == null) return NotFound();

            return View(profile);
        }

        // 3. XỬ LÝ KHÓA QUYỀN DOANH NGHIỆP (Nếu phát hiện vi phạm)
        [HttpPost]
        public async Task<IActionResult> ToggleStatus(int id)
        {
            var profile = await _context.BusinessProfiles.FindAsync(id);
            if (profile == null) return Json(new { success = false });

            // Chuyển đổi trạng thái giữa Approved và Rejected (Khóa)
            profile.VerificationStatus = (profile.VerificationStatus == "Approved") ? "Rejected" : "Approved";
            profile.UpdatedAt = DateTime.Now;

            await _context.SaveChangesAsync();
            return Json(new { success = true, status = profile.VerificationStatus });
        }
    }
}