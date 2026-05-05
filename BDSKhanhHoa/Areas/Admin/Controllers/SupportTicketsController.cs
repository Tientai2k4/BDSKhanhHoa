using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")] // Chỉ Admin mới được quản lý
    [Route("Admin/[controller]/[action]")]
    public class SupportTicketsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public SupportTicketsController(ApplicationDbContext context)
        {
            _context = context;
        }

        private bool TryGetCurrentUserId(out int userId)
        {
            userId = 0;
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(userIdStr, out userId);
        }

        [HttpGet]
        public async Task<IActionResult> Index(string tab = "all", string? status = null, string? keyword = null)
        {
            if (!TryGetCurrentUserId(out _)) return Challenge();

            // Lấy danh sách tên dự án để map ID -> Tên (dùng cho bảng tư vấn)
            var projectNames = await _context.Projects
                .AsNoTracking()
                .Select(p => new { p.ProjectID, p.ProjectName })
                .ToDictionaryAsync(x => x.ProjectID, x => x.ProjectName);

            var consultationsQuery = _context.Consultations.AsNoTracking().Where(c => c.ProjectID != null).AsQueryable();
            var contactMessagesQuery = _context.ContactMessages.AsNoTracking().Include(x => x.Project).AsQueryable();
            var leadsQuery = _context.ProjectLeads.AsNoTracking().Include(x => x.Project).AsQueryable();

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                keyword = keyword.Trim();
                consultationsQuery = consultationsQuery.Where(x => x.FullName.Contains(keyword) || x.Phone.Contains(keyword) || x.Note.Contains(keyword));
                contactMessagesQuery = contactMessagesQuery.Where(x => x.FullName.Contains(keyword) || x.Subject.Contains(keyword) || x.Message.Contains(keyword));
                leadsQuery = leadsQuery.Where(x => x.Name.Contains(keyword) || x.Phone.Contains(keyword) || x.Project.ProjectName.Contains(keyword));
            }

            if (!string.IsNullOrWhiteSpace(status) && !status.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                consultationsQuery = consultationsQuery.Where(x => x.Status == status);
                leadsQuery = leadsQuery.Where(x => x.LeadStatus == status);
            }

            if (tab.Equals("tu-van", StringComparison.OrdinalIgnoreCase))
            {
                contactMessagesQuery = contactMessagesQuery.Where(x => false);
                leadsQuery = leadsQuery.Where(x => false);
            }
            else if (tab.Equals("ho-tro", StringComparison.OrdinalIgnoreCase))
            {
                consultationsQuery = consultationsQuery.Where(x => false);
                leadsQuery = leadsQuery.Where(x => false);
            }
            else if (tab.Equals("leads", StringComparison.OrdinalIgnoreCase))
            {
                consultationsQuery = consultationsQuery.Where(x => false);
                contactMessagesQuery = contactMessagesQuery.Where(x => false);
            }

            ViewBag.Consultations = await consultationsQuery.OrderByDescending(x => x.CreatedAt).Take(50).ToListAsync();
            ViewBag.ProjectLeads = await leadsQuery.OrderByDescending(x => x.CreatedAt).Take(50).ToListAsync();

            // TÁCH LÀM 2 DANH SÁCH: ĐANG CHỜ VÀ ĐÃ XỬ LÝ LỊCH SỬ
            ViewBag.PendingContacts = await contactMessagesQuery
                .Where(x => x.Status != "Done" && x.Status != "Đã xử lý")
                .OrderByDescending(x => x.CreatedAt).ToListAsync();

            ViewBag.ResolvedContacts = await contactMessagesQuery
                .Where(x => x.Status == "Done" || x.Status == "Đã xử lý")
                .OrderByDescending(x => x.UpdatedAt) // Sắp xếp theo ngày Update mới nhất
                .Take(100)
                .ToListAsync();

            // THỐNG KÊ SỐ LẦN CHỈNH SỬA CỦA TỪNG DỰ ÁN DỰA TRÊN LỊCH SỬ ĐÃ XONG
            var projectEditCounts = await _context.ContactMessages
                .Where(x => x.ProjectID != null && (x.Status == "Done" || x.Status == "Đã xử lý"))
                .GroupBy(x => x.ProjectID.Value)
                .Select(g => new { ProjectID = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ProjectID, x => x.Count);

            ViewBag.ProjectEditCounts = projectEditCounts;
            ViewBag.ProjectNames = projectNames;

            // Đếm số lượng hiển thị trên Badge
            ViewBag.ConsultationCount = await _context.Consultations.CountAsync(c => c.ProjectID != null);
            ViewBag.ContactMessageCount = await _context.ContactMessages.CountAsync(x => x.Status != "Done" && x.Status != "Đã xử lý");
            ViewBag.ProjectLeadCount = await _context.ProjectLeads.CountAsync();

            ViewBag.ActiveTab = tab;
            ViewBag.Status = status;
            ViewBag.Keyword = keyword;

            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateConsultationStatus(int id, string status, string? returnUrl = null)
        {
            var item = await _context.Consultations.FirstOrDefaultAsync(x => x.ConsultID == id);
            if (item != null)
            {
                item.Status = status?.Trim();
                _context.Consultations.Update(item);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Cập nhật yêu cầu tư vấn thành công.";
            }
            return SafeRedirect(returnUrl);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateLeadStatus(int id, string status, string? returnUrl = null)
        {
            var item = await _context.ProjectLeads.FirstOrDefaultAsync(x => x.LeadID == id);
            if (item != null)
            {
                item.LeadStatus = status?.Trim();
                _context.ProjectLeads.Update(item);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Cập nhật tiến độ Lead thành công.";
            }
            return SafeRedirect(returnUrl);
        }

        private IActionResult SafeRedirect(string? returnUrl)
        {
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
            return RedirectToAction(nameof(Index));
        }

        // HÀM XỬ LÝ KHÉP KÍN: ADMIN ĐÁNH DẤU HOÀN TẤT VÀ BÁO VỀ CHO CĐT
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcessSupportRequest(int id)
        {
            var ticket = await _context.ContactMessages.FindAsync(id);
            if (ticket == null) return NotFound();

            ticket.Status = "Done";
            ticket.UpdatedAt = DateTime.Now; // Cập nhật thời gian hoàn tất
            _context.Update(ticket);

            // Gửi thông báo lại cho CDT
            if (ticket.UserID.HasValue)
            {
                _context.Notifications.Add(new Notification
                {
                    UserID = ticket.UserID.Value,
                    Title = "Yêu cầu cập nhật hồ sơ thành công",
                    Content = $"Yêu cầu '{ticket.Subject}' của bạn đã được Admin phê duyệt và cập nhật lên hệ thống thành công.",
                    CreatedAt = DateTime.Now,
                    IsRead = false
                });
            }

            await _context.SaveChangesAsync();
            TempData["Success"] = "Đã xác nhận xử lý xong. Dữ liệu đã được lưu vào Lịch sử và thông báo đã được gửi đến Chủ đầu tư.";
            return RedirectToAction(nameof(Index), new { tab = "ho-tro" });
        }
    }
}