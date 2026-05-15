using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin, Staff")]
    [Route("Admin/[controller]")]
    public class ContactController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IAuditLogService _auditLogService; // Thêm Service Log

        public ContactController(ApplicationDbContext context, IAuditLogService auditLogService)
        {
            _context = context;
            _auditLogService = auditLogService;
        }

        [HttpGet("")]
        [HttpGet("Index")]
        public async Task<IActionResult> Index(string? keyword, string? status)
        {
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

        [HttpGet("Details/{id?}")]
        public async Task<IActionResult> Details(int id)
        {
            var contact = await _context.ContactMessages
                .FirstOrDefaultAsync(c => c.ContactID == id && c.UserID == null && c.ProjectID == null);

            if (contact == null) return NotFound();

            return View(contact);
        }

        [HttpPost("UpdateStatus")]
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

                // GHI LOG
                int userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
                await _auditLogService.LogAsync(userId, "Cập nhật trạng thái liên hệ", "Contact", $"ContactID: {id} -> {status}", severity: "Info");

                TempData["SuccessMsg"] = "Cập nhật trạng thái xử lý thành công!";
            }
            else
            {
                TempData["ErrorMsg"] = "Không tìm thấy thư liên hệ hợp lệ.";
            }
            return RedirectToAction(nameof(Details), new { id = id });
        }

        [HttpPost("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var contact = await _context.ContactMessages
                .FirstOrDefaultAsync(c => c.ContactID == id && c.UserID == null && c.ProjectID == null);

            if (contact != null)
            {
                _context.ContactMessages.Remove(contact);
                await _context.SaveChangesAsync();

                // GHI LOG
                int userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
                await _auditLogService.LogAsync(userId, "Xóa tin nhắn liên hệ", "Contact", $"ContactID: {id}", severity: "Warning");

                TempData["SuccessMsg"] = "Đã xóa tin nhắn liên hệ khỏi hệ thống.";
            }
            return RedirectToAction(nameof(Index));
        }
    }
}