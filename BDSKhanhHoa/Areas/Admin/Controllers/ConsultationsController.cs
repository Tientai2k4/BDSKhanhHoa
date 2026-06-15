using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Staff")]
    [Route("Admin/[controller]/[action]")]
    public class ConsultationsController : Controller
    {
        private readonly ApplicationDbContext _context;

        private const string REMIND_MARKER = "[REMIND_SELLER]";
        private const string REMIND_DISPLAY_TEXT = "Đã gửi thông báo nhắc người phụ trách";

        public ConsultationsController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> Index(
            int? projectId = null,
            string? keyword = null,
            string? source = "All",
            string? remindFilter = "CanRemind",
            string? dateRange = "all",
            int page = 1)
        {
            const int pageSize = 12;

            keyword = CleanKeyword(keyword);
            source = NormalizeFilter(source, "All");
            remindFilter = NormalizeFilter(remindFilter, "CanRemind");
            dateRange = NormalizeFilter(dateRange, "all");

            var projectList = await _context.Projects
                .AsNoTracking()
                .Where(p => !p.IsDeleted)
                .OrderBy(p => p.ProjectName)
                .Select(p => new { p.ProjectID, p.ProjectName })
                .ToListAsync();

            ViewBag.ProjectList = new SelectList(projectList, "ProjectID", "ProjectName", projectId);
            ViewBag.CurrentProjectId = projectId;

            IQueryable<Consultation> scopedQuery = _context.Consultations
                .AsNoTracking()
                .Include(c => c.Property).ThenInclude(p => p.User)
                .Include(c => c.Project).ThenInclude(p => p.Owner)
                .Include(c => c.Sender)
                .Include(c => c.AssignedUser)
                .AsQueryable();

            if (projectId.HasValue && projectId.Value > 0)
            {
                scopedQuery = scopedQuery.Where(c => c.ProjectID == projectId.Value);
                source = "Project";
            }

            scopedQuery = ApplySourceFilter(scopedQuery, source);
            scopedQuery = ApplyDateFilter(scopedQuery, dateRange);
            scopedQuery = ApplyKeywordFilter(scopedQuery, keyword);

            ViewBag.TotalLeads = await scopedQuery.CountAsync();
            ViewBag.NeedsReminderLeads = await scopedQuery.CountAsync(c =>
                (c.Status == null || c.Status == "" || c.Status == "New" || c.Status == "Mới" || c.Status == "Mới gửi") &&
                (c.SellerNote == null || !c.SellerNote.Contains(REMIND_MARKER)));

            ViewBag.RemindedLeads = await scopedQuery.CountAsync(c =>
                c.SellerNote != null && c.SellerNote.Contains(REMIND_MARKER));

            ViewBag.HandledLeads = await scopedQuery.CountAsync(c =>
                !(c.Status == null || c.Status == "" || c.Status == "New" || c.Status == "Mới" || c.Status == "Mới gửi") ||
                (c.SellerNote != null && c.SellerNote.Contains(REMIND_MARKER)));

            ViewBag.NewLeads = await scopedQuery.CountAsync(c =>
                c.Status == null || c.Status == "" || c.Status == "New" || c.Status == "Mới" || c.Status == "Mới gửi");

            ViewBag.ProcessingLeads = await scopedQuery.CountAsync(c =>
                c.Status == "Contacted" || c.Status == "Đã liên hệ");

            ViewBag.ClosedLeads = await scopedQuery.CountAsync(c =>
                c.Status == "Closed" || c.Status == "Resolved" || c.Status == "Đã chốt" || c.Status == "Hoàn tất tư vấn");

            ViewBag.JunkLeads = await scopedQuery.CountAsync(c =>
                c.Status == "Cancelled" || c.Status == "Spam" || c.Status == "Invalid" || c.Status == "Không hợp lệ");

            IQueryable<Consultation> listQuery = ApplyReminderFilter(scopedQuery, remindFilter);

            int totalItems = await listQuery.CountAsync();
            int totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize));
            page = Math.Clamp(page, 1, totalPages);

            var leads = await listQuery
                .OrderByDescending(c =>
                    (c.Status == null || c.Status == "" || c.Status == "New" || c.Status == "Mới" || c.Status == "Mới gửi") &&
                    (c.SellerNote == null || !c.SellerNote.Contains(REMIND_MARKER)))
                .ThenByDescending(c => c.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.Keyword = keyword;
            ViewBag.Source = source;
            ViewBag.RemindFilter = remindFilter;
            ViewBag.DateRange = dateRange;
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.TotalFilteredItems = totalItems;
            ViewBag.RemindMarker = REMIND_MARKER;

            return View(leads);
        }

        [HttpGet]
        public async Task<IActionResult> GetDetails(int id)
        {
            try
            {
                var c = await _context.Consultations
                    .AsNoTracking()
                    .Include(x => x.Property).ThenInclude(p => p.User)
                    .Include(x => x.Project).ThenInclude(p => p.Owner)
                    .Include(x => x.Sender)
                    .Include(x => x.AssignedUser)
                    .FirstOrDefaultAsync(x => x.ConsultID == id);

                if (c == null)
                {
                    return Json(new { success = false, message = "Không tìm thấy yêu cầu tư vấn." });
                }

                bool wasReminded = IsReminded(c.SellerNote);
                bool canRemind = CanRemindConsultation(c.Status, c.SellerNote);

                var responseData = new
                {
                    id = c.ConsultID,
                    customerName = string.IsNullOrWhiteSpace(c.FullName) ? "Khách vãng lai" : c.FullName.Trim(),
                    customerPhone = string.IsNullOrWhiteSpace(c.Phone) ? "Không có" : c.Phone.Trim(),
                    customerEmail = string.IsNullOrWhiteSpace(c.Email) ? "Không có" : c.Email.Trim(),
                    sourceName = GetConsultationSourceName(c),
                    sourceType = GetConsultationSourceType(c),
                    handlerName = GetConsultationHandlerName(c),
                    handlerPhone = c.AssignedUser?.Phone ?? c.Property?.User?.Phone ?? c.Project?.Owner?.Phone ?? "Không có",
                    note = string.IsNullOrWhiteSpace(c.Note) ? "Không có lời nhắn" : c.Note.Trim(),
                    sellerNote = string.IsNullOrWhiteSpace(CleanReminderMarker(c.SellerNote))
                        ? "Chưa có ghi chú xử lý."
                        : CleanReminderMarker(c.SellerNote),
                    status = GetConsultationStatusText(c.Status),
                    rawStatus = c.Status ?? "",
                    wasReminded,
                    canRemind,
                    remindedText = wasReminded ? REMIND_DISPLAY_TEXT : "Chưa gửi thông báo",
                    createdAt = c.CreatedAt.ToString("HH:mm dd/MM/yyyy"),
                    updatedAt = c.UpdatedAt?.ToString("HH:mm dd/MM/yyyy") ?? "Chưa cập nhật"
                };

                return Json(new { success = true, data = responseData });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Lỗi hệ thống: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemindSeller(int id)
        {
            var c = await _context.Consultations
                .Include(x => x.Property).ThenInclude(p => p.User)
                .Include(x => x.Project).ThenInclude(p => p.Owner)
                .Include(x => x.AssignedUser)
                .FirstOrDefaultAsync(x => x.ConsultID == id);

            if (c == null)
            {
                return Json(new { success = false, message = "Không tìm thấy yêu cầu tư vấn." });
            }

            if (!CanRemindConsultation(c.Status, c.SellerNote))
            {
                bool wasReminded = IsReminded(c.SellerNote);

                return Json(new
                {
                    success = false,
                    code = wasReminded ? "ALREADY_REMINDED" : "ALREADY_HANDLED",
                    message = wasReminded
                        ? "Yêu cầu này đã được thông báo rồi, hệ thống không gửi lại để tránh làm phiền người phụ trách."
                        : "Yêu cầu này đã được tiếp nhận/xử lý, Admin không cần bấm chuông nữa."
                });
            }

            int? targetUserId = c.AssignedToUserID
                                ?? c.Project?.OwnerUserID
                                ?? c.Property?.UserID;

            if (targetUserId == null || targetUserId.Value <= 0)
            {
                return Json(new
                {
                    success = false,
                    message = "Yêu cầu này chưa xác định được người phụ trách để thông báo."
                });
            }

            var targetUser = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u =>
                    u.UserID == targetUserId.Value &&
                    u.IsDeleted == false &&
                    u.IsActive == true);

            if (targetUser == null)
            {
                return Json(new
                {
                    success = false,
                    message = "Người phụ trách không tồn tại hoặc tài khoản đã bị khóa."
                });
            }

            DateTime now = DateTime.Now;
            string actorName = User.FindFirst("FullName")?.Value
                               ?? User.FindFirstValue(ClaimTypes.Name)
                               ?? User.Identity?.Name
                               ?? "Admin/Staff";

            string customerName = string.IsNullOrWhiteSpace(c.FullName) ? "khách vãng lai" : c.FullName.Trim();
            string sourceName = GetConsultationSourceName(c);

            _context.Notifications.Add(new Notification
            {
                UserID = targetUserId.Value,
                Title = "🔔 Admin nhắc xử lý yêu cầu tư vấn",
                Content = $"Bạn có yêu cầu tư vấn mới/chưa xử lý từ khách {customerName}, liên quan đến \"{sourceName}\". Vui lòng liên hệ khách và cập nhật trạng thái trên hệ thống.",
                ActionUrl = BuildLeadActionUrl(c),
                ActionText = "Xem yêu cầu",
                CreatedAt = now,
                IsRead = false
            });

            string remindLog =
                $"{REMIND_MARKER} {now:HH:mm dd/MM/yyyy} - {actorName} đã gửi thông báo nhắc người phụ trách: {targetUser.FullName ?? targetUser.Username}.";

            c.SellerNote = string.IsNullOrWhiteSpace(c.SellerNote)
                ? remindLog
                : c.SellerNote.Trim() + Environment.NewLine + remindLog;

            c.UpdatedAt = now;

            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                id = c.ConsultID,
                reminded = true,
                remindedAt = now.ToString("HH:mm dd/MM/yyyy"),
                message = "Đã gửi chuông thông báo. Mục này sẽ được ẩn khỏi danh sách cần xử lý."
            });
        }

        [HttpGet]
        public async Task<IActionResult> ExportCsv(
            int? projectId = null,
            string? keyword = null,
            string? source = "All",
            string? remindFilter = "All",
            string? dateRange = "all")
        {
            keyword = CleanKeyword(keyword);
            source = NormalizeFilter(source, "All");
            remindFilter = NormalizeFilter(remindFilter, "All");
            dateRange = NormalizeFilter(dateRange, "all");

            IQueryable<Consultation> query = _context.Consultations
                .AsNoTracking()
                .Include(c => c.Property).ThenInclude(p => p.User)
                .Include(c => c.Project).ThenInclude(p => p.Owner)
                .Include(c => c.AssignedUser)
                .AsQueryable();

            if (projectId.HasValue && projectId.Value > 0)
            {
                query = query.Where(c => c.ProjectID == projectId.Value);
                source = "Project";
            }

            query = ApplySourceFilter(query, source);
            query = ApplyDateFilter(query, dateRange);
            query = ApplyKeywordFilter(query, keyword);
            query = ApplyReminderFilter(query, remindFilter);

            var leads = await query
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();

            var builder = new StringBuilder();
            builder.Append('\uFEFF');
            builder.AppendLine("MaYeuCau,NgayTao,KhachHang,SDT,Email,Nguon,LoaiNguon,NguoiPhuTrach,TrangThai,TinhTrangThongBao");

            foreach (var c in leads)
            {
                builder.AppendLine(
                    $"{c.ConsultID}," +
                    $"{c.CreatedAt:dd/MM/yyyy HH:mm}," +
                    $"\"{EscapeCsv(c.FullName ?? "Khách vãng lai")}\"," +
                    $"\"{EscapeCsv(c.Phone ?? "")}\"," +
                    $"\"{EscapeCsv(c.Email ?? "")}\"," +
                    $"\"{EscapeCsv(GetConsultationSourceName(c))}\"," +
                    $"\"{EscapeCsv(GetConsultationSourceType(c))}\"," +
                    $"\"{EscapeCsv(GetConsultationHandlerName(c))}\"," +
                    $"\"{EscapeCsv(GetConsultationStatusText(c.Status))}\"," +
                    $"\"{EscapeCsv(IsReminded(c.SellerNote) ? "Đã thông báo" : "Chưa thông báo")}\"");
            }

            return File(
                Encoding.UTF8.GetBytes(builder.ToString()),
                "text/csv",
                $"YeuCauTuVanCanGiamSat_{DateTime.Now:yyyyMMddHHmm}.csv");
        }

        private static IQueryable<Consultation> ApplySourceFilter(IQueryable<Consultation> query, string source)
        {
            if (source.Equals("Property", StringComparison.OrdinalIgnoreCase))
            {
                return query.Where(c => c.PropertyID != null && c.ProjectID == null);
            }

            if (source.Equals("Project", StringComparison.OrdinalIgnoreCase))
            {
                return query.Where(c => c.ProjectID != null);
            }

            return query;
        }

        private static IQueryable<Consultation> ApplyDateFilter(IQueryable<Consultation> query, string dateRange)
        {
            DateTime today = DateTime.Now.Date;

            return dateRange.Trim().ToLowerInvariant() switch
            {
                "today" => query.Where(c => c.CreatedAt >= today && c.CreatedAt < today.AddDays(1)),
                "week" => query.Where(c => c.CreatedAt >= today.AddDays(-7)),
                "month" => query.Where(c => c.CreatedAt >= today.AddMonths(-1)),
                _ => query
            };
        }

        private static IQueryable<Consultation> ApplyKeywordFilter(IQueryable<Consultation> query, string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return query;
            }

            return query.Where(c =>
                (c.FullName != null && EF.Functions.Like(c.FullName, $"%{keyword}%")) ||
                (c.Phone != null && EF.Functions.Like(c.Phone, $"%{keyword}%")) ||
                (c.Email != null && EF.Functions.Like(c.Email, $"%{keyword}%")) ||
                (c.Property != null && c.Property.Title != null && EF.Functions.Like(c.Property.Title, $"%{keyword}%")) ||
                (c.Project != null && c.Project.ProjectName != null && EF.Functions.Like(c.Project.ProjectName, $"%{keyword}%")) ||
                (c.AssignedUser != null && c.AssignedUser.FullName != null && EF.Functions.Like(c.AssignedUser.FullName, $"%{keyword}%")) ||
                (c.Property != null && c.Property.User != null && c.Property.User.FullName != null && EF.Functions.Like(c.Property.User.FullName, $"%{keyword}%")) ||
                (c.Project != null && c.Project.Owner != null && c.Project.Owner.FullName != null && EF.Functions.Like(c.Project.Owner.FullName, $"%{keyword}%")));
        }

        private static IQueryable<Consultation> ApplyReminderFilter(IQueryable<Consultation> query, string remindFilter)
        {
            if (remindFilter.Equals("CanRemind", StringComparison.OrdinalIgnoreCase) ||
                remindFilter.Equals("NeedNotify", StringComparison.OrdinalIgnoreCase))
            {
                return query.Where(c =>
                    (c.Status == null || c.Status == "" || c.Status == "New" || c.Status == "Mới" || c.Status == "Mới gửi") &&
                    (c.SellerNote == null || !c.SellerNote.Contains(REMIND_MARKER)));
            }

            if (remindFilter.Equals("Reminded", StringComparison.OrdinalIgnoreCase))
            {
                return query.Where(c => c.SellerNote != null && c.SellerNote.Contains(REMIND_MARKER));
            }

            if (remindFilter.Equals("Handled", StringComparison.OrdinalIgnoreCase))
            {
                return query.Where(c =>
                    !(c.Status == null || c.Status == "" || c.Status == "New" || c.Status == "Mới" || c.Status == "Mới gửi") ||
                    (c.SellerNote != null && c.SellerNote.Contains(REMIND_MARKER)));
            }

            return query;
        }

        private static bool CanRemindConsultation(string? status, string? sellerNote)
        {
            bool isNew = string.IsNullOrWhiteSpace(status) ||
                         status == "New" ||
                         status == "Mới" ||
                         status == "Mới gửi";

            return isNew && !IsReminded(sellerNote);
        }

        private static bool IsReminded(string? text)
        {
            return !string.IsNullOrWhiteSpace(text) &&
                   text.Contains(REMIND_MARKER, StringComparison.OrdinalIgnoreCase);
        }

        private static string CleanReminderMarker(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            return Regex.Replace(text, Regex.Escape(REMIND_MARKER), "", RegexOptions.IgnoreCase).Trim();
        }

        private static string CleanKeyword(string? keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return string.Empty;
            }

            keyword = keyword.Trim();
            return keyword.Length > 80 ? keyword.Substring(0, 80) : keyword;
        }

        private static string NormalizeFilter(string? value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static string EscapeCsv(string? value)
        {
            return (value ?? string.Empty).Replace("\"", "\"\"");
        }

        private static string GetConsultationSourceName(Consultation c)
        {
            return c.Property?.Title
                   ?? c.Project?.ProjectName
                   ?? "Bất động sản / dự án đã bị gỡ";
        }

        private static string GetConsultationSourceType(Consultation c)
        {
            if (c.ProjectID.HasValue)
            {
                return "Dự án";
            }

            if (c.PropertyID.HasValue)
            {
                return "Tin lẻ BĐS";
            }

            return "Không xác định";
        }

        private static string GetConsultationHandlerName(Consultation c)
        {
            return c.AssignedUser?.FullName
                   ?? c.Property?.User?.FullName
                   ?? c.Project?.Owner?.FullName
                   ?? "Chưa phân công";
        }

        private static string GetConsultationStatusText(string? status)
        {
            return status switch
            {
                null => "Mới gửi",
                "" => "Mới gửi",
                "New" => "Mới gửi",
                "Mới" => "Mới gửi",
                "Mới gửi" => "Mới gửi",
                "Contacted" => "Đã tiếp nhận",
                "Đã liên hệ" => "Đã tiếp nhận",
                "Closed" => "Hoàn tất tư vấn",
                "Resolved" => "Hoàn tất tư vấn",
                "Hoàn tất tư vấn" => "Hoàn tất tư vấn",
                "Spam" => "Spam/Rác",
                "Cancelled" => "Khách hủy",
                "Invalid" => "Không hợp lệ",
                "Không hợp lệ" => "Không hợp lệ",
                _ => status
            };
        }

        private static string BuildLeadActionUrl(Consultation consultation)
        {
            return "/Consultations/Index?statusFilter=All";
        }
    }
}
