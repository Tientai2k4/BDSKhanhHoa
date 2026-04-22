using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class ConsultationsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public ConsultationsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // ==========================================
        // 1. GET: /Consultations/Index (Trang quản lý chính)
        // Bổ sung Tìm kiếm, Lọc và Phân trang
        // ==========================================
        public async Task<IActionResult> Index(string searchString, string statusFilter, int page = 1)
        {
            if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int currentUserId))
            {
                return RedirectToAction("Login", "Account");
            }

            int pageSize = 10; // Số lượng hiển thị trên 1 trang

            // Lấy Query gốc: Các yêu cầu tư vấn gửi đến BĐS của người bán này
            var query = _context.Consultations
                .Include(c => c.Property)
                .Where(c => c.Property != null && c.Property.UserID == currentUserId)
                .AsQueryable();

            // --- Thống kê nhanh truyền ra View ---
            ViewBag.TotalLeads = await query.CountAsync();
            ViewBag.NewLeads = await query.CountAsync(c => c.Status == "New");
            ViewBag.ContactedLeads = await query.CountAsync(c => c.Status == "Contacted");

            // --- Lọc theo Trạng thái ---
            if (!string.IsNullOrEmpty(statusFilter) && statusFilter != "All")
            {
                query = query.Where(c => c.Status == statusFilter);
            }

            // --- Tìm kiếm theo Tên hoặc Số điện thoại ---
            if (!string.IsNullOrEmpty(searchString))
            {
                var lowerSearch = searchString.ToLower();
                query = query.Where(c =>
                    (c.FullName != null && c.FullName.ToLower().Contains(lowerSearch)) ||
                    (c.Phone != null && c.Phone.Contains(searchString))
                );
            }

            // --- Sắp xếp và Phân trang ---
            int totalItems = await query.CountAsync();
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
            page = page < 1 ? 1 : (page > totalPages && totalPages > 0 ? totalPages : page);

            var leads = await query
                .OrderByDescending(c => c.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            // Truyền dữ liệu phân trang và filter về View để giữ trạng thái
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.SearchString = searchString;
            ViewBag.StatusFilter = statusFilter;

            return View(leads);
        }

        // ==========================================
        // 2. POST: /Consultations/Create (API nhận form từ Modal)
        // Dành cho người mua gửi yêu cầu từ trang Chi tiết BĐS
        // ==========================================
        [HttpPost]
        [AllowAnonymous] // Cho phép khách chưa đăng nhập cũng có thể gửi yêu cầu
        public async Task<IActionResult> Create(string fullName, string phone, string email, string note, int? propertyId)
        {
            try
            {
                if (string.IsNullOrEmpty(phone))
                {
                    return Json(new { success = false, message = "Số điện thoại là bắt buộc!" });
                }

                var consultation = new Consultation
                {
                    FullName = fullName,
                    Phone = phone,
                    Email = email,
                    Note = note,
                    PropertyID = propertyId,
                    Status = "New",
                    CreatedAt = DateTime.Now
                };

                _context.Consultations.Add(consultation);
                await _context.SaveChangesAsync();

                return Json(new { success = true, message = "Đã gửi yêu cầu tư vấn thành công. Người bán sẽ sớm liên hệ với bạn!" });
            }
            catch (Exception ex)
            {
                // Ghi log lỗi nếu cần thiết
                return Json(new { success = false, message = "Đã xảy ra lỗi hệ thống: " + ex.Message });
            }
        }

        // ==========================================
        // 3. POST: /Consultations/UpdateStatus (AJAX cập nhật trạng thái)
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> UpdateStatus(int id, string newStatus)
        {
            if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int currentUserId))
                return Json(new { success = false, message = "Vui lòng đăng nhập!" });

            var consultation = await _context.Consultations
                .Include(c => c.Property)
                .FirstOrDefaultAsync(c => c.ConsultID == id);

            if (consultation == null)
                return Json(new { success = false, message = "Không tìm thấy dữ liệu!" });

            // Kiểm tra bảo mật: Chỉ chủ nhà mới được đổi trạng thái
            if (consultation.Property == null || consultation.Property.UserID != currentUserId)
                return Json(new { success = false, message = "Bạn không có quyền thao tác trên dữ liệu này!" });

            consultation.Status = newStatus;
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Đã cập nhật trạng thái thành công!" });
        }

        // ==========================================
        // 4. POST: /Consultations/Delete (AJAX Xóa thư rác)
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int currentUserId))
                return Json(new { success = false, message = "Vui lòng đăng nhập!" });

            var consultation = await _context.Consultations
                .Include(c => c.Property)
                .FirstOrDefaultAsync(c => c.ConsultID == id);

            if (consultation == null)
                return Json(new { success = false, message = "Dữ liệu không tồn tại!" });

            // Kiểm tra quyền
            if (consultation.Property == null || consultation.Property.UserID != currentUserId)
                return Json(new { success = false, message = "Bạn không có quyền xóa dữ liệu này!" });

            _context.Consultations.Remove(consultation);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Đã xóa yêu cầu tư vấn!" });
        }

        // ==========================================
        // 5. GET: /Consultations/GetDetails (AJAX lấy chi tiết Lời nhắn)
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> GetDetails(int id)
        {
            var consultation = await _context.Consultations
                .Include(c => c.Property)
                .FirstOrDefaultAsync(c => c.ConsultID == id);

            if (consultation == null) return NotFound();

            return Json(new
            {
                fullName = consultation.FullName ?? "Khách vãng lai",
                phone = consultation.Phone,
                email = consultation.Email ?? "Không có",
                note = consultation.Note ?? "Không có lời nhắn",
                propertyTitle = consultation.Property?.Title ?? "BĐS không xác định", // Giả sử model Property có cột Title
                createdAt = consultation.CreatedAt.ToString("dd/MM/yyyy HH:mm"),
                status = consultation.Status
            });
        }
    }
}