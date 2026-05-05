using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.AspNetCore.Authorization;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin, Staff")] // Phân quyền quản trị
    [Route("Admin/[controller]")] // SỬA LỖI TẠI ĐÂY: Chỉ định nghĩa Route cha
    public class ContactController : Controller
    {
        private readonly ApplicationDbContext _context;

        public ContactController(ApplicationDbContext context)
        {
            _context = context;
        }

        // ==========================================
        // 1. DANH SÁCH & TÌM KIẾM LIÊN HỆ 
        // ==========================================
        [HttpGet("")]      // Cho phép truy cập bằng: /Admin/Contact
        [HttpGet("Index")] // Cho phép truy cập bằng: /Admin/Contact/Index
        public async Task<IActionResult> Index(string? keyword, string? status)
        {
            // BỘ LỌC THÉP: Chỉ lấy liên hệ công khai (Không có UserID và Không có ProjectID)
            var query = _context.ContactMessages
                .AsNoTracking()
                .Where(c => c.UserID == null && c.ProjectID == null)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                keyword = keyword.Trim().ToLower();
                query = query.Where(c => c.FullName.ToLower().Contains(keyword) ||
                                         (c.Phone != null && c.Phone.Contains(keyword)) ||
                                         (c.Email != null && c.Email.ToLower().Contains(keyword)) ||
                                         (c.Subject != null && c.Subject.ToLower().Contains(keyword)));
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(c => c.Status == status);
            }

            var contacts = await query.OrderByDescending(c => c.CreatedAt).ToListAsync();

            ViewBag.Keyword = keyword;
            ViewBag.Status = status;

            return View(contacts);
        }

        // ==========================================
        // 2. XEM CHI TIẾT
        // ==========================================
        [HttpGet("Details/{id?}")] // Xử lý chuẩn cho URL: /Admin/Contact/Details/3
        public async Task<IActionResult> Details(int id)
        {
            // Chỉ tìm đúng trong nhóm khách vãng lai
            var contact = await _context.ContactMessages
                .FirstOrDefaultAsync(c => c.ContactID == id && c.UserID == null && c.ProjectID == null);

            if (contact == null) return NotFound();

            return View(contact);
        }

        // ==========================================
        // 3. CẬP NHẬT TRẠNG THÁI
        // ==========================================
        [HttpPost("UpdateStatus")] // Xử lý cho Form submit tới: /Admin/Contact/UpdateStatus
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStatus(int id, string status)
        {
            var contact = await _context.ContactMessages
                .FirstOrDefaultAsync(c => c.ContactID == id && c.UserID == null && c.ProjectID == null);

            if (contact != null)
            {
                contact.Status = status;
                contact.UpdatedAt = System.DateTime.Now;
                await _context.SaveChangesAsync();
                TempData["SuccessMsg"] = "Cập nhật trạng thái xử lý thành công!";
            }
            else
            {
                TempData["ErrorMsg"] = "Không tìm thấy thư liên hệ hợp lệ.";
            }
            return RedirectToAction(nameof(Details), new { id = id });
        }

        // ==========================================
        // 4. XÓA TIN NHẮN
        // ==========================================
        [HttpPost("Delete")] // Xử lý cho Form submit tới: /Admin/Contact/Delete
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var contact = await _context.ContactMessages
                .FirstOrDefaultAsync(c => c.ContactID == id && c.UserID == null && c.ProjectID == null);

            if (contact != null)
            {
                _context.ContactMessages.Remove(contact);
                await _context.SaveChangesAsync();
                TempData["SuccessMsg"] = "Đã xóa tin nhắn liên hệ khỏi hệ thống.";
            }
            return RedirectToAction(nameof(Index));
        }
    }
}