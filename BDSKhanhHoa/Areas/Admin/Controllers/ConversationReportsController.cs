using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Staff")]
    public class ConversationReportsController : Controller
    {
        private readonly ApplicationDbContext _context;

        private const string ViolationLockTag = "AUTO-KHÓA DO VI PHẠM";
        private const string ViolationWarningTag = "CẢNH BÁO DO BÁO CÁO CUỘC TRÒ CHUYỆN";
        private const int ViolationLimit = 3;
        private const int UserAdminNoteMaxLength = 1900;
        private const int ReportAdminNoteMaxLength = 1900;

        public ConversationReportsController(ApplicationDbContext context)
        {
            _context = context;
        }

        private int GetCurrentAdminId()
        {
            string? id = User.FindFirstValue(ClaimTypes.NameIdentifier);
            int.TryParse(id, out int adminId);
            return adminId;
        }

        private static string SafeName(User? user)
        {
            return user?.FullName ?? user?.Username ?? "Người dùng";
        }

        private static string NormalizeStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status)) return "Pending";

            status = status.Trim();

            if (status.Equals("All", StringComparison.OrdinalIgnoreCase)) return "All";
            if (status.Equals("Pending", StringComparison.OrdinalIgnoreCase)) return "Pending";
            if (status.Equals("Processed", StringComparison.OrdinalIgnoreCase)) return "Processed";
            if (status.Equals("Rejected", StringComparison.OrdinalIgnoreCase)) return "Rejected";

            return "Pending";
        }

        private static string DisplayStatus(string? status)
        {
            return status switch
            {
                "Pending" => "Chờ xử lý",
                "Processed" => "Đã xử lý",
                "Rejected" => "Không chấp nhận",
                _ => "Không rõ"
            };
        }

        private static string DisplayAction(string? action)
        {
            return action switch
            {
                "WarningOnly" => "Ghi nhận và cảnh báo",
                "LockReportedUser" => "Khóa tài khoản bị báo cáo",
                "Reject" => "Không chấp nhận báo cáo",
                _ => "Chưa xử lý"
            };
        }

        private static string Cut(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";

            value = value.Trim();

            if (value.Length <= maxLength) return value;

            return value.Substring(0, maxLength - 3) + "...";
        }

        private static string MergeAdminNote(string? currentNote, string newNote)
        {
            currentNote = string.IsNullOrWhiteSpace(currentNote) ? "" : currentNote.Trim();
            newNote = string.IsNullOrWhiteSpace(newNote) ? "" : newNote.Trim();

            string merged = string.IsNullOrWhiteSpace(currentNote)
                ? newNote
                : newNote + Environment.NewLine + currentNote;

            return Cut(merged, UserAdminNoteMaxLength);
        }

        private async Task<List<int>> GetAdminAndStaffIdsAsync()
        {
            return await _context.Users
                .AsNoTracking()
                .Where(u =>
                    !u.IsDeleted &&
                    u.IsActive == true &&
                    (u.RoleID == 1 || u.RoleID == 2))
                .Select(u => u.UserID)
                .ToListAsync();
        }

        private async Task NotifyAdminAndStaffAsync(string title, string content, string actionUrl, string actionText)
        {
            var adminIds = await GetAdminAndStaffIdsAsync();

            foreach (int adminId in adminIds)
            {
                _context.Notifications.Add(new Notification
                {
                    UserID = adminId,
                    Title = title,
                    Content = Cut(content, 3900),
                    ActionUrl = actionUrl,
                    ActionText = actionText,
                    IsRead = false,
                    CreatedAt = DateTime.Now
                });
            }
        }

        private async Task<int> CountUserViolationsAsync(int userId)
        {
            return await _context.ConversationReports
                .AsNoTracking()
                .CountAsync(r =>
                    r.ReportedUserID == userId &&
                    r.Status == "Processed" &&
                    (
                        r.AdminAction == "WarningOnly" ||
                        r.AdminAction == "LockReportedUser"
                    ));
        }

        private async Task<int> CountUserWarningsAsync(int userId)
        {
            return await _context.ConversationReports
                .AsNoTracking()
                .CountAsync(r =>
                    r.ReportedUserID == userId &&
                    r.Status == "Processed" &&
                    r.AdminAction == "WarningOnly");
        }

        private async Task<int> CountUserLocksAsync(int userId)
        {
            return await _context.ConversationReports
                .AsNoTracking()
                .CountAsync(r =>
                    r.ReportedUserID == userId &&
                    r.Status == "Processed" &&
                    r.AdminAction == "LockReportedUser");
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? status = "Pending", string? keyword = null, int page = 1)
        {
            const int pageSize = 15;

            page = Math.Max(1, page);
            status = NormalizeStatus(status);
            keyword = string.IsNullOrWhiteSpace(keyword) ? "" : keyword.Trim();

            IQueryable<ConversationReport> query = _context.ConversationReports
                .AsNoTracking()
                .Include(r => r.Reporter)
                .Include(r => r.ReportedUser)
                .Include(r => r.Property)
                .Include(r => r.ProcessedBy);

            if (status != "All")
            {
                query = query.Where(r => r.Status == status);
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                query = query.Where(r =>
                    EF.Functions.Like(r.Reason ?? "", $"%{keyword}%") ||
                    EF.Functions.Like(r.Description ?? "", $"%{keyword}%") ||
                    EF.Functions.Like(r.AdminNote ?? "", $"%{keyword}%") ||

                    (r.Reporter != null &&
                        (
                            EF.Functions.Like(r.Reporter.FullName ?? "", $"%{keyword}%") ||
                            EF.Functions.Like(r.Reporter.Username ?? "", $"%{keyword}%") ||
                            EF.Functions.Like(r.Reporter.Phone ?? "", $"%{keyword}%") ||
                            EF.Functions.Like(r.Reporter.Email ?? "", $"%{keyword}%")
                        )
                    ) ||

                    (r.ReportedUser != null &&
                        (
                            EF.Functions.Like(r.ReportedUser.FullName ?? "", $"%{keyword}%") ||
                            EF.Functions.Like(r.ReportedUser.Username ?? "", $"%{keyword}%") ||
                            EF.Functions.Like(r.ReportedUser.Phone ?? "", $"%{keyword}%") ||
                            EF.Functions.Like(r.ReportedUser.Email ?? "", $"%{keyword}%")
                        )
                    ) ||

                    (r.Property != null &&
                        (
                            EF.Functions.Like(r.Property.Title ?? "", $"%{keyword}%") ||
                            r.PropertyID.ToString() == keyword
                        )
                    )
                );
            }

            int totalItems = await query.CountAsync();
            int totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize));

            if (page > totalPages) page = totalPages;

            var reports = await query
                .OrderByDescending(r => r.Status == "Pending")
                .ThenByDescending(r => r.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.Status = status;
            ViewBag.Keyword = keyword;
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.TotalItems = totalItems;

            ViewBag.PendingCount = await _context.ConversationReports.AsNoTracking().CountAsync(r => r.Status == "Pending");
            ViewBag.ProcessedCount = await _context.ConversationReports.AsNoTracking().CountAsync(r => r.Status == "Processed");
            ViewBag.RejectedCount = await _context.ConversationReports.AsNoTracking().CountAsync(r => r.Status == "Rejected");

            ViewBag.WarningCount = await _context.ConversationReports
                .AsNoTracking()
                .CountAsync(r => r.Status == "Processed" && r.AdminAction == "WarningOnly");

            ViewBag.LockedByConversationReports = await _context.Users
                .AsNoTracking()
                .CountAsync(u =>
                    !u.IsDeleted &&
                    u.IsActive == false &&
                    u.AdminNote != null &&
                    u.AdminNote.Contains(ViolationLockTag));

            ViewBag.ViolationLimit = ViolationLimit;

            return View(reports);
        }

        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            var report = await _context.ConversationReports
                .AsNoTracking()
                .Include(r => r.Reporter)
                .Include(r => r.ReportedUser)
                .Include(r => r.Property)
                .Include(r => r.ProcessedBy)
                .FirstOrDefaultAsync(r => r.ReportID == id);

            if (report == null)
            {
                TempData["Error"] = "Không tìm thấy báo cáo cuộc trò chuyện.";
                return RedirectToAction(nameof(Index));
            }

            var messages = await _context.UserMessages
                .AsNoTracking()
                .Include(m => m.Sender)
                .Include(m => m.Receiver)
                .Where(m =>
                    m.PropertyID == report.PropertyID &&
                    (
                        (m.SenderID == report.ReporterID && m.ReceiverID == report.ReportedUserID) ||
                        (m.SenderID == report.ReportedUserID && m.ReceiverID == report.ReporterID)
                    ))
                .OrderBy(m => m.CreatedAt)
                .ToListAsync();

            int violationCount = await CountUserViolationsAsync(report.ReportedUserID);
            int warningCount = await CountUserWarningsAsync(report.ReportedUserID);
            int lockCount = await CountUserLocksAsync(report.ReportedUserID);

            ViewBag.Messages = messages;
            ViewBag.ReportedUserViolationCount = violationCount;
            ViewBag.ReportedUserWarningCount = warningCount;
            ViewBag.ReportedUserLockCount = lockCount;
            ViewBag.ViolationLimit = ViolationLimit;

            return View(report);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Process(int reportId, string adminAction, string? adminNote)
        {
            int adminId = GetCurrentAdminId();

            if (adminId <= 0)
            {
                TempData["Error"] = "Phiên đăng nhập Admin/Staff không hợp lệ.";
                return RedirectToAction(nameof(Index));
            }

            var report = await _context.ConversationReports
                .Include(r => r.Reporter)
                .Include(r => r.ReportedUser)
                .Include(r => r.Property)
                .FirstOrDefaultAsync(r => r.ReportID == reportId);

            if (report == null)
            {
                TempData["Error"] = "Không tìm thấy báo cáo cần xử lý.";
                return RedirectToAction(nameof(Index));
            }

            if (report.Status != "Pending")
            {
                TempData["Error"] = "Báo cáo này đã được xử lý trước đó.";
                return RedirectToAction(nameof(Details), new { id = reportId });
            }

            adminAction = string.IsNullOrWhiteSpace(adminAction)
                ? "WarningOnly"
                : adminAction.Trim();

            adminNote = string.IsNullOrWhiteSpace(adminNote)
                ? "Admin/Staff đã kiểm tra báo cáo và ghi nhận kết quả xử lý."
                : adminNote.Trim();

            adminNote = Cut(adminNote, 700);

            var reportedUser = await _context.Users
                .FirstOrDefaultAsync(u => u.UserID == report.ReportedUserID && !u.IsDeleted);

            var reporter = await _context.Users
                .FirstOrDefaultAsync(u => u.UserID == report.ReporterID && !u.IsDeleted);

            if (reportedUser == null)
            {
                TempData["Error"] = "Không tìm thấy tài khoản người bị báo cáo.";
                return RedirectToAction(nameof(Details), new { id = reportId });
            }

            int violationBefore = await CountUserViolationsAsync(report.ReportedUserID);
            int warningBefore = await CountUserWarningsAsync(report.ReportedUserID);
            int lockBefore = await CountUserLocksAsync(report.ReportedUserID);

            string actionName;
            string reporterTitle;
            string reporterContent;
            string reportedTitle;
            string reportedContent;
            string reportedActionUrl;
            string reportedActionText;
            bool notifyReportedUser = true;
            bool shouldAddViolation = false;
            bool shouldLockUser = false;

            if (adminAction == "Reject")
            {
                report.Status = "Rejected";
                report.AdminAction = "Reject";

                actionName = "Không chấp nhận báo cáo";

                reporterTitle = "Báo cáo cuộc trò chuyện chưa đủ căn cứ xử lý";
                reporterContent =
                    $"Báo cáo của bạn liên quan đến tin \"{report.Property?.Title ?? "Không rõ"}\" đã được Admin/Staff kiểm tra.\n" +
                    "Kết quả: báo cáo chưa đủ căn cứ để ghi nhận vi phạm.\n" +
                    $"Ghi chú từ Admin/Staff: {adminNote}";

                reportedTitle = "Báo cáo liên quan đến cuộc trò chuyện đã được kiểm tra";
                reportedContent =
                    $"Một báo cáo liên quan đến cuộc trò chuyện của bạn trong tin \"{report.Property?.Title ?? "Không rõ"}\" đã được kiểm tra.\n" +
                    "Kết quả: hệ thống chưa ghi nhận vi phạm đối với tài khoản của bạn.\n" +
                    "Bạn không cần thực hiện thao tác nào thêm.";

                reportedActionUrl = $"/UserMessage/Index?receiverId={report.ReporterID}&propertyId={report.PropertyID}";
                reportedActionText = "Xem lại cuộc trò chuyện";
            }
            else if (adminAction == "LockReportedUser")
            {
                if (reportedUser.RoleID == 1)
                {
                    TempData["Error"] = "Không thể khóa tài khoản quản trị viên.";
                    return RedirectToAction(nameof(Details), new { id = reportId });
                }

                report.Status = "Processed";
                report.AdminAction = "LockReportedUser";

                shouldAddViolation = true;
                shouldLockUser = true;

                actionName = "Khóa tài khoản bị báo cáo";

                reporterTitle = "Báo cáo cuộc trò chuyện đã được xử lý";
                reporterContent =
                    $"Báo cáo của bạn liên quan đến tin \"{report.Property?.Title ?? "Không rõ"}\" đã được xác minh.\n" +
                    "Kết quả: tài khoản vi phạm đã bị khóa.\n" +
                    $"Lý do báo cáo: {report.Reason}\n" +
                    $"Ghi chú xử lý: {adminNote}";

                reportedTitle = "Tài khoản đã bị khóa do vi phạm";
                reportedContent =
                    "Tài khoản của bạn đã bị khóa do vi phạm quy định trong cuộc trò chuyện.\n" +
                    $"Tin liên quan: {report.Property?.Title ?? "Không rõ"}\n" +
                    $"Lý do bị báo cáo: {report.Reason}\n" +
                    $"Kết luận xử lý: {adminNote}\n" +
                    "Bạn không thể tiếp tục gửi tin nhắn hoặc sử dụng các chức năng yêu cầu tài khoản đang hoạt động.";

                reportedActionUrl = "/Notification/Index?filter=system";
                reportedActionText = "Xem thông báo hệ thống";
            }
            else
            {
                report.Status = "Processed";
                report.AdminAction = "WarningOnly";

                shouldAddViolation = true;
                shouldLockUser = false;

                actionName = "Ghi nhận và cảnh báo";

                reporterTitle = "Báo cáo cuộc trò chuyện đã được ghi nhận";
                reporterContent =
                    $"Báo cáo của bạn liên quan đến tin \"{report.Property?.Title ?? "Không rõ"}\" đã được Admin/Staff kiểm tra.\n" +
                    "Kết quả: hệ thống đã ghi nhận và gửi cảnh báo đến người bị báo cáo.\n" +
                    "Tài khoản người bị báo cáo chưa bị khóa vì mức độ vi phạm chưa đủ nghiêm trọng.\n" +
                    $"Ghi chú xử lý: {adminNote}";

                reportedTitle = "Cảnh báo vi phạm trong cuộc trò chuyện";
                reportedContent =
                    "Admin/Staff đã kiểm tra báo cáo liên quan đến cuộc trò chuyện của bạn.\n" +
                    $"Tin liên quan: {report.Property?.Title ?? "Không rõ"}\n" +
                    $"Lý do bị báo cáo: {report.Reason}\n" +
                    "Kết quả: tài khoản của bạn bị cảnh báo nhưng chưa bị khóa.\n" +
                    $"Số lần vi phạm đã ghi nhận: {violationBefore + 1}/{ViolationLimit}.\n" +
                    "Vui lòng không yêu cầu đặt cọc/chuyển tiền đáng ngờ, không spam, không xúc phạm và không cung cấp thông tin sai sự thật.\n" +
                    $"Ghi chú từ Admin/Staff: {adminNote}";

                reportedActionUrl = $"/UserMessage/Index?receiverId={report.ReporterID}&propertyId={report.PropertyID}";
                reportedActionText = "Xem lại cuộc trò chuyện";
            }

            int violationAfter = shouldAddViolation ? violationBefore + 1 : violationBefore;
            int warningAfter = adminAction == "WarningOnly" ? warningBefore + 1 : warningBefore;
            int lockAfter = adminAction == "LockReportedUser" ? lockBefore + 1 : lockBefore;

            if (shouldLockUser)
            {
                reportedUser.IsActive = false;
            }

            if (adminAction == "WarningOnly")
            {
                string shortWarningNote =
                    $"[{ViolationWarningTag} - {DateTime.Now:dd/MM/yyyy HH:mm}] " +
                    $"Cảnh báo #{warningAfter}, tổng vi phạm {violationAfter}/{ViolationLimit}. " +
                    $"Báo cáo #{report.ReportID}. Ghi chú: {adminNote}";

                reportedUser.AdminNote = MergeAdminNote(reportedUser.AdminNote, shortWarningNote);
            }
            else if (adminAction == "LockReportedUser")
            {
                string shortLockNote =
                    $"[{ViolationLockTag} - {DateTime.Now:dd/MM/yyyy HH:mm}] " +
                    $"Khóa tài khoản. Tổng vi phạm {violationAfter}/{ViolationLimit}. " +
                    $"Báo cáo #{report.ReportID}. Ghi chú: {adminNote}";

                reportedUser.AdminNote = MergeAdminNote(reportedUser.AdminNote, shortLockNote);
            }

            report.ProcessedByID = adminId;
            report.ProcessedAt = DateTime.Now;

            report.AdminNote = Cut(
                $"Kết quả xử lý: {actionName}{Environment.NewLine}" +
                $"Thời gian xử lý: {DateTime.Now:HH:mm dd/MM/yyyy}{Environment.NewLine}" +
                $"Người xử lý ID: {adminId}{Environment.NewLine}" +
                $"Số lần cảnh báo: {warningAfter}{Environment.NewLine}" +
                $"Số lần khóa: {lockAfter}{Environment.NewLine}" +
                $"Tổng vi phạm đã ghi nhận: {violationAfter}/{ViolationLimit}{Environment.NewLine}" +
                $"Ghi chú: {adminNote}",
                ReportAdminNoteMaxLength
            );

            _context.Notifications.Add(new Notification
            {
                UserID = report.ReporterID,
                Title = reporterTitle,
                Content = Cut(
                    reporterContent +
                    $"\n\nTổng vi phạm của người bị báo cáo hiện tại: {violationAfter}/{ViolationLimit}.",
                    3900),
                ActionUrl = $"/UserMessage/Index?receiverId={report.ReportedUserID}&propertyId={report.PropertyID}",
                ActionText = "Xem lại cuộc trò chuyện",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            if (notifyReportedUser)
            {
                _context.Notifications.Add(new Notification
                {
                    UserID = report.ReportedUserID,
                    Title = reportedTitle,
                    Content = Cut(reportedContent, 3900),
                    ActionUrl = reportedActionUrl,
                    ActionText = reportedActionText,
                    IsRead = false,
                    CreatedAt = DateTime.Now
                });
            }

            string adminExtra =
                violationAfter >= ViolationLimit && !shouldLockUser
                    ? $"\nLưu ý: tài khoản này đã đạt {violationAfter}/{ViolationLimit} lần vi phạm, Admin nên xem xét khóa nếu tiếp tục vi phạm."
                    : "";

            await NotifyAdminAndStaffAsync(
                title: "Đã xử lý báo cáo cuộc trò chuyện",
                content:
                    $"Báo cáo #{report.ReportID} đã được xử lý.\n" +
                    $"Kết quả: {actionName}.\n" +
                    $"Người báo cáo: {SafeName(reporter)}.\n" +
                    $"Người bị báo cáo: {SafeName(reportedUser)}.\n" +
                    $"Tin liên quan: {report.Property?.Title ?? "Không rõ"}.\n" +
                    $"Số lần cảnh báo: {warningAfter}.\n" +
                    $"Số lần khóa: {lockAfter}.\n" +
                    $"Tổng vi phạm: {violationAfter}/{ViolationLimit}." +
                    adminExtra +
                    $"\nGhi chú: {adminNote}",
                actionUrl: $"/Admin/ConversationReports/Details/{report.ReportID}",
                actionText: "Xem kết quả xử lý"
            );

            await _context.SaveChangesAsync();

            TempData["Success"] =
                $"Đã xử lý báo cáo. Kết quả: {actionName}. " +
                $"Tổng vi phạm của người bị báo cáo: {violationAfter}/{ViolationLimit}.";

            return RedirectToAction(nameof(Details), new { id = reportId });
        }
    }
}