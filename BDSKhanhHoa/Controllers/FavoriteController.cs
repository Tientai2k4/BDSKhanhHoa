using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Controllers
{
    [Authorize] // Bắt buộc đăng nhập mới được lưu và xem tin yêu thích
    public class FavoriteController : Controller
    {
        private readonly ApplicationDbContext _context;

        public FavoriteController(ApplicationDbContext context)
        {
            _context = context;
        }

        // ==========================================
        // 1. TRANG QUẢN LÝ DANH SÁCH TIN YÊU THÍCH
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId)) return RedirectToAction("Login", "Account");

            var favorites = await _context.Favorites
                .Include(f => f.Property)
                    .ThenInclude(p => p.PropertyType)
                .Include(f => f.Property)
                    .ThenInclude(p => p.Ward).ThenInclude(w => w.Area)
                .Where(f => f.UserID == userId && f.Property.IsDeleted == false)
                .OrderByDescending(f => f.CreatedAt)
                .ToListAsync();

            return View(favorites);
        }

        // ==========================================
        // 2. XÓA TIN KHỎI DANH SÁCH YÊU THÍCH
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Remove(int id)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId)) return RedirectToAction("Login", "Account");

            var fav = await _context.Favorites.FirstOrDefaultAsync(f => f.FavoriteID == id && f.UserID == userId);
            if (fav != null)
            {
                _context.Favorites.Remove(fav);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã gỡ bỏ bất động sản khỏi danh sách yêu thích.";
            }
            return RedirectToAction("Index");
        }

        // ==========================================
        // 3. API ĐỂ GỌI BẰNG AJAX (NÚT LƯU TIN)
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> ToggleFavorite([FromBody] int propertyId)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId))
                return Json(new { success = false, message = "Vui lòng đăng nhập để lưu tin!" });

            // Kiểm tra xem đã lưu chưa
            var existingFav = await _context.Favorites
                .FirstOrDefaultAsync(f => f.UserID == userId && f.PropertyID == propertyId);

            if (existingFav != null)
            {
                // Nếu đã lưu thì Hủy lưu (Unlike)
                _context.Favorites.Remove(existingFav);
                await _context.SaveChangesAsync();
                return Json(new { success = true, isSaved = false, message = "Đã bỏ lưu tin." });
            }
            else
            {
                // Nếu chưa lưu thì Thêm vào (Like)
                var newFav = new Favorite { UserID = userId, PropertyID = propertyId, CreatedAt = DateTime.Now };
                _context.Favorites.Add(newFav);
                await _context.SaveChangesAsync();
                return Json(new { success = true, isSaved = true, message = "Đã lưu vào danh sách yêu thích!" });
            }
        }
    }
}