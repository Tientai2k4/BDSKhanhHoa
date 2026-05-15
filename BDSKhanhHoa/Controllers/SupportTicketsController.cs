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
        private readonly ILogger<SupportTicketsController> _logger;

        private const long MaxAttachmentSize = 10 * 1024 * 1024;

        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg",
            ".jpeg",
            ".png",
            ".webp",
            ".pdf",
            ".doc",
            ".docx",
            ".xls",
            ".xlsx",
            ".zip",
            ".rar"
        };

        public SupportTicketsController(
            ApplicationDbContext context,
            IWebHostEnvironment env,
            ILogger<SupportTicketsController> logger)
        {
            _context = context;
            _env = env;
            _logger = logger;
        }

        private bool TryGetCurrentUserId(out int userId)
        {
            userId = 0;
            string? userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(userIdStr, out userId);
        }

        private async Task<bool> CanAccessBusinessPortalAsync(int userId)
        {
            bool hasApprovedBusinessProfile = await _context.BusinessProfiles
                .AsNoTracking()
                .AnyAsync(x =>
                    x.UserID == userId &&
                    (
                        x.VerificationStatus == "Approved" ||
                        x.VerificationStatus == "Đã duyệt" ||
                        x.VerificationStatus == "Đã xác minh"
                    ));

            bool hasAssignedProject = await _context.Projects
                .AsNoTracking()
                .AnyAsync(p => p.OwnerUserID == userId && !p.IsDeleted);

            return hasApprovedBusinessProfile || hasAssignedProject;
        }

        private IQueryable<Project> GetMyProjectsQuery(int userId)
        {
            return _context.Projects
                .AsNoTracking()
                .Include(p => p.Area)
                .Include(p => p.Ward)
                .Where(p => p.OwnerUserID == userId && !p.IsDeleted);
        }

        private async Task<Project?> GetOwnedProjectAsync(int userId, int projectId)
        {
            return await _context.Projects
                .AsNoTracking()
                .FirstOrDefaultAsync(p =>
                    p.ProjectID == projectId &&
                    p.OwnerUserID == userId &&
                    !p.IsDeleted);
        }

        private static string NormalizeTicketStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status))
                return "Chờ xử lý";

            return status.Trim() switch
            {
                "Pending" => "Chờ xử lý",
                "Processing" => "Đang xử lý",
                "Resolved" => "Đã xử lý",
                "Closed" => "Đã đóng",

                "Chưa xử lý" => "Chờ xử lý",
                "Chờ xử lý" => "Chờ xử lý",
                "Đang xử lý" => "Đang xử lý",
                "Đã xử lý" => "Đã xử lý",
                "Đã đóng" => "Đã đóng",

                _ => status.Trim()
            };
        }

        private static string CleanText(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string cleaned = value.Trim();

            if (cleaned.Length > maxLength)
                cleaned = cleaned.Substring(0, maxLength);

            return cleaned;
        }

        private static bool IsValidPhone(string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone))
                return false;

            string normalized = new string(phone.Where(char.IsDigit).ToArray());

            return normalized.Length >= 9 && normalized.Length <= 11;
        }

        private async Task<string?> SaveAttachmentAsync(IFormFile? attachment)
        {
            if (attachment == null || attachment.Length <= 0)
                return null;

            if (attachment.Length > MaxAttachmentSize)
                throw new InvalidOperationException("File đính kèm vượt quá dung lượng tối đa 10MB.");

            string extension = Path.GetExtension(attachment.FileName);

            if (string.IsNullOrWhiteSpace(extension) || !AllowedExtensions.Contains(extension))
                throw new InvalidOperationException("Định dạng file không được hỗ trợ.");

            string uploadDir = Path.Combine(_env.WebRootPath, "uploads", "support_tickets");

            if (!Directory.Exists(uploadDir))
                Directory.CreateDirectory(uploadDir);

            string safeFileName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
            string absolutePath = Path.Combine(uploadDir, safeFileName);

            using (var stream = new FileStream(absolutePath, FileMode.Create))
            {
                await attachment.CopyToAsync(stream);
            }

            return "/uploads/support_tickets/" + safeFileName;
        }

        private async Task NotifyAdminsAsync(ContactMessage ticket, string projectName)
        {
            var adminRoleIds = await _context.Roles
                .AsNoTracking()
                .Where(r =>
                    r.RoleName == "Admin" ||
                    r.RoleName == "Super Admin" ||
                    r.RoleName == "Quản trị viên")
                .Select(r => r.RoleID)
                .ToListAsync();

            if (!adminRoleIds.Any())
                return;

            var adminIds = await _context.Users
                .AsNoTracking()
                .Where(u =>
                    adminRoleIds.Contains(u.RoleID) &&
                    u.IsActive == true &&
                    u.IsDeleted == false)
                .Select(u => u.UserID)
                .ToListAsync();

            foreach (int adminId in adminIds)
            {
                _context.Notifications.Add(new Notification
                {
                    UserID = adminId,
                    Title = "Yêu cầu hỗ trợ mới từ CĐT",
                    Content = $"CĐT vừa gửi yêu cầu hỗ trợ cho dự án \"{projectName}\". Tiêu đề: {ticket.Subject}",
                    ActionUrl = "/Admin/ContactMessages/Index",
                    ActionText = "Xem yêu cầu",
                    IsRead = false,
                    CreatedAt = DateTime.Now
                });
            }
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return RedirectToAction("Login", "Account");
            }

            if (!await CanAccessBusinessPortalAsync(userId))
            {
                TempData["Error"] = "Tài khoản của bạn chưa được cấp quyền quản lý dự án.";
                return RedirectToAction("Index", "Home");
            }

            var businessProfile = await _context.BusinessProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.UserID == userId);

            var currentUser = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.UserID == userId);

            var projects = await GetMyProjectsQuery(userId)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            var projectIds = projects.Select(p => p.ProjectID).ToList();

            var supportHistory = projectIds.Any()
                ? await _context.ContactMessages
                    .AsNoTracking()
                    .Include(x => x.Project)
                    .Where(x =>
                        x.UserID == userId &&
                        x.ProjectID.HasValue &&
                        projectIds.Contains(x.ProjectID.Value))
                    .OrderByDescending(x => x.CreatedAt)
                    .ToListAsync()
                : new List<ContactMessage>();

            ViewBag.BusinessName = businessProfile?.BusinessName ?? "Doanh nghiệp đối tác";
            ViewBag.ProjectCount = projects.Count;

            ViewBag.CurrentUserID = userId;
            ViewBag.UserFullName = businessProfile?.RepresentativeName ?? currentUser?.FullName ?? "Đại diện CĐT";
            ViewBag.UserPhone = businessProfile?.RepresentativePhone ?? currentUser?.Phone ?? "";
            ViewBag.UserEmail = businessProfile?.BusinessEmail ?? currentUser?.Email ?? User.Identity?.Name ?? "";

            ViewBag.SupportHistory = supportHistory;
            ViewBag.OpenTicketCount = supportHistory.Count(x =>
                NormalizeTicketStatus(x.Status) == "Chờ xử lý" ||
                NormalizeTicketStatus(x.Status) == "Đang xử lý");

            ViewBag.ClosedTicketCount = supportHistory.Count(x =>
                NormalizeTicketStatus(x.Status) == "Đã xử lý" ||
                NormalizeTicketStatus(x.Status) == "Đã đóng");

            return View(projects);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(
            string subject,
            string message,
            int? projectId,
            IFormFile? attachment,
            string fullName,
            string phone,
            string email)
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return RedirectToAction("Login", "Account");
            }

            if (!await CanAccessBusinessPortalAsync(userId))
            {
                TempData["Error"] = "Tài khoản của bạn chưa được cấp quyền quản lý dự án.";
                return RedirectToAction(nameof(Index));
            }

            subject = CleanText(subject, 200);
            message = CleanText(message, 4000);
            fullName = CleanText(fullName, 150);
            phone = CleanText(phone, 20);
            email = CleanText(email, 150);

            if (string.IsNullOrWhiteSpace(subject))
            {
                TempData["Error"] = "Vui lòng nhập tiêu đề yêu cầu.";
                return RedirectToAction(nameof(Index));
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                TempData["Error"] = "Vui lòng nhập nội dung cần hỗ trợ.";
                return RedirectToAction(nameof(Index));
            }

            if (!projectId.HasValue || projectId.Value <= 0)
            {
                TempData["Error"] = "Vui lòng chọn dự án cần hỗ trợ.";
                return RedirectToAction(nameof(Index));
            }

            Project? project = await GetOwnedProjectAsync(userId, projectId.Value);

            if (project == null)
            {
                TempData["Error"] = "Dự án không tồn tại hoặc bạn không có quyền gửi yêu cầu cho dự án này.";
                return RedirectToAction(nameof(Index));
            }

            if (string.IsNullOrWhiteSpace(fullName))
            {
                fullName = "Đại diện CĐT";
            }

            if (!IsValidPhone(phone))
            {
                TempData["Error"] = "Số điện thoại không hợp lệ.";
                return RedirectToAction(nameof(Index));
            }

            if (string.IsNullOrWhiteSpace(email))
            {
                TempData["Error"] = "Vui lòng nhập email liên hệ.";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                string? filePath = await SaveAttachmentAsync(attachment);

                var ticket = new ContactMessage
                {
                    UserID = userId,
                    ProjectID = project.ProjectID,
                    FullName = fullName,
                    Phone = phone,
                    Email = email,
                    Subject = subject,
                    Message = message,
                    AttachmentPath = filePath,
                    Status = "Chờ xử lý",
                    CreatedAt = DateTime.Now
                };

                _context.ContactMessages.Add(ticket);
                await NotifyAdminsAsync(ticket, project.ProjectName);
                await _context.SaveChangesAsync();

                TempData["Success"] = "Yêu cầu hỗ trợ đã được gửi thành công. Bạn có thể theo dõi tiến độ xử lý trong lịch sử yêu cầu.";
            }
            catch (InvalidOperationException ex)
            {
                TempData["Error"] = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi CĐT gửi yêu cầu hỗ trợ.");
                TempData["Error"] = "Hệ thống đang bận, vui lòng thử lại sau.";
            }

            return RedirectToAction(nameof(Index));
        }
    }
}