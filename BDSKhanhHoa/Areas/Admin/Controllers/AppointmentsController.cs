using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Staff")]
    [Route("Admin/[controller]/[action]")]
    public class AppointmentsController : Controller
    {
        private readonly ApplicationDbContext _context;

        private const string REMIND_MARKER = "[REMIND_SELLER_APPOINTMENT]";
        private const string REMIND_DISPLAY_TEXT = "Đã gửi thông báo nhắc người bán";

        public AppointmentsController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> Index(
            string? keyword = null,
            string? source = "All",
            string dateRange = "all",
            int page = 1)
        {
            const int pageSize = 15;

            var query = _context.Appointments
                .AsNoTracking()
                .Include(a => a.Property).ThenInclude(p => p.Project)
                .Include(a => a.Project)
                .Include(a => a.Buyer)
                .Include(a => a.Seller)
                .Include(a => a.Lead).ThenInclude(l => l.Project)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(source) &&
                !source.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                if (source.Equals("Property", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(a => a.PropertyID != null);
                }
                else if (source.Equals("Project", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(a => a.ProjectID != null && a.PropertyID == null);
                }
                else if (source.Equals("Lead", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(a => a.LeadID != null);
                }
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                keyword = keyword.Trim();

                query = query.Where(a =>
                    (a.CustomerName != null && EF.Functions.Like(a.CustomerName, $"%{keyword}%")) ||
                    (a.CustomerPhone != null && EF.Functions.Like(a.CustomerPhone, $"%{keyword}%")) ||
                    (a.AssignedStaffName != null && EF.Functions.Like(a.AssignedStaffName, $"%{keyword}%")) ||
                    (a.Buyer != null && a.Buyer.Username != null && EF.Functions.Like(a.Buyer.Username, $"%{keyword}%")) ||
                    (a.Buyer != null && a.Buyer.FullName != null && EF.Functions.Like(a.Buyer.FullName, $"%{keyword}%")) ||
                    (a.Seller != null && a.Seller.FullName != null && EF.Functions.Like(a.Seller.FullName, $"%{keyword}%")) ||
                    (a.Property != null && a.Property.Title != null && EF.Functions.Like(a.Property.Title, $"%{keyword}%")) ||
                    (a.Project != null && a.Project.ProjectName != null && EF.Functions.Like(a.Project.ProjectName, $"%{keyword}%")) ||
                    (a.Lead != null && a.Lead.Project != null && a.Lead.Project.ProjectName != null && EF.Functions.Like(a.Lead.Project.ProjectName, $"%{keyword}%"))
                );
            }

            var today = DateTime.Now.Date;

            switch (dateRange?.Trim().ToLowerInvariant())
            {
                case "today":
                    query = query.Where(a => a.AppointmentDate >= today && a.AppointmentDate < today.AddDays(1));
                    break;

                case "week":
                    query = query.Where(a => a.AppointmentDate >= today.AddDays(-7));
                    break;

                case "month":
                    query = query.Where(a => a.AppointmentDate >= today.AddMonths(-1));
                    break;
            }

            ViewBag.TotalAppointments = await query.CountAsync();
            ViewBag.PendingAppointments = await query.CountAsync(a => a.Status == "Pending" || a.Status == "Rescheduled");
            ViewBag.ConfirmedAppointments = await query.CountAsync(a => a.Status == "Confirmed");
            ViewBag.CompletedAppointments = await query.CountAsync(a => a.Status == "Completed");
            ViewBag.CancelledAppointments = await query.CountAsync(a => a.Status == "Cancelled");
            ViewBag.InterestedCount = await query.CountAsync(a => a.ResultStatus == "Interested" || a.ResultStatus == "DepositPending");
            ViewBag.RemindedAppointments = await query.CountAsync(a => a.NegotiationNote != null && a.NegotiationNote.Contains(REMIND_MARKER));

            int totalItems = await query.CountAsync();
            int totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize));

            page = Math.Clamp(page, 1, totalPages);

            var list = await query
                .OrderByDescending(a => a.AppointmentDate)
                .ThenByDescending(a => a.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.Keyword = keyword;
            ViewBag.Source = string.IsNullOrWhiteSpace(source) ? "All" : source;
            ViewBag.DateRange = string.IsNullOrWhiteSpace(dateRange) ? "all" : dateRange;
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;

            return View(list);
        }

        [HttpGet]
        public async Task<IActionResult> GetDetails(int id)
        {
            try
            {
                var a = await _context.Appointments
                    .AsNoTracking()
                    .Include(x => x.Buyer)
                    .Include(x => x.Seller)
                    .Include(x => x.Property)
                    .Include(x => x.Project)
                    .FirstOrDefaultAsync(x => x.AppointmentID == id);

                if (a == null)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Không tìm thấy lịch hẹn."
                    });
                }

                string sourceName = a.Property?.Title
                                    ?? a.Project?.ProjectName
                                    ?? "Không xác định";

                bool wasReminded = IsReminded(a.NegotiationNote);
                string cleanNegotiationNote = CleanReminderMarker(a.NegotiationNote);

                var responseData = new
                {
                    id = a.AppointmentID,
                    appointmentDate = a.AppointmentDate.ToString("HH:mm dd/MM/yyyy"),
                    proposedDate = a.ProposedAppointmentDate?.ToString("HH:mm dd/MM/yyyy") ?? "Không có",
                    createdAt = a.CreatedAt.ToString("HH:mm dd/MM/yyyy"),
                    completedAt = a.CompletedAt?.ToString("HH:mm dd/MM/yyyy") ?? "Chưa hoàn tất",

                    status = a.Status ?? "N/A",
                    resultStatus = a.ResultStatus ?? "Chưa có",

                    buyerName = !string.IsNullOrWhiteSpace(a.CustomerName)
                        ? a.CustomerName
                        : (a.Buyer?.FullName ?? a.Buyer?.Username ?? "Khách vãng lai"),

                    buyerPhone = a.CustomerPhone ?? "Không có",

                    sellerName = a.Seller?.FullName ?? "Chưa xác định",
                    sellerPhone = a.Seller?.Phone ?? "Không có",

                    source = sourceName,
                    location = a.MeetingLocation ?? "Chưa xác định",
                    note = a.Note ?? "Không có ghi chú",

                    negotiationNote = string.IsNullOrWhiteSpace(cleanNegotiationNote)
                        ? "Chưa có lịch sử thương lượng."
                        : cleanNegotiationNote,

                    resultNote = a.ResultNote ?? "Không có",
                    wasReminded = wasReminded,
                    remindedText = wasReminded ? REMIND_DISPLAY_TEXT : "Chưa gửi nhắc nhở"
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
                    message = "Lỗi xử lý hệ thống: " + ex.Message
                });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemindSeller(int id, bool force = false)
        {
            var a = await _context.Appointments
                .Include(x => x.Property)
                .Include(x => x.Project)
                .Include(x => x.Seller)
                .Include(x => x.Buyer)
                .FirstOrDefaultAsync(x => x.AppointmentID == id);

            if (a == null)
            {
                return Json(new
                {
                    success = false,
                    message = "Không tìm thấy lịch hẹn."
                });
            }

            bool wasReminded = IsReminded(a.NegotiationNote);

            if (wasReminded && !force)
            {
                return Json(new
                {
                    success = false,
                    code = "ALREADY_REMINDED",
                    message = "Lịch hẹn này đã từng được gửi thông báo cho người bán. Bạn có muốn gửi nhắc lại không?"
                });
            }

            int? targetUserId = null;

            if (a.SellerID > 0)
            {
                targetUserId = a.SellerID;
            }
            else if (a.Property?.UserID != null && a.Property.UserID > 0)
            {
                targetUserId = a.Property.UserID;
            }
            else if (a.Project?.OwnerUserID != null && a.Project.OwnerUserID > 0)
            {
                targetUserId = a.Project.OwnerUserID;
            }

            if (targetUserId == null || targetUserId.Value <= 0)
            {
                return Json(new
                {
                    success = false,
                    message = "Lịch hẹn này chưa xác định được người bán hoặc người phụ trách để nhắc nhở."
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
                    message = "Người bán/người phụ trách không tồn tại hoặc tài khoản đã bị khóa."
                });
            }

            DateTime now = DateTime.Now;

            string actorName = User.FindFirst("FullName")?.Value
                               ?? User.Identity?.Name
                               ?? "Admin/Staff";

            string customerName = !string.IsNullOrWhiteSpace(a.CustomerName)
                ? a.CustomerName.Trim()
                : (a.Buyer?.FullName ?? a.Buyer?.Username ?? "khách hàng");

            string sourceName = a.Property?.Title
                                ?? a.Project?.ProjectName
                                ?? "nguồn bất động sản không xác định";

            string appointmentTime = a.AppointmentDate.ToString("HH:mm dd/MM/yyyy");

            string notificationTitle = force
                ? "🔔 Nhắc lại: Lịch hẹn đang chờ xử lý"
                : "🔔 Admin nhắc nhở: Xử lý lịch hẹn";

            string notificationContent =
                $"Bạn có lịch hẹn với khách hàng {customerName} vào {appointmentTime}, liên quan đến \"{sourceName}\". " +
                "Vui lòng kiểm tra, liên hệ khách và cập nhật tiến độ xử lý trên hệ thống.";

            _context.Notifications.Add(new Notification
            {
                UserID = targetUserId.Value,
                Title = notificationTitle,
                Content = notificationContent,
                ActionUrl = BuildAppointmentActionUrl(a),
                ActionText = "Xem lịch hẹn",
                CreatedAt = now,
                IsRead = false
            });

            string remindLog =
                $"{REMIND_MARKER} {now:HH:mm dd/MM/yyyy} - {actorName} đã gửi thông báo nhắc người phụ trách: {targetUser.FullName ?? targetUser.Username}.";

            if (string.IsNullOrWhiteSpace(a.NegotiationNote))
            {
                a.NegotiationNote = remindLog;
            }
            else
            {
                a.NegotiationNote = a.NegotiationNote.Trim() + Environment.NewLine + remindLog;
            }

            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                reminded = true,
                remindedAt = now.ToString("HH:mm dd/MM/yyyy"),
                message = force
                    ? "Đã gửi nhắc lại thành công và vẫn giữ trạng thái đã thông báo."
                    : "Đã gửi thông báo nhắc nhở thành công và đánh dấu lịch hẹn này là đã thông báo."
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id, bool blockSpam = false)
        {
            var item = await _context.Appointments.FindAsync(id);

            if (item == null)
            {
                return Json(new
                {
                    success = false,
                    message = "Không tìm thấy lịch hẹn."
                });
            }

            _context.Appointments.Remove(item);
            await _context.SaveChangesAsync();

            string msg = blockSpam
                ? "Đã xóa lịch hẹn ảo và ghi nhận là dữ liệu Spam."
                : "Đã xóa vĩnh viễn dữ liệu lịch hẹn khỏi hệ thống.";

            return Json(new
            {
                success = true,
                message = msg
            });
        }

        [HttpGet]
        public async Task<IActionResult> ExportCsv()
        {
            var appointments = await _context.Appointments
                .AsNoTracking()
                .Include(a => a.Buyer)
                .Include(a => a.Seller)
                .Include(a => a.Property)
                .Include(a => a.Project)
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync();

            var builder = new StringBuilder();
            builder.Append('\uFEFF');
            builder.AppendLine("Mã Lịch Hẹn,Ngày Tạo,Thời Gian Hẹn,Khách Hàng,SĐT Khách,Người Bán/Phụ Trách,Nguồn BĐS,Trạng Thái,Kết Quả,Đã Nhắc");

            foreach (var a in appointments)
            {
                var customer = string.IsNullOrWhiteSpace(a.CustomerName)
                    ? (a.Buyer?.FullName ?? a.Buyer?.Username ?? "N/A")
                    : a.CustomerName;

                var seller = a.Seller?.FullName ?? "N/A";

                var sourceName = a.Property?.Title
                                 ?? a.Project?.ProjectName
                                 ?? "N/A";

                string statusText = a.Status switch
                {
                    "Pending" => "Chờ xác nhận",
                    "Confirmed" => "Đã xác nhận",
                    "Rescheduled" => "Đang dời lịch",
                    "Cancelled" => "Đã hủy",
                    "Completed" => "Hoàn tất",
                    _ => a.Status ?? "N/A"
                };

                string resultText = a.ResultStatus switch
                {
                    "Interested" => "Khách ưng ý",
                    "DepositPending" => "Chờ chốt cọc",
                    "FollowUp" => "Cần bám sát",
                    "NotInterested" => "Không ưng",
                    _ => "Chưa có"
                };

                string remindedText = IsReminded(a.NegotiationNote) ? "Đã nhắc" : "Chưa nhắc";

                builder.AppendLine(
                    $"{a.AppointmentID}," +
                    $"{a.CreatedAt:dd/MM/yyyy HH:mm}," +
                    $"{a.AppointmentDate:dd/MM/yyyy HH:mm}," +
                    $"\"{EscapeCsv(customer)}\"," +
                    $"\"{EscapeCsv(a.CustomerPhone ?? "N/A")}\"," +
                    $"\"{EscapeCsv(seller)}\"," +
                    $"\"{EscapeCsv(sourceName)}\"," +
                    $"\"{EscapeCsv(statusText)}\"," +
                    $"\"{EscapeCsv(resultText)}\"," +
                    $"\"{EscapeCsv(remindedText)}\"");
            }

            return File(
                Encoding.UTF8.GetBytes(builder.ToString()),
                "text/csv",
                $"ThongKeLichHen_BDS_{DateTime.Now:yyyyMMdd}.csv");
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

        private static string BuildAppointmentActionUrl(Appointment appointment)
        {
            return "/User/Appointments";
        }
    }
}
