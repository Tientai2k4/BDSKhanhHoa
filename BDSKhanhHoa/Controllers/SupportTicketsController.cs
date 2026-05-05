using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class SupportTicketsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _env;

        public SupportTicketsController(ApplicationDbContext context, IWebHostEnvironment env)
        {
            _context = context;
            _env = env;
        }

        private bool TryGetCurrentUserId(out int userId)
        {
            userId = 0;
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(userIdStr, out userId);
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (!TryGetCurrentUserId(out int userId)) return Challenge();

            var businessProfile = await _context.BusinessProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.UserID == userId);

            var projects = await _context.Projects
                .AsNoTracking()
                .Where(p => p.OwnerUserID == userId && !p.IsDeleted)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            // Truy xuất toàn bộ lịch sử yêu cầu hỗ trợ của CĐT này
            var supportHistory = await _context.ContactMessages
                .AsNoTracking()
                .Include(x => x.Project)
                .Where(x => x.UserID == userId)
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync();

            ViewBag.BusinessName = businessProfile?.BusinessName ?? "Doanh nghiệp đối tác";
            ViewBag.ProjectCount = projects.Count;

            ViewBag.CurrentUserID = userId;
            ViewBag.UserPhone = businessProfile?.RepresentativePhone ?? "";
            ViewBag.UserEmail = businessProfile?.BusinessEmail ?? User.Identity?.Name ?? "";

            // Truyền lịch sử ra View
            ViewBag.SupportHistory = supportHistory;

            return View(projects);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(string subject, string message, int? projectId, IFormFile? attachment, string fullName, string phone, string email)
        {
            if (!TryGetCurrentUserId(out int userId)) return Challenge();

            if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(message) || projectId == null)
            {
                TempData["Error"] = "Vui lòng nhập đầy đủ tiêu đề, nội dung và chọn dự án.";
                return RedirectToAction(nameof(Index));
            }

            // Xử lý Upload file
            string? filePath = null;
            if (attachment != null && attachment.Length > 0)
            {
                string uploadDir = Path.Combine(_env.WebRootPath, "uploads", "support_tickets");
                if (!Directory.Exists(uploadDir)) Directory.CreateDirectory(uploadDir);

                string fileName = Guid.NewGuid().ToString() + "_" + Path.GetFileName(attachment.FileName);
                string absolutePath = Path.Combine(uploadDir, fileName);

                using (var stream = new FileStream(absolutePath, FileMode.Create))
                {
                    await attachment.CopyToAsync(stream);
                }
                filePath = "/uploads/support_tickets/" + fileName;
            }

            // Lưu dữ liệu vào Database
            var ticket = new ContactMessage
            {
                UserID = userId,
                ProjectID = projectId,
                FullName = string.IsNullOrWhiteSpace(fullName) ? "Đại diện CĐT" : fullName,
                Phone = phone,
                Email = email,
                Subject = subject,
                Message = message,
                AttachmentPath = filePath,
                Status = "Pending", // Trạng thái chờ xử lý
                CreatedAt = DateTime.Now
            };

            _context.ContactMessages.Add(ticket);
            await _context.SaveChangesAsync();

            TempData["Success"] = "✅ Yêu cầu hỗ trợ đã được gửi thành công! Quý khách có thể theo dõi tiến độ ở phần Lịch sử bên dưới.";
            return RedirectToAction(nameof(Index));
        }
    }
}