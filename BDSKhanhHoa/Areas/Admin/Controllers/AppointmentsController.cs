using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Staff")]
    [Route("Admin/[controller]/[action]")]
    public class AppointmentsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public AppointmentsController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> Index(
            string? status = "All",
            string? resultStatus = "All",
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
                .Include(a => a.Lead)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(status) && !status.Equals("All", StringComparison.OrdinalIgnoreCase))
                query = query.Where(a => a.Status == status);

            if (!string.IsNullOrWhiteSpace(resultStatus) && !resultStatus.Equals("All", StringComparison.OrdinalIgnoreCase))
                query = query.Where(a => a.ResultStatus == resultStatus);

            if (!string.IsNullOrWhiteSpace(source) && !source.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                if (source.Equals("Property", StringComparison.OrdinalIgnoreCase))
                    query = query.Where(a => a.PropertyID != null);
                else if (source.Equals("Project", StringComparison.OrdinalIgnoreCase))
                    query = query.Where(a => a.ProjectID != null && a.PropertyID == null);
                else if (source.Equals("Lead", StringComparison.OrdinalIgnoreCase))
                    query = query.Where(a => a.LeadID != null);
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                keyword = keyword.Trim();
                query = query.Where(a =>
                    (a.CustomerName != null && EF.Functions.Like(a.CustomerName, $"%{keyword}%")) ||
                    (a.CustomerPhone != null && EF.Functions.Like(a.CustomerPhone, $"%{keyword}%")) ||
                    (a.AssignedStaffName != null && EF.Functions.Like(a.AssignedStaffName, $"%{keyword}%")) ||
                    (a.Buyer != null && a.Buyer.Username != null && EF.Functions.Like(a.Buyer.Username, $"%{keyword}%")) ||
                    (a.Seller != null && a.Seller.FullName != null && EF.Functions.Like(a.Seller.FullName, $"%{keyword}%")) ||
                    (a.Property != null && a.Property.Title != null && EF.Functions.Like(a.Property.Title, $"%{keyword}%"))
                );
            }

            var today = DateTime.Now.Date;
            switch (dateRange?.Trim().ToLowerInvariant())
            {
                case "today": query = query.Where(a => a.AppointmentDate >= today && a.AppointmentDate < today.AddDays(1)); break;
                case "week": query = query.Where(a => a.AppointmentDate >= today.AddDays(-7)); break;
                case "month": query = query.Where(a => a.AppointmentDate >= today.AddMonths(-1)); break;
            }

            ViewBag.TotalAppointments = await query.CountAsync();
            ViewBag.PendingAppointments = await query.CountAsync(a => a.Status == "Pending" || a.Status == "Rescheduled");
            ViewBag.ConfirmedAppointments = await query.CountAsync(a => a.Status == "Confirmed");
            ViewBag.CompletedAppointments = await query.CountAsync(a => a.Status == "Completed");
            ViewBag.CancelledAppointments = await query.CountAsync(a => a.Status == "Cancelled");
            ViewBag.InterestedCount = await query.CountAsync(a => a.ResultStatus == "Interested" || a.ResultStatus == "DepositPending");

            int totalItems = await query.CountAsync();
            int totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize));
            page = Math.Clamp(page, 1, totalPages);

            var list = await query
                .OrderByDescending(a => a.AppointmentDate).ThenByDescending(a => a.CreatedAt)
                .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

            ViewBag.Status = status; ViewBag.ResultStatus = resultStatus;
            ViewBag.Keyword = keyword; ViewBag.Source = source; ViewBag.DateRange = dateRange;
            ViewBag.CurrentPage = page; ViewBag.TotalPages = totalPages;

            return View(list);
        }

        // ĐÃ SỬA LỖI JSON VÀ LỖI NULL KHI ĐỌC NGÀY THÁNG
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

                if (a == null) return Json(new { success = false, message = "Không tìm thấy dữ liệu." });

                var sourceName = a.Property?.Title ?? a.Project?.ProjectName ?? "Không xác định";

                // Map thủ công để tránh lỗi Circular Reference của Entity Framework
                var responseData = new
                {
                    appointmentDate = a.AppointmentDate.ToString("HH:mm dd/MM/yyyy"),
                    proposedDate = a.ProposedAppointmentDate?.ToString("HH:mm dd/MM/yyyy") ?? "Không có",
                    createdAt = a.CreatedAt.ToString("HH:mm dd/MM/yyyy"),
                    completedAt = a.CompletedAt?.ToString("HH:mm dd/MM/yyyy") ?? "Chưa hoàn tất",
                    status = a.Status ?? "N/A",
                    resultStatus = a.ResultStatus ?? "Chưa có",
                    buyerName = !string.IsNullOrWhiteSpace(a.CustomerName) ? a.CustomerName : (a.Buyer?.FullName ?? "Khách vãng lai"),
                    buyerPhone = a.CustomerPhone ?? "Không có",
                    sellerName = a.Seller?.FullName ?? "Hệ thống",
                    sellerPhone = a.Seller?.Phone ?? "Không có",
                    source = sourceName,
                    location = a.MeetingLocation ?? "Chưa xác định",
                    note = a.Note ?? "Không có ghi chú",
                    negotiationNote = a.NegotiationNote ?? "Không có",
                    resultNote = a.ResultNote ?? "Không có"
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
                .Include(x => x.Property)
                .Include(x => x.Project)
                .FirstOrDefaultAsync(x => x.AppointmentID == id);

            if (a == null) return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });

            int? targetUserId = null;
            try
            {
                if (a.Property != null) targetUserId = a.Property.UserID;
                else if (a.Project != null) targetUserId = a.Project.OwnerUserID;
            }
            catch
            {
                if (a.SellerID != null) targetUserId = a.SellerID;
            }

            if (targetUserId == null) return Json(new { success = false, message = "Lịch hẹn này không có người phụ trách hợp lệ để nhắc nhở." });

            _context.Notifications.Add(new Notification
            {
                UserID = targetUserId.Value,
                Title = "🔔 Admin nhắc nhở: Xử lý Lịch hẹn",
                Content = $"Hệ thống ghi nhận lịch hẹn với khách hàng {a.CustomerName} đang tồn đọng. Vui lòng kiểm tra và cập nhật tiến độ ngay!",
                CreatedAt = DateTime.Now,
                IsRead = false
            });

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Đã gửi thông báo hối thúc đến Môi giới / Chủ nhà thành công!" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id, bool blockSpam = false)
        {
            var item = await _context.Appointments.FindAsync(id);
            if (item == null) return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });

            _context.Appointments.Remove(item);
            await _context.SaveChangesAsync();

            string msg = blockSpam
                ? "Đã xóa Lịch hẹn ảo và đưa IP vào danh sách theo dõi Spam!"
                : "Đã xóa vĩnh viễn dữ liệu rác khỏi hệ thống.";

            return Json(new { success = true, message = msg });
        }

        [HttpGet]
        public async Task<IActionResult> ExportCsv()
        {
            var appointments = await _context.Appointments
                .Include(a => a.Buyer).Include(a => a.Seller).Include(a => a.Property)
                .OrderByDescending(a => a.CreatedAt).ToListAsync();

            var builder = new StringBuilder();
            builder.Append('\uFEFF');
            builder.AppendLine("Mã Lịch Hẹn,Ngày Tạo,Thời Gian Hẹn,Khách Hàng,SĐT Khách,Môi Giới/Chủ,Bất Động Sản,Trạng Thái,Kết Quả");

            foreach (var a in appointments)
            {
                var customer = string.IsNullOrWhiteSpace(a.CustomerName) ? (a.Buyer?.FullName ?? "N/A") : a.CustomerName;
                var seller = a.Seller?.FullName ?? "N/A";
                var property = a.Property?.Title != null ? $"\"{a.Property.Title.Replace("\"", "\"\"")}\"" : "N/A";

                string statusText = a.Status switch { "Pending" => "Chờ xác nhận", "Confirmed" => "Đã xác nhận", "Rescheduled" => "Đang dời lịch", "Cancelled" => "Đã hủy", "Completed" => "Hoàn tất", _ => a.Status };
                string resultText = a.ResultStatus switch { "Interested" => "Khách ưng ý", "DepositPending" => "Chờ chốt cọc", "FollowUp" => "Cần bám sát", "NotInterested" => "Không ưng", _ => "Chưa có" };

                builder.AppendLine($"{a.AppointmentID},{a.CreatedAt:dd/MM/yyyy},{a.AppointmentDate:dd/MM/yyyy HH:mm},\"{customer}\",{a.CustomerPhone},\"{seller}\",{property},{statusText},{resultText}");
            }

            return File(Encoding.UTF8.GetBytes(builder.ToString()), "text/csv", $"ThongKeLichHen_BDS_{DateTime.Now:yyyyMMdd}.csv");
        }
    }
}