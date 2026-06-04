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
        private const string STATUS_NEW = "Chưa xử lý";
        private const string STATUS_PROCESSING = "Đang xử lý";
        private const string STATUS_DONE = "Đã xử lý";

        private readonly ApplicationDbContext _context;
        private readonly IAuditLogService _auditLogService;

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

                query = query.Where(c =>
                    c.FullName.ToLower().Contains(keyword) ||
                    (c.Phone != null && c.Phone.Contains(keyword)) ||
                    (c.Email != null && c.Email.ToLower().Contains(keyword)) ||
                    (c.Subject != null && c.Subject.ToLower().Contains(keyword)) ||
                    (c.Message != null && c.Message.ToLower().Contains(keyword)));
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                status = NormalizeStatus(status);

                if (IsValidStatus(status))
                {
                    if (status == STATUS_NEW)
                    {
                        query = query.Where(c =>
                            c.Status == null || c.Status == "" ||
                            c.Status == STATUS_NEW ||
                            c.Status == "Moi" ||
                            c.Status == "New" ||
                            c.Status == "Pending" ||
                            c.Status == "Chua xu ly");
                    }
                    else if (status == STATUS_PROCESSING)
                    {
                        query = query.Where(c =>
                            c.Status == STATUS_PROCESSING ||
                            c.Status == "Dang xu ly" ||
                            c.Status == "Processing");
                    }
                    else if (status == STATUS_DONE)
                    {
                        query = query.Where(c =>
                            c.Status == STATUS_DONE ||
                            c.Status == "Da xu ly" ||
                            c.Status == "Done" ||
                            c.Status == "Completed");
                    }
                }
            }

            /*
                KHÔNG gọi GetStatusOrder(c.Status) trực tiếp trong OrderBy.
                EF Core không thể dịch method C# tự viết sang SQL nên sẽ lỗi:
                "The LINQ expression ... ContactController.GetStatusOrder(c.Status) could not be translated".

                Cách sửa: viết điều kiện sắp xếp trực tiếp bằng biểu thức EF có thể dịch được.
                Sau khi lấy dữ liệu về RAM, chuẩn hóa lại Status để View hiển thị thống nhất.
            */
            var contacts = await query
                .OrderBy(c =>
                    c.Status == null || c.Status == "" ||
                    c.Status == STATUS_NEW ||
                    c.Status == "Moi" ||
                    c.Status == "New" ||
                    c.Status == "Pending" ||
                    c.Status == "Chua xu ly"
                        ? 1
                        : c.Status == STATUS_PROCESSING ||
                          c.Status == "Dang xu ly" ||
                          c.Status == "Processing"
                            ? 2
                            : c.Status == STATUS_DONE ||
                              c.Status == "Da xu ly" ||
                              c.Status == "Done" ||
                              c.Status == "Completed"
                                ? 3
                                : 4)
                .ThenByDescending(c => c.CreatedAt)
                .ToListAsync();

            foreach (var contact in contacts)
            {
                contact.Status = NormalizeStatus(contact.Status);
            }

            ViewBag.Keyword = keyword;
            ViewBag.Status = status;

            ViewBag.NewCount = await _context.ContactMessages
                .AsNoTracking()
                .CountAsync(c =>
                    c.UserID == null &&
                    c.ProjectID == null &&
                    (c.Status == null || c.Status == "" ||
                     c.Status == STATUS_NEW ||
                     c.Status == "Moi" ||
                     c.Status == "New" ||
                     c.Status == "Pending" ||
                     c.Status == "Chua xu ly"));

            ViewBag.ProcessingCount = await _context.ContactMessages
                .AsNoTracking()
                .CountAsync(c =>
                    c.UserID == null &&
                    c.ProjectID == null &&
                    (c.Status == STATUS_PROCESSING ||
                     c.Status == "Dang xu ly" ||
                     c.Status == "Processing"));

            ViewBag.DoneCount = await _context.ContactMessages
                .AsNoTracking()
                .CountAsync(c =>
                    c.UserID == null &&
                    c.ProjectID == null &&
                    (c.Status == STATUS_DONE ||
                     c.Status == "Da xu ly" ||
                     c.Status == "Done" ||
                     c.Status == "Completed"));

            return View(contacts);
        }

        [HttpGet("Details/{id:int}")]
        public async Task<IActionResult> Details(int id)
        {
            var contact = await _context.ContactMessages
                .FirstOrDefaultAsync(c =>
                    c.ContactID == id &&
                    c.UserID == null &&
                    c.ProjectID == null);

            if (contact == null)
            {
                TempData["ErrorMsg"] = "Không tìm thấy thư liên hệ hợp lệ.";
                return RedirectToAction(nameof(Index));
            }

            contact.Status = NormalizeStatus(contact.Status);

            return View(contact);
        }

        [HttpPost("UpdateStatus")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStatus(int id, string status)
        {
            status = NormalizeStatus(status);

            if (!IsValidStatus(status))
            {
                TempData["ErrorMsg"] = "Trạng thái cập nhật không hợp lệ.";
                return RedirectToAction(nameof(Details), new { id });
            }

            var contact = await _context.ContactMessages
                .FirstOrDefaultAsync(c =>
                    c.ContactID == id &&
                    c.UserID == null &&
                    c.ProjectID == null);

            if (contact == null)
            {
                TempData["ErrorMsg"] = "Không tìm thấy thư liên hệ hợp lệ.";
                return RedirectToAction(nameof(Index));
            }

            string oldStatus = NormalizeStatus(contact.Status);

            if (oldStatus == STATUS_DONE)
            {
                TempData["ErrorMsg"] = "Thư liên hệ đã xử lý xong nên không thể chuyển lại trạng thái trước đó.";
                return RedirectToAction(nameof(Details), new { id });
            }

            if (oldStatus == status)
            {
                TempData["InfoMsg"] = "Thư liên hệ đang ở trạng thái này, không có thay đổi mới.";
                return RedirectToAction(nameof(Details), new { id });
            }

            if (!CanMoveStatus(oldStatus, status))
            {
                TempData["ErrorMsg"] = $"Không thể chuyển trạng thái từ \"{oldStatus}\" sang \"{status}\". Quy trình xử lý chỉ được đi theo thứ tự: Chưa xử lý → Đang xử lý → Đã xử lý.";
                return RedirectToAction(nameof(Details), new { id });
            }

            contact.Status = status;
            contact.UpdatedAt = DateTime.Now;

            await _context.SaveChangesAsync();

            int userId = GetCurrentUserId();

            await _auditLogService.LogAsync(
                userId,
                "Cập nhật trạng thái liên hệ",
                "Contact",
                $"ContactID: {id} | {oldStatus} -> {status}",
                severity: status == STATUS_DONE ? "Info" : "Info");

            TempData["SuccessMsg"] = $"Đã chuyển trạng thái từ \"{oldStatus}\" sang \"{status}\".";

            return RedirectToAction(nameof(Details), new { id });
        }

        [HttpPost("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var contact = await _context.ContactMessages
                .FirstOrDefaultAsync(c =>
                    c.ContactID == id &&
                    c.UserID == null &&
                    c.ProjectID == null);

            if (contact == null)
            {
                TempData["ErrorMsg"] = "Không tìm thấy thư liên hệ cần xóa.";
                return RedirectToAction(nameof(Index));
            }

            _context.ContactMessages.Remove(contact);
            await _context.SaveChangesAsync();

            int userId = GetCurrentUserId();

            await _auditLogService.LogAsync(
                userId,
                "Xóa tin nhắn liên hệ",
                "Contact",
                $"ContactID: {id} | Status: {contact.Status}",
                severity: "Warning");

            TempData["SuccessMsg"] = "Đã xóa tin nhắn liên hệ khỏi hệ thống.";
            return RedirectToAction(nameof(Index));
        }

        private static bool CanMoveStatus(string oldStatus, string newStatus)
        {
            oldStatus = NormalizeStatus(oldStatus);
            newStatus = NormalizeStatus(newStatus);

            if (oldStatus == STATUS_NEW)
            {
                return newStatus == STATUS_PROCESSING || newStatus == STATUS_DONE;
            }

            if (oldStatus == STATUS_PROCESSING)
            {
                return newStatus == STATUS_DONE;
            }

            return false;
        }

        private static bool IsValidStatus(string status)
        {
            status = NormalizeStatus(status);

            return status == STATUS_NEW ||
                   status == STATUS_PROCESSING ||
                   status == STATUS_DONE;
        }

        private static string NormalizeStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return STATUS_NEW;
            }

            status = status.Trim();

            return status switch
            {
                "Moi" => STATUS_NEW,
                "New" => STATUS_NEW,
                "Pending" => STATUS_NEW,
                "Chua xu ly" => STATUS_NEW,
                "Chưa xử lý" => STATUS_NEW,

                "Dang xu ly" => STATUS_PROCESSING,
                "Processing" => STATUS_PROCESSING,
                "Đang xử lý" => STATUS_PROCESSING,

                "Da xu ly" => STATUS_DONE,
                "Done" => STATUS_DONE,
                "Completed" => STATUS_DONE,
                "Đã xử lý" => STATUS_DONE,

                _ => status
            };
        }

        private static int GetStatusOrder(string? status)
        {
            status = NormalizeStatus(status);

            return status switch
            {
                STATUS_NEW => 1,
                STATUS_PROCESSING => 2,
                STATUS_DONE => 3,
                _ => 4
            };
        }

        private int GetCurrentUserId()
        {
            string? userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(userIdStr, out int userId) ? userId : 0;
        }
    }
}