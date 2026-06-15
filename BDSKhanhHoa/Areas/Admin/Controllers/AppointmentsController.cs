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
    public class AppointmentsController : Controller
    {
        private readonly ApplicationDbContext _context;

        private const string REMIND_MARKER = "[REMIND_SELLER_APPOINTMENT]";
        private const string REMIND_DISPLAY_TEXT = "Đã gửi thông báo nhắc người phụ trách";

        public AppointmentsController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> Index(
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

            IQueryable<Appointment> scopedQuery = _context.Appointments
                .AsNoTracking()
                .Include(a => a.Property).ThenInclude(p => p.Project)
                .Include(a => a.Project)
                .Include(a => a.Buyer)
                .Include(a => a.Seller)
                .Include(a => a.Lead).ThenInclude(l => l.Project)
                .AsQueryable();

            scopedQuery = ApplySourceFilter(scopedQuery, source);
            scopedQuery = ApplyDateFilter(scopedQuery, dateRange);
            scopedQuery = ApplyKeywordFilter(scopedQuery, keyword);

            ViewBag.TotalAppointments = await scopedQuery.CountAsync();
            ViewBag.NeedsReminderAppointments = await scopedQuery.CountAsync(a =>
                (a.Status == "Pending" || a.Status == "Chờ xác nhận") &&
                (a.NegotiationNote == null || !a.NegotiationNote.Contains(REMIND_MARKER)));

            ViewBag.RemindedAppointments = await scopedQuery.CountAsync(a =>
                a.NegotiationNote != null && a.NegotiationNote.Contains(REMIND_MARKER));

            ViewBag.HandledAppointments = await scopedQuery.CountAsync(a =>
                !(a.Status == "Pending" || a.Status == "Chờ xác nhận") ||
                (a.NegotiationNote != null && a.NegotiationNote.Contains(REMIND_MARKER)));

            ViewBag.PendingAppointments = await scopedQuery.CountAsync(a =>
                a.Status == "Pending" || a.Status == "Chờ xác nhận" ||
                a.Status == "Rescheduled" || a.Status == "Đang dời lịch");

            ViewBag.ConfirmedAppointments = await scopedQuery.CountAsync(a =>
                a.Status == "Confirmed" || a.Status == "Đã xác nhận");

            ViewBag.CompletedAppointments = await scopedQuery.CountAsync(a =>
                a.Status == "Completed" || a.Status == "Đã hoàn tất");

            ViewBag.CancelledAppointments = await scopedQuery.CountAsync(a =>
                a.Status == "Cancelled" || a.Status == "Đã hủy" ||
                a.Status == "NoShow" || a.Status == "Khách không đến");

            IQueryable<Appointment> listQuery = ApplyReminderFilter(scopedQuery, remindFilter);

            int totalItems = await listQuery.CountAsync();
            int totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize));
            page = Math.Clamp(page, 1, totalPages);

            var appointments = await listQuery
                .OrderByDescending(a =>
                    (a.Status == "Pending" || a.Status == "Chờ xác nhận") &&
                    (a.NegotiationNote == null || !a.NegotiationNote.Contains(REMIND_MARKER)))
                .ThenBy(a => a.AppointmentDate)
                .ThenByDescending(a => a.CreatedAt)
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

            return View(appointments);
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
                    .Include(x => x.Property).ThenInclude(p => p.Project)
                    .Include(x => x.Project)
                    .Include(x => x.Lead).ThenInclude(l => l.Project)
                    .FirstOrDefaultAsync(x => x.AppointmentID == id);

                if (a == null)
                {
                    return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });
                }

                bool wasReminded = IsReminded(a.NegotiationNote);
                bool canRemind = CanRemindAppointment(a.Status, a.NegotiationNote);

                var responseData = new
                {
                    id = a.AppointmentID,
                    appointmentDate = a.AppointmentDate.ToString("HH:mm dd/MM/yyyy"),
                    proposedDate = a.ProposedAppointmentDate?.ToString("HH:mm dd/MM/yyyy") ?? "Không có",
                    createdAt = a.CreatedAt.ToString("HH:mm dd/MM/yyyy"),
                    completedAt = a.CompletedAt?.ToString("HH:mm dd/MM/yyyy") ?? "Chưa hoàn tất",
                    status = GetAppointmentStatusText(a.Status),
                    rawStatus = a.Status ?? "",
                    resultStatus = GetAppointmentResultText(a.ResultStatus),
                    buyerName = GetCustomerName(a),
                    buyerPhone = string.IsNullOrWhiteSpace(a.CustomerPhone) ? "Không có" : a.CustomerPhone.Trim(),
                    sellerName = GetSellerName(a),
                    sellerPhone = a.Seller?.Phone ?? "Không có",
                    source = GetAppointmentSourceName(a),
                    sourceType = GetAppointmentSourceType(a),
                    location = string.IsNullOrWhiteSpace(a.MeetingLocation) ? "Chưa xác định" : a.MeetingLocation.Trim(),
                    note = string.IsNullOrWhiteSpace(a.Note) ? "Không có ghi chú" : a.Note.Trim(),
                    negotiationNote = string.IsNullOrWhiteSpace(CleanReminderMarker(a.NegotiationNote))
                        ? "Chưa có lịch sử thương lượng."
                        : CleanReminderMarker(a.NegotiationNote),
                    resultNote = string.IsNullOrWhiteSpace(a.ResultNote) ? "Không có" : a.ResultNote.Trim(),
                    wasReminded,
                    canRemind,
                    remindedText = wasReminded ? REMIND_DISPLAY_TEXT : "Chưa gửi thông báo"
                };

                return Json(new { success = true, data = responseData });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Lỗi xử lý hệ thống: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemindSeller(int id)
        {
            var a = await _context.Appointments
                .Include(x => x.Property).ThenInclude(p => p.Project)
                .Include(x => x.Project)
                .Include(x => x.Seller)
                .Include(x => x.Buyer)
                .Include(x => x.Lead).ThenInclude(l => l.Project)
                .FirstOrDefaultAsync(x => x.AppointmentID == id);

            if (a == null)
            {
                return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });
            }

            if (!CanRemindAppointment(a.Status, a.NegotiationNote))
            {
                bool wasReminded = IsReminded(a.NegotiationNote);

                return Json(new
                {
                    success = false,
                    code = wasReminded ? "ALREADY_REMINDED" : "ALREADY_HANDLED",
                    message = wasReminded
                        ? "Lịch hẹn này đã được thông báo rồi, hệ thống không gửi lại để tránh làm phiền người phụ trách."
                        : "Lịch hẹn này đã được tiếp nhận/xử lý, Admin không cần bấm chuông nữa."
                });
            }

            int? targetUserId = GetAppointmentTargetUserId(a);

            if (targetUserId == null || targetUserId.Value <= 0)
            {
                return Json(new
                {
                    success = false,
                    message = "Lịch hẹn này chưa xác định được người bán hoặc người phụ trách để thông báo."
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

            string customerName = GetCustomerName(a);
            string sourceName = GetAppointmentSourceName(a);
            string appointmentTime = a.AppointmentDate.ToString("HH:mm dd/MM/yyyy");

            _context.Notifications.Add(new Notification
            {
                UserID = targetUserId.Value,
                Title = "🔔 Admin nhắc xử lý lịch hẹn",
                Content = $"Bạn có lịch hẹn mới/chưa xử lý với khách {customerName} vào {appointmentTime}, liên quan đến \"{sourceName}\". Vui lòng liên hệ khách và cập nhật trạng thái trên hệ thống.",
                ActionUrl = BuildAppointmentActionUrl(a),
                ActionText = "Xem lịch hẹn",
                CreatedAt = now,
                IsRead = false
            });

            string remindLog =
                $"{REMIND_MARKER} {now:HH:mm dd/MM/yyyy} - {actorName} đã gửi thông báo nhắc người phụ trách: {targetUser.FullName ?? targetUser.Username}.";

            a.NegotiationNote = string.IsNullOrWhiteSpace(a.NegotiationNote)
                ? remindLog
                : a.NegotiationNote.Trim() + Environment.NewLine + remindLog;

            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                id = a.AppointmentID,
                reminded = true,
                remindedAt = now.ToString("HH:mm dd/MM/yyyy"),
                message = "Đã gửi chuông thông báo. Mục này sẽ được ẩn khỏi danh sách cần xử lý."
            });
        }

        [HttpGet]
        public async Task<IActionResult> ExportCsv(
            string? keyword = null,
            string? source = "All",
            string? remindFilter = "All",
            string? dateRange = "all")
        {
            keyword = CleanKeyword(keyword);
            source = NormalizeFilter(source, "All");
            remindFilter = NormalizeFilter(remindFilter, "All");
            dateRange = NormalizeFilter(dateRange, "all");

            IQueryable<Appointment> query = _context.Appointments
                .AsNoTracking()
                .Include(a => a.Buyer)
                .Include(a => a.Seller)
                .Include(a => a.Property).ThenInclude(p => p.Project)
                .Include(a => a.Project)
                .Include(a => a.Lead).ThenInclude(l => l.Project)
                .AsQueryable();

            query = ApplySourceFilter(query, source);
            query = ApplyDateFilter(query, dateRange);
            query = ApplyKeywordFilter(query, keyword);
            query = ApplyReminderFilter(query, remindFilter);

            var appointments = await query
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync();

            var builder = new StringBuilder();
            builder.Append('\uFEFF');
            builder.AppendLine("MaLichHen,NgayTao,ThoiGianHen,KhachHang,SDTKhach,NguoiPhuTrach,Nguon,LoaiNguon,TrangThai,KetQua,TinhTrangThongBao");

            foreach (var a in appointments)
            {
                builder.AppendLine(
                    $"{a.AppointmentID}," +
                    $"{a.CreatedAt:dd/MM/yyyy HH:mm}," +
                    $"{a.AppointmentDate:dd/MM/yyyy HH:mm}," +
                    $"\"{EscapeCsv(GetCustomerName(a))}\"," +
                    $"\"{EscapeCsv(a.CustomerPhone ?? "")}\"," +
                    $"\"{EscapeCsv(GetSellerName(a))}\"," +
                    $"\"{EscapeCsv(GetAppointmentSourceName(a))}\"," +
                    $"\"{EscapeCsv(GetAppointmentSourceType(a))}\"," +
                    $"\"{EscapeCsv(GetAppointmentStatusText(a.Status))}\"," +
                    $"\"{EscapeCsv(GetAppointmentResultText(a.ResultStatus))}\"," +
                    $"\"{EscapeCsv(IsReminded(a.NegotiationNote) ? "Đã thông báo" : "Chưa thông báo")}\"");
            }

            return File(
                Encoding.UTF8.GetBytes(builder.ToString()),
                "text/csv",
                $"LichHenCanGiamSat_{DateTime.Now:yyyyMMddHHmm}.csv");
        }

        private static IQueryable<Appointment> ApplySourceFilter(IQueryable<Appointment> query, string source)
        {
            if (source.Equals("Property", StringComparison.OrdinalIgnoreCase))
            {
                return query.Where(a =>
                    a.PropertyID != null &&
                    a.ProjectID == null &&
                    a.LeadID == null &&
                    (a.Property == null || a.Property.ProjectID == null));
            }

            if (source.Equals("Project", StringComparison.OrdinalIgnoreCase))
            {
                return query.Where(a =>
                    a.ProjectID != null ||
                    a.LeadID != null ||
                    (a.Property != null && a.Property.ProjectID != null));
            }

            if (source.Equals("Lead", StringComparison.OrdinalIgnoreCase))
            {
                return query.Where(a => a.LeadID != null);
            }

            return query;
        }

        private static IQueryable<Appointment> ApplyDateFilter(IQueryable<Appointment> query, string dateRange)
        {
            DateTime today = DateTime.Now.Date;

            return dateRange.Trim().ToLowerInvariant() switch
            {
                "today" => query.Where(a => a.CreatedAt >= today && a.CreatedAt < today.AddDays(1)),
                "appointment_today" => query.Where(a => a.AppointmentDate >= today && a.AppointmentDate < today.AddDays(1)),
                "week" => query.Where(a => a.CreatedAt >= today.AddDays(-7)),
                "month" => query.Where(a => a.CreatedAt >= today.AddMonths(-1)),
                _ => query
            };
        }

        private static IQueryable<Appointment> ApplyKeywordFilter(IQueryable<Appointment> query, string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return query;
            }

            return query.Where(a =>
                (a.CustomerName != null && EF.Functions.Like(a.CustomerName, $"%{keyword}%")) ||
                (a.CustomerPhone != null && EF.Functions.Like(a.CustomerPhone, $"%{keyword}%")) ||
                (a.AssignedStaffName != null && EF.Functions.Like(a.AssignedStaffName, $"%{keyword}%")) ||
                (a.Buyer != null && a.Buyer.Username != null && EF.Functions.Like(a.Buyer.Username, $"%{keyword}%")) ||
                (a.Buyer != null && a.Buyer.FullName != null && EF.Functions.Like(a.Buyer.FullName, $"%{keyword}%")) ||
                (a.Seller != null && a.Seller.FullName != null && EF.Functions.Like(a.Seller.FullName, $"%{keyword}%")) ||
                (a.Property != null && a.Property.Title != null && EF.Functions.Like(a.Property.Title, $"%{keyword}%")) ||
                (a.Project != null && a.Project.ProjectName != null && EF.Functions.Like(a.Project.ProjectName, $"%{keyword}%")) ||
                (a.Lead != null && a.Lead.Project != null && a.Lead.Project.ProjectName != null && EF.Functions.Like(a.Lead.Project.ProjectName, $"%{keyword}%")));
        }

        private static IQueryable<Appointment> ApplyReminderFilter(IQueryable<Appointment> query, string remindFilter)
        {
            if (remindFilter.Equals("CanRemind", StringComparison.OrdinalIgnoreCase) ||
                remindFilter.Equals("NeedNotify", StringComparison.OrdinalIgnoreCase))
            {
                return query.Where(a =>
                    (a.Status == "Pending" || a.Status == "Chờ xác nhận") &&
                    (a.NegotiationNote == null || !a.NegotiationNote.Contains(REMIND_MARKER)));
            }

            if (remindFilter.Equals("Reminded", StringComparison.OrdinalIgnoreCase))
            {
                return query.Where(a => a.NegotiationNote != null && a.NegotiationNote.Contains(REMIND_MARKER));
            }

            if (remindFilter.Equals("Handled", StringComparison.OrdinalIgnoreCase))
            {
                return query.Where(a =>
                    !(a.Status == "Pending" || a.Status == "Chờ xác nhận") ||
                    (a.NegotiationNote != null && a.NegotiationNote.Contains(REMIND_MARKER)));
            }

            return query;
        }

        private static int? GetAppointmentTargetUserId(Appointment appointment)
        {
            if (appointment.SellerID > 0)
            {
                return appointment.SellerID;
            }

            if (appointment.Project?.OwnerUserID != null && appointment.Project.OwnerUserID > 0)
            {
                return appointment.Project.OwnerUserID;
            }

            if (appointment.Lead?.Project?.OwnerUserID != null && appointment.Lead.Project.OwnerUserID > 0)
            {
                return appointment.Lead.Project.OwnerUserID;
            }

            if (appointment.Property?.Project?.OwnerUserID != null && appointment.Property.Project.OwnerUserID > 0)
            {
                return appointment.Property.Project.OwnerUserID;
            }

            if (appointment.Property?.UserID != null && appointment.Property.UserID > 0)
            {
                return appointment.Property.UserID;
            }

            return null;
        }

        private static bool CanRemindAppointment(string? status, string? negotiationNote)
        {
            return (status == "Pending" || status == "Chờ xác nhận") && !IsReminded(negotiationNote);
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

        private static string GetCustomerName(Appointment a)
        {
            return !string.IsNullOrWhiteSpace(a.CustomerName)
                ? a.CustomerName.Trim()
                : (a.Buyer?.FullName ?? a.Buyer?.Username ?? "Khách vãng lai");
        }

        private static string GetSellerName(Appointment a)
        {
            if (a.Seller != null)
            {
                return a.Seller.FullName ?? a.Seller.Username ?? "Người phụ trách";
            }

            if (!string.IsNullOrWhiteSpace(a.AssignedStaffName))
            {
                return a.AssignedStaffName.Trim();
            }

            return "Chưa xác định";
        }

        private static string GetAppointmentSourceName(Appointment a)
        {
            return a.Property?.Title
                   ?? a.Project?.ProjectName
                   ?? a.Lead?.Project?.ProjectName
                   ?? "Bất động sản / dự án";
        }

        private static string GetAppointmentSourceType(Appointment a)
        {
            if (a.ProjectID.HasValue || a.LeadID.HasValue || a.Property?.ProjectID != null)
            {
                return "Dự án";
            }

            if (a.PropertyID.HasValue)
            {
                return "Tin lẻ BĐS";
            }

            return "Không xác định";
        }

        private static string GetAppointmentStatusText(string? status)
        {
            return status switch
            {
                "Pending" => "Chờ xác nhận",
                "Confirmed" => "Đã xác nhận",
                "Cancelled" => "Đã hủy",
                "Completed" => "Đã hoàn tất",
                "Rescheduled" => "Đang dời lịch",
                "NoShow" => "Khách không đến",
                "Chờ xác nhận" => "Chờ xác nhận",
                "Đã xác nhận" => "Đã xác nhận",
                "Đã hủy" => "Đã hủy",
                "Đã hoàn tất" => "Đã hoàn tất",
                "Đang dời lịch" => "Đang dời lịch",
                "Khách không đến" => "Khách không đến",
                _ => "Chờ xác nhận"
            };
        }

        private static string GetAppointmentResultText(string? resultStatus)
        {
            return resultStatus switch
            {
                "Interested" => "Khách quan tâm",
                "NotInterested" => "Khách không quan tâm",
                "DepositPending" => "Chờ đặt cọc",
                "FollowUp" => "Cần chăm sóc thêm",
                "Khách quan tâm" => "Khách quan tâm",
                "Khách không quan tâm" => "Khách không quan tâm",
                "Chờ đặt cọc" => "Chờ đặt cọc",
                "Cần chăm sóc thêm" => "Cần chăm sóc thêm",
                _ => "Chưa có kết quả"
            };
        }

        private static string BuildAppointmentActionUrl(Appointment appointment)
        {
            bool isProjectAppointment =
                appointment.ProjectID.HasValue ||
                appointment.LeadID.HasValue ||
                (appointment.Property != null && appointment.Property.ProjectID.HasValue);

            return isProjectAppointment
                ? "/Appointments/Index?mode=DoanhNghiep&tab=lich-den"
                : "/Appointments/Index?mode=CaNhan&tab=lich-den";
        }
    }
}
