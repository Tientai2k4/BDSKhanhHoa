using BDSKhanhHoa.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin, Staff")] // Ban Quản Trị
    public class AdminBusinessProfileController : Controller
    {
        private readonly ApplicationDbContext _context;

        public AdminBusinessProfileController(ApplicationDbContext context)
        {
            _context = context;
        }

        // 1. DANH SÁCH CHỦ ĐẦU TƯ / DOANH NGHIỆP
        public async Task<IActionResult> Index(string searchString)
        {
            var query = _context.BusinessProfiles
                .Include(b => b.User)
                .AsNoTracking();

            if (!string.IsNullOrEmpty(searchString))
            {
                searchString = searchString.ToLower();
                query = query.Where(b =>
                    b.BusinessName.ToLower().Contains(searchString) ||
                    b.TaxCode.Contains(searchString) ||
                    (b.User != null && b.User.Username.ToLower().Contains(searchString))
                );
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
                .FirstOrDefaultAsync(m => m.BusinessProfileID == id);

            if (profile == null) return NotFound();

            return View(profile);
        }

        // 3. KHÓA / MỞ KHÓA HOẠT ĐỘNG DOANH NGHIỆP
        [HttpPost]
        public async Task<IActionResult> ToggleStatus(int id)
        {
            var profile = await _context.BusinessProfiles.FindAsync(id);
            if (profile == null) return Json(new { success = false, message = "Không tìm thấy hồ sơ doanh nghiệp." });

            // Toggle giữa Approved (Đang hoạt động) và Suspended (Đình chỉ/Khóa)
            profile.VerificationStatus = (profile.VerificationStatus == "Approved") ? "Suspended" : "Approved";
            profile.UpdatedAt = DateTime.Now;

            await _context.SaveChangesAsync();
            return Json(new { success = true, status = profile.VerificationStatus });
        }
    }
}