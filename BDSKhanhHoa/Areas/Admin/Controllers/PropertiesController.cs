using BDSKhanhHoa.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    // BẮT BUỘC: Đánh dấu Controller này thuộc khu vực Admin
    [Area("Admin")]
    // Bảo mật: Chỉ Admin hoặc Staff (Nhân viên) mới được vào
    [Authorize(Roles = "Admin,Staff")]
    public class PropertiesController : Controller
    {
        private readonly ApplicationDbContext _context;

        public PropertiesController(ApplicationDbContext context)
        {
            _context = context;
        }

        // ==========================================
        // 1. DANH SÁCH TIN ĐĂNG (TRANG QUẢN LÝ)
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Index(string status = "")
        {
            var query = _context.Properties
                .Include(p => p.User)
                .Include(p => p.PropertyType)
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .AsQueryable();

            // Lọc theo trạng thái nếu có bấm nút Lọc
            if (!string.IsNullOrEmpty(status))
            {
                query = query.Where(p => p.Status == status);
            }

            var properties = await query
                .OrderByDescending(p => p.CreatedAt) // Sắp xếp tin mới nhất lên đầu
                .ToListAsync();

            ViewBag.CurrentStatus = status;
            return View(properties);
        }

        // ==========================================
        // 2. XỬ LÝ DUYỆT / TỪ CHỐI TIN ĐĂNG
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> UpdateStatus(int id, string newStatus)
        {
            var property = await _context.Properties.FindAsync(id);
            if (property == null)
            {
                TempData["Error"] = "Không tìm thấy bất động sản!";
                return RedirectToAction(nameof(Index));
            }

            // Chỉ cho phép các trạng thái hợp lệ
            if (newStatus == "Approved" || newStatus == "Rejected" || newStatus == "Pending")
            {
                property.Status = newStatus;
                property.UpdatedAt = DateTime.Now;
                await _context.SaveChangesAsync();

                TempData["Success"] = $"Đã cập nhật trạng thái tin thành: {(newStatus == "Approved" ? "Đã duyệt" : "Đã từ chối")}";
            }

            return RedirectToAction(nameof(Index));
        }

        // ==========================================
        // 3. XÓA TIN ĐĂNG (Đưa vào thùng rác)
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            var property = await _context.Properties.FindAsync(id);
            if (property != null)
            {
                property.IsDeleted = true; // Xóa mềm (Soft Delete)
                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã xóa tin đăng thành công!";
            }
            return RedirectToAction(nameof(Index));
        }
    }
}