using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
        private const string REMIND_DISPLAY_TEXT = "Đã gửi thông báo nhắc người bán";

        public ConsultationsController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> Index(
            string? status = "All",
            string? keyword = null,
            string? source = "All",
            string dateRange = "all",
            int page = 1)
        {
            const int pageSize = 15;

            var query = _context.Consultations
                .AsNoTracking()
                .Include(c => c.Property).ThenInclude(p => p.User)
                .Include(c => c.Project).ThenInclude(p => p.Owner)
                .Include(c => c.Sender)
                .Include(c => c.AssignedUser)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(status) &&
                !status.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(c => c.Status == status);
            }

            if (!string.IsNullOrWhiteSpace(source) &&
                !source.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                if (source.Equals("Property", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(c => c.PropertyID != null);
                }
                else if (source.Equals("Project", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(c => c.ProjectID != null);
                }
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                keyword = keyword.Trim();

                query = query.Where(c =>
                    (c.FullName != null && EF.Functions.Like(c.FullName, $"%{keyword}%")) ||
                    (c.Phone != null && EF.Functions.Like(c.Phone, $"%{keyword}%")) ||
                    (c.Email != null && EF.Functions.Like(c.Email, $"%{keyword}%")) ||
                    (c.Property != null && c.Property.Title != null && EF.Functions.Like(c.Property.Title, $"%{keyword}%")) ||
                    (c.Project != null && c.Project.ProjectName != null && EF.Functions.Like(c.Project.ProjectName, $"%{keyword}%")) ||
                    (c.AssignedUser != null && c.AssignedUser.FullName != null && EF.Functions.Like(c.AssignedUser.FullName, $"%{keyword}%"))
                );
            }

            var today = DateTime.Now.Date;

            switch (dateRange?.Trim().ToLowerInvariant())
            {
                case "today":
                    query = query.Where(c => c.CreatedAt >= today && c.CreatedAt < today.AddDays(1));
                    break;

                case "week":
                    query = query.Where(c => c.CreatedAt >= today.AddDays(-7));
                    break;

                case "month":
                    query = query.Where(c => c.CreatedAt >= today.AddMonths(-1));
                    break;
            }

            ViewBag.TotalLeads = await query.CountAsync();
            ViewBag.NewLeads = await query.CountAsync(c => c.Status == "New");
            ViewBag.ProcessingLeads = await query.CountAsync(c => c.Status == "Contacted");
            ViewBag.ClosedLeads = await query.CountAsync(c => c.Status == "Closed");
            ViewBag.JunkLeads = await query.CountAsync(c => c.Status == "Cancelled" || c.Status == "Spam");

            ViewBag.RemindedLeads = await query.CountAsync(c =>
                c.SellerNote != null &&
                c.SellerNote.Contains(REMIND_MARKER));

            int totalItems = await query.CountAsync();
            int totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize));

            page = Math.Clamp(page, 1, totalPages);

            var list = await query
                .OrderByDescending(c => c.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.Status = status;
            ViewBag.Keyword = keyword;
            ViewBag.Source = source;
            ViewBag.DateRange = dateRange;
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;

            return View(list);
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
                    return Json(new
                    {
                        success = false,
                        message = "Không tìm thấy yêu cầu tư vấn."
                    });
                }

                string sourceName = c.Property?.Title
                                    ?? c.Project?.ProjectName
                                    ?? "Bất động sản bị xóa";

                string sourceType = c.PropertyID != null
                    ? "Tin lẻ BĐS"
                    : c.ProjectID != null
                        ? "Dự án"
                        : "Không xác định";

                string handlerName = c.AssignedUser?.FullName
                                     ?? c.Property?.User?.FullName
                                     ?? c.Project?.Owner?.FullName
                                     ?? "Chưa phân công";

                string handlerPhone = c.AssignedUser?.Phone
                                     ?? c.Property?.User?.Phone
                                     ?? c.Project?.Owner?.Phone
                                     ?? "N/A";

                bool wasReminded = IsReminded(c.SellerNote);
                string cleanSellerNote = CleanReminderMarker(c.SellerNote);

                var responseData = new
                {
                    id = c.ConsultID,
                    customerName = c.FullName ?? "Khách vãng lai",
                    customerPhone = c.Phone ?? "N/A",
                    customerEmail = c.Email ?? "Không có",

                    sourceName = sourceName,
                    sourceType = sourceType,

                    handlerName = handlerName,
                    handlerPhone = handlerPhone,

                    note = c.Note ?? "Không có lời nhắn",

                    sellerNote = string.IsNullOrWhiteSpace(cleanSellerNote)
                        ? "Chưa có ghi chú xử lý."
                        : cleanSellerNote,

                    status = c.Status ?? "N/A",
                    wasReminded = wasReminded,
                    remindedText = wasReminded ? REMIND_DISPLAY_TEXT : "Chưa gửi nhắc nhở",

                    createdAt = c.CreatedAt.ToString("HH:mm dd/MM/yyyy"),
                    updatedAt = c.UpdatedAt?.ToString("HH:mm dd/MM/yyyy") ?? "Chưa cập nhật"
                };

                return Json(new
                {
                    success = true,
                    data = responseData
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = "Lỗi hệ thống: " + ex.Message
                });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemindSeller(int id, bool force = false)
        {
            var c = await _context.Consultations
                .Include(x => x.Property).ThenInclude(p => p.User)
                .Include(x => x.Project).ThenInclude(p => p.Owner)
                .Include(x => x.AssignedUser)
                .FirstOrDefaultAsync(x => x.ConsultID == id);

            if (c == null)
            {
                return Json(new
                {
                    success = false,
                    message = "Không tìm thấy yêu cầu tư vấn."
                });
            }

            bool wasReminded = IsReminded(c.SellerNote);

            if (wasReminded && !force)
            {
                return Json(new
                {
                    success = false,
                    code = "ALREADY_REMINDED",
                    message = "Yêu cầu này đã từng được gửi thông báo cho người bán. Bạn có muốn gửi nhắc lại không?"
                });
            }

            int? targetUserId = c.AssignedToUserID
                                ?? c.Property?.UserID
                                ?? c.Project?.OwnerUserID;

            if (targetUserId == null || targetUserId.Value <= 0)
            {
                return Json(new
                {
                    success = false,
                    message = "Yêu cầu này chưa xác định được người phụ trách để nhắc nhở."
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

            string actorName = User.FindFirst("FullName")?.Value
                               ?? User.FindFirstValue(ClaimTypes.Name)
                               ?? "Admin/Staff";

            DateTime now = DateTime.Now;

            string sourceName = c.Property?.Title
                                ?? c.Project?.ProjectName
                                ?? "nguồn bất động sản không xác định";

            string customerName = string.IsNullOrWhiteSpace(c.FullName)
                ? "khách vãng lai"
                : c.FullName.Trim();

            string notificationTitle = force
                ? "🔔 Nhắc lại: Khách đang chờ tư vấn"
                : "🔔 Admin nhắc nhở: Khách chờ tư vấn";

            string notificationContent =
                $"Bạn có một yêu cầu tư vấn từ khách hàng {customerName} liên quan đến \"{sourceName}\". " +
                "Vui lòng liên hệ khách hàng và cập nhật trạng thái xử lý trên hệ thống.";

            _context.Notifications.Add(new Notification
            {
                UserID = targetUserId.Value,
                Title = notificationTitle,
                Content = notificationContent,
                ActionUrl = BuildLeadActionUrl(c),
                ActionText = "Xem yêu cầu",
                CreatedAt = now,
                IsRead = false
            });

            string remindLog =
                $"{REMIND_MARKER} {now:HH:mm dd/MM/yyyy} - {actorName} đã gửi thông báo nhắc người phụ trách: {targetUser.FullName ?? targetUser.Username}.";

            if (string.IsNullOrWhiteSpace(c.SellerNote))
            {
                c.SellerNote = remindLog;
            }
            else
            {
                c.SellerNote = c.SellerNote.Trim() + Environment.NewLine + remindLog;
            }

            c.UpdatedAt = now;

            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                reminded = true,
                remindedAt = now.ToString("HH:mm dd/MM/yyyy"),
                message = force
                    ? "Đã gửi nhắc lại thành công và vẫn giữ trạng thái đã thông báo."
                    : "Đã gửi thông báo nhắc nhở thành công và đánh dấu yêu cầu này là đã thông báo."
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id, bool blockSpam = false)
        {
            var item = await _context.Consultations.FindAsync(id);

            if (item == null)
            {
                return Json(new
                {
                    success = false,
                    message = "Không tìm thấy yêu cầu tư vấn."
                });
            }

            _context.Consultations.Remove(item);
            await _context.SaveChangesAsync();

            string msg = blockSpam
                ? "Đã xóa yêu cầu rác và ghi nhận là Spam."
                : "Đã xóa vĩnh viễn yêu cầu tư vấn khỏi hệ thống.";

            return Json(new
            {
                success = true,
                message = msg
            });
        }

        [HttpGet]
        public async Task<IActionResult> ExportCsv()
        {
            var leads = await _context.Consultations
                .AsNoTracking()
                .Include(c => c.Property)
                .Include(c => c.Project)
                .Include(c => c.AssignedUser)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();

            var builder = new StringBuilder();
            builder.Append('\uFEFF');
            builder.AppendLine("Mã Lead,Ngày Tạo,Khách Hàng,SĐT,Email,Nguồn,Người Phụ Trách,Trạng Thái,Đã Nhắc");

            foreach (var c in leads)
            {
                string sourceName = c.Property?.Title
                                    ?? c.Project?.ProjectName
                                    ?? "N/A";

                string handlerName = c.AssignedUser?.FullName ?? "N/A";

                string statusText = c.Status switch
                {
                    "New" => "Mới gửi",
                    "Contacted" => "Đã liên hệ",
                    "Closed" => "Đã chốt",
                    "Cancelled" => "Đã hủy",
                    "Spam" => "Spam",
                    _ => c.Status ?? "N/A"
                };

                string remindedText = IsReminded(c.SellerNote) ? "Đã nhắc" : "Chưa nhắc";

                builder.AppendLine(
                    $"{c.ConsultID}," +
                    $"{c.CreatedAt:dd/MM/yyyy HH:mm}," +
                    $"\"{EscapeCsv(c.FullName ?? "Khách vãng lai")}\"," +
                    $"\"{EscapeCsv(c.Phone ?? "N/A")}\"," +
                    $"\"{EscapeCsv(c.Email ?? "N/A")}\"," +
                    $"\"{EscapeCsv(sourceName)}\"," +
                    $"\"{EscapeCsv(handlerName)}\"," +
                    $"\"{EscapeCsv(statusText)}\"," +
                    $"\"{EscapeCsv(remindedText)}\"");
            }

            return File(
                Encoding.UTF8.GetBytes(builder.ToString()),
                "text/csv",
                $"ThongKeYeuCauTuVan_BDS_{DateTime.Now:yyyyMMdd}.csv");
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

        private static string EscapeCsv(string value)
        {
            return value.Replace("\"", "\"\"");
        }

        private static string BuildLeadActionUrl(Consultation consultation)
        {
            return "/User/Consultations";
        }
    }
}