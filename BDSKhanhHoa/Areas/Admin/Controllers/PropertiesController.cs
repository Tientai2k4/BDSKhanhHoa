using BDSKhanhHoa.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Staff")]
    public class PropertiesController : Controller
    {
        private readonly ApplicationDbContext _context;

        public PropertiesController(ApplicationDbContext context)
        {
            _context = context;
        }

        // ==========================================
        // 1. TỔNG HỢP DANH SÁCH TIN ĐĂNG
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Index(string status = "")
        {
            var query = _context.Properties
                .Include(p => p.User)
                .Include(p => p.PropertyType)
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Where(p => p.IsDeleted == false) // Loại bỏ tin đã xóa mềm
                .AsQueryable();

            if (!string.IsNullOrEmpty(status))
            {
                query = query.Where(p => p.Status == status);
            }

            var properties = await query
                .OrderBy(p => p.Status == "Pending" ? 0 : 1) // Chờ duyệt xếp lên đầu
                .ThenByDescending(p => p.CreatedAt)
                .ToListAsync();

            ViewBag.CurrentStatus = status;
            ViewBag.PendingCount = await _context.Properties.CountAsync(p => p.Status == "Pending" && p.IsDeleted == false);

            return View("Index", properties);
        }

        // ==========================================
        // 2. MÀN HÌNH KIỂM DUYỆT NHANH (SHORTCUT)
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Verify()
        {
            var pendingProperties = await _context.Properties
                .Include(p => p.User)
                .Include(p => p.PropertyType)
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Where(p => p.Status == "Pending" && p.IsDeleted == false)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            ViewBag.CurrentStatus = "Pending";
            ViewBag.PendingCount = pendingProperties.Count;

            // Bắt buộc trả về View Index để dùng chung giao diện
            return View("Index", pendingProperties);
        }

        // ==========================================
        // 3. HÀM DUYỆT / TỪ CHỐI (CÓ NHẬN LÝ DO)
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStatus(int id, string newStatus, string? reason)
        {
            var property = await _context.Properties.FindAsync(id);
            if (property == null)
            {
                TempData["Error"] = "Không tìm thấy thông tin bất động sản này!";
                return RedirectToAction(nameof(Index));
            }

            if (newStatus == "Approved" || newStatus == "Rejected")
            {
                property.Status = newStatus;
                property.UpdatedAt = DateTime.Now;

                // Xử lý lưu lý do từ chối
                if (newStatus == "Rejected")
                {
                    property.RejectionReason = string.IsNullOrEmpty(reason)
                        ? "Tin đăng vi phạm chính sách hoặc sai thông tin."
                        : reason;
                }
                else
                {
                    // Nếu tin được duyệt, xóa lý do cũ đi
                    property.RejectionReason = null;
                }

                await _context.SaveChangesAsync();

                TempData["Success"] = newStatus == "Approved"
                    ? $"Đã phê duyệt tin: {property.Title}"
                    : $"Đã từ chối tin: {property.Title}";
            }

            // Giữ Admin ở lại trang hiện tại (Pending hoặc Index)
            string referer = Request.Headers["Referer"].ToString();
            if (!string.IsNullOrEmpty(referer)) return Redirect(referer);

            return RedirectToAction(nameof(Index));
        }

        // ==========================================
        // 4. XÓA TIN ĐĂNG VĨNH VIỄN (HOẶC SOFT DELETE TÙY BẠN)
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var property = await _context.Properties.FindAsync(id);
            if (property != null)
            {
                property.IsDeleted = true; // Xóa mềm
                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã đưa tin đăng vào thùng rác thành công!";
            }
            else
            {
                TempData["Error"] = "Không tìm thấy tin đăng để xóa.";
            }
            return RedirectToAction(nameof(Index));
        }
    }
}