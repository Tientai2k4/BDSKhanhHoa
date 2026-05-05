using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class ProjectVaultController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _env;

        public ProjectVaultController(ApplicationDbContext context, IWebHostEnvironment env)
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

        public class VaultFileDto
        {
            public string Id { get; set; }
            public string FileName { get; set; }
            public string FilePath { get; set; }
            public string FileType { get; set; }
            public long FileSize { get; set; }
            public DateTime UploadedAt { get; set; }
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (!TryGetCurrentUserId(out int userId)) return Challenge();

            var projects = await _context.Projects
                .AsNoTracking()
                .Include(p => p.Area)
                .Where(p => p.OwnerUserID == userId && !p.IsDeleted)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            ViewBag.HasLegalDocs = projects.Count(p => !string.IsNullOrWhiteSpace(p.LegalDocsJson));
            return View(projects);
        }

        [HttpGet]
        public async Task<IActionResult> ManageVault(int id)
        {
            if (!TryGetCurrentUserId(out int userId)) return Challenge();

            var project = await _context.Projects.FirstOrDefaultAsync(p => p.ProjectID == id && p.OwnerUserID == userId);
            if (project == null) return NotFound();

            // 1. Lấy danh sách file đã duyệt
            var files = new List<VaultFileDto>();
            if (!string.IsNullOrWhiteSpace(project.LegalDocsJson))
            {
                try { files = JsonSerializer.Deserialize<List<VaultFileDto>>(project.LegalDocsJson) ?? new List<VaultFileDto>(); }
                catch { }
            }
            ViewBag.VaultFiles = files.OrderByDescending(f => f.UploadedAt).ToList();

            // 2. LẤY LỊCH SỬ YÊU CẦU (Bổ sung phần này)
            // Lọc các tin nhắn hỗ trợ có tiêu đề chứa mã dự án này
            var historyRequests = await _context.ContactMessages
                .AsNoTracking()
                .Where(m => m.UserID == userId && m.Subject.Contains($"[Dự án #{id}]"))
                .OrderByDescending(m => m.CreatedAt)
                .ToListAsync();
            ViewBag.HistoryRequests = historyRequests;

            return View(project);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RequestUpdate(int projectId, string subject, string reason, IFormFile? attachment)
        {
            if (!TryGetCurrentUserId(out int userId)) return Challenge();

            try
            {
                string? filePath = null;
                if (attachment != null && attachment.Length > 0)
                {
                    var ext = Path.GetExtension(attachment.FileName).ToLower();
                    var fileName = $"Req_{userId}_{DateTime.Now.Ticks}{ext}";
                    var uploadPath = Path.Combine(_env.WebRootPath, "uploads", "support_requests", fileName);
                    var dir = Path.GetDirectoryName(uploadPath);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    using (var stream = new FileStream(uploadPath, FileMode.Create)) { await attachment.CopyToAsync(stream); }
                    filePath = $"/uploads/support_requests/{fileName}";
                }

                var ticket = new ContactMessage
                {
                    UserID = userId,
                    FullName = User.Identity?.Name ?? "Chủ đầu tư",
                    Subject = $"[Dự án #{projectId}] {subject}",
                    Message = reason,
                    AttachmentPath = filePath,
                    CreatedAt = DateTime.Now,
                    Status = "Chưa xử lý"
                };

                _context.ContactMessages.Add(ticket);
                await _context.SaveChangesAsync();

                // Dùng thông báo ngắn gọn để tránh lỗi Encoding phức tạp
                TempData["Success"] = "Gửi yêu cầu thành công! Admin sẽ phản hồi sớm.";
            }
            catch (Exception)
            {
                TempData["Error"] = "Lỗi hệ thống, không thể gửi yêu cầu.";
            }

            return RedirectToAction(nameof(ManageVault), new { id = projectId });
        }
    }
}