using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using BDSKhanhHoa.Services;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class AppointmentsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmailService _emailService;

        private static readonly HashSet<string> AllowedStatus = new(StringComparer.OrdinalIgnoreCase)
        {
            "Pending", "Confirmed", "Cancelled", "Completed", "Rescheduled", "NoShow"
        };

        private static readonly HashSet<string> AllowedResultStatus = new(StringComparer.OrdinalIgnoreCase)
        {
            "Interested", "NotInterested", "DepositPending", "FollowUp"
        };

        public AppointmentsController(ApplicationDbContext context, IEmailService emailService)
        {
            _context = context;
            _emailService = emailService;
        }

        private bool TryGetCurrentUserId(out int userId)
        {
            userId = 0;
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(userIdStr, out userId);
        }

        private IQueryable<Appointment> BuildBaseQuery(int userId)
        {
            return _context.Appointments
                .AsNoTracking()
                .Include(a => a.Property).ThenInclude(p => p.Project)
                .Include(a => a.Project)
                .Include(a => a.Buyer)
                .Include(a => a.Seller)
                .Include(a => a.Lead)
                .Where(a => a.BuyerID == userId || a.SellerID == userId);
        }

        [HttpGet]
        public async Task<IActionResult> Index(
            string? mode = null,
            string tab = "all",
            string statusFilter = "All",
            string? keyword = null,
            string dateRange = "all",
            int page = 1)
        {
            if (!TryGetCurrentUserId(out int userId))
                return RedirectToAction("Login", "Account");

            const int pageSize = 12;
            var appointmentMode = string.IsNullOrWhiteSpace(mode) ? "Public" : mode.Trim();
            bool isBusinessMode = appointmentMode.Equals("Business", StringComparison.OrdinalIgnoreCase);

            ViewBag.AppointmentMode = appointmentMode;
            ViewBag.CurrentUserId = userId;

            var baseQuery = BuildBaseQuery(userId);
            var today = DateTime.Now.Date;

            if (!string.IsNullOrWhiteSpace(statusFilter) && !statusFilter.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                baseQuery = baseQuery.Where(a => a.Status == statusFilter);
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                keyword = keyword.Trim();
                baseQuery = baseQuery.Where(a =>
                    (a.CustomerName != null && EF.Functions.Like(a.CustomerName, $"%{keyword}%")) ||
                    (a.CustomerPhone != null && EF.Functions.Like(a.CustomerPhone, $"%{keyword}%")) ||
                    (a.CustomerEmail != null && EF.Functions.Like(a.CustomerEmail, $"%{keyword}%")) ||
                    (a.Note != null && EF.Functions.Like(a.Note, $"%{keyword}%")) ||
                    (a.Property != null && a.Property.Title != null && EF.Functions.Like(a.Property.Title, $"%{keyword}%")) ||
                    (a.Project != null && a.Project.ProjectName != null && EF.Functions.Like(a.Project.ProjectName, $"%{keyword}%"))
                );
            }

            switch (dateRange?.Trim().ToLowerInvariant())
            {
                case "today":
                    baseQuery = baseQuery.Where(a => a.AppointmentDate >= today && a.AppointmentDate < today.AddDays(1));
                    break;
                case "week":
                    baseQuery = baseQuery.Where(a => a.AppointmentDate >= today.AddDays(-7));
                    break;
                case "month":
                    baseQuery = baseQuery.Where(a => a.AppointmentDate >= today.AddMonths(-1));
                    break;
            }

            if (tab.Equals("incoming", StringComparison.OrdinalIgnoreCase))
                baseQuery = baseQuery.Where(a => a.SellerID == userId);
            else if (tab.Equals("outgoing", StringComparison.OrdinalIgnoreCase))
                baseQuery = baseQuery.Where(a => a.BuyerID == userId);
            else if (tab.Equals("upcoming", StringComparison.OrdinalIgnoreCase))
                baseQuery = baseQuery.Where(a => a.Status == "Pending" || a.Status == "Confirmed" || a.Status == "Rescheduled");
            else if (tab.Equals("completed", StringComparison.OrdinalIgnoreCase))
                baseQuery = baseQuery.Where(a => a.Status == "Completed");
            else if (tab.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
                baseQuery = baseQuery.Where(a => a.Status == "Cancelled");

            var fullQuery = BuildBaseQuery(userId);
            ViewBag.TotalAppointments = await fullQuery.CountAsync();
            ViewBag.PendingAppointments = await fullQuery.CountAsync(a => a.Status == "Pending" || a.Status == "Rescheduled");
            ViewBag.ConfirmedAppointments = await fullQuery.CountAsync(a => a.Status == "Confirmed");
            ViewBag.CompletedAppointments = await fullQuery.CountAsync(a => a.Status == "Completed");
            ViewBag.CancelledAppointments = await fullQuery.CountAsync(a => a.Status == "Cancelled");
            ViewBag.TodayAppointments = await fullQuery.CountAsync(a => a.AppointmentDate >= today && a.AppointmentDate < today.AddDays(1));

            int totalItems = await baseQuery.CountAsync();
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
            if (totalPages < 1) totalPages = 1;
            if (page < 1) page = 1;
            if (page > totalPages) page = totalPages;

            var appointments = await baseQuery
                .OrderByDescending(a => a.AppointmentDate)
                .ThenByDescending(a => a.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.ActiveTab = tab;
            ViewBag.StatusFilter = statusFilter;
            ViewBag.Keyword = keyword;
            ViewBag.DateRange = dateRange;
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;

            return View(appointments);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            int? propertyId, int? projectId, int? leadId,
            string? customerName, string? customerPhone, string? customerEmail,
            DateTime appointmentDate, string appointmentTime,
            string? note, string? meetingLocation)
        {
            if (!TryGetCurrentUserId(out int userId))
                return Json(new { success = false, message = "Vui lòng đăng nhập để tạo lịch hẹn." });

            if (!TimeSpan.TryParse(appointmentTime, out var timeSpan))
                return Json(new { success = false, message = "Giờ hẹn không hợp lệ." });

            Property? property = null;
            Project? project = null;
            ProjectLead? lead = null;

            if (propertyId.HasValue && propertyId.Value > 0)
            {
                property = await _context.Properties.Include(p => p.Project).FirstOrDefaultAsync(p => p.PropertyID == propertyId.Value);
                if (property == null || property.IsDeleted == true)
                    return Json(new { success = false, message = "Bất động sản không tồn tại hoặc đã bị gỡ." });
                project ??= property.Project;
            }

            if (projectId.HasValue && projectId.Value > 0)
            {
                project ??= await _context.Projects.FirstOrDefaultAsync(p => p.ProjectID == projectId.Value && !p.IsDeleted);
                if (project == null) return Json(new { success = false, message = "Dự án không tồn tại." });
            }

            if (leadId.HasValue && leadId.Value > 0)
            {
                lead = await _context.ProjectLeads.Include(l => l.Project).FirstOrDefaultAsync(l => l.LeadID == leadId.Value);
                if (lead == null) return Json(new { success = false, message = "Không tìm thấy lead CRM." });
                project ??= lead.Project;
            }

            var fullAppointmentDate = appointmentDate.Date.Add(timeSpan);
            if (fullAppointmentDate <= DateTime.Now)
                return Json(new { success = false, message = "Thời gian hẹn phải ở trong tương lai." });

            var sellerId = property?.UserID ?? project?.OwnerUserID ?? lead?.Project?.OwnerUserID ?? userId;

            var seller = await _context.Users.FindAsync(sellerId);
            var buyer = await _context.Users.FindAsync(userId);

            var newAppointment = new Appointment
            {
                PropertyID = property?.PropertyID,
                ProjectID = project?.ProjectID,
                LeadID = lead?.LeadID,
                BuyerID = userId,
                SellerID = sellerId,
                CustomerName = string.IsNullOrWhiteSpace(customerName) ? null : customerName.Trim(),
                CustomerPhone = string.IsNullOrWhiteSpace(customerPhone) ? null : customerPhone.Trim(),
                CustomerEmail = string.IsNullOrWhiteSpace(customerEmail) ? null : customerEmail.Trim(),
                AppointmentDate = fullAppointmentDate,
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                MeetingLocation = string.IsNullOrWhiteSpace(meetingLocation) ? null : meetingLocation.Trim(),
                Status = "Pending",
                CreatedAt = DateTime.Now
            };

            _context.Appointments.Add(newAppointment);

            _context.Notifications.Add(new Notification
            {
                UserID = sellerId,
                Title = "Có lịch hẹn mới",
                Content = $"Khách hàng {newAppointment.CustomerName} vừa đặt lịch hẹn vào {fullAppointmentDate:dd/MM/yyyy HH:mm}.",
                ActionUrl = "/Appointments/Index",
                ActionText = "Xem lịch hẹn",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            if (seller != null && !string.IsNullOrWhiteSpace(seller.Email))
            {
                string subject = $"[BDS Khánh Hòa] Yêu cầu đặt lịch hẹn mới từ {newAppointment.CustomerName}";
                string htmlMessage = $"<h3>Bạn có một yêu cầu đặt lịch xem bất động sản mới!</h3>" +
                                     $"<p><strong>Khách hàng:</strong> {newAppointment.CustomerName}</p>" +
                                     $"<p><strong>Số điện thoại:</strong> {newAppointment.CustomerPhone}</p>" +
                                     $"<p><strong>Thời gian hẹn:</strong> {fullAppointmentDate:dd/MM/yyyy HH:mm}</p>" +
                                     $"<p>Vui lòng đăng nhập vào hệ thống để Xác nhận hoặc Dời lịch.</p>";
                await _emailService.SendEmailAsync(seller.Email, subject, htmlMessage);
            }

            return Json(new { success = true, message = "Đã gửi yêu cầu đặt lịch hẹn thành công. Đang chờ người bán xác nhận." });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SellerAccept(int id)
        {
            if (!TryGetCurrentUserId(out int userId)) return Json(new { success = false, message = "Vui lòng đăng nhập!" });

            var appointment = await _context.Appointments.Include(a => a.Buyer).FirstOrDefaultAsync(a => a.AppointmentID == id);
            if (appointment == null) return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });
            if (appointment.SellerID != userId) return Json(new { success = false, message = "Bạn không có quyền thao tác." });

            appointment.Status = "Confirmed";
            appointment.NegotiationNote = "Người bán đã xác nhận lịch hẹn này.";
            appointment.UpdatedAt = DateTime.Now;

            _context.Notifications.Add(new Notification
            {
                UserID = appointment.BuyerID,
                Title = "Lịch hẹn đã được xác nhận",
                Content = $"Lịch hẹn của bạn vào lúc {appointment.AppointmentDate:dd/MM/yyyy HH:mm} đã được người bán đồng ý.",
                ActionUrl = "/Appointments/Index",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            if (appointment.Buyer != null && !string.IsNullOrWhiteSpace(appointment.Buyer.Email))
            {
                await _emailService.SendEmailAsync(appointment.Buyer.Email, "[BDS Khánh Hòa] Lịch hẹn đã được xác nhận",
                    $"Lịch hẹn của bạn vào {appointment.AppointmentDate:dd/MM/yyyy HH:mm} đã được xác nhận thành công. Vui lòng đến đúng giờ!");
            }

            return Json(new { success = true, message = "Đã xác nhận lịch hẹn thành công." });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SellerReject(int id, string reason)
        {
            if (!TryGetCurrentUserId(out int userId)) return Json(new { success = false, message = "Vui lòng đăng nhập!" });

            var appointment = await _context.Appointments.Include(a => a.Buyer).FirstOrDefaultAsync(a => a.AppointmentID == id);
            if (appointment == null) return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });
            if (appointment.SellerID != userId) return Json(new { success = false, message = "Bạn không có quyền thao tác." });

            appointment.Status = "Cancelled";
            appointment.NegotiationNote = $"Người bán từ chối / hủy lịch: {reason}";
            appointment.UpdatedAt = DateTime.Now;

            _context.Notifications.Add(new Notification
            {
                UserID = appointment.BuyerID,
                Title = "Lịch hẹn bị từ chối",
                Content = $"Lịch hẹn vào {appointment.AppointmentDate:dd/MM/yyyy HH:mm} đã bị từ chối. Lý do: {reason}",
                ActionUrl = "/Appointments/Index",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            if (appointment.Buyer != null && !string.IsNullOrWhiteSpace(appointment.Buyer.Email))
            {
                await _emailService.SendEmailAsync(appointment.Buyer.Email, "[BDS Khánh Hòa] Lịch hẹn đã bị từ chối",
                    $"Người bán đã từ chối lịch hẹn của bạn. Lý do: {reason}");
            }

            return Json(new { success = true, message = "Đã từ chối và hủy lịch hẹn." });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SellerReschedule(int id, DateTime proposedDate, string proposedTime, string reason)
        {
            if (!TryGetCurrentUserId(out int userId)) return Json(new { success = false, message = "Vui lòng đăng nhập!" });

            if (!TimeSpan.TryParse(proposedTime, out var timeSpan))
                return Json(new { success = false, message = "Giờ hẹn không hợp lệ." });

            var appointment = await _context.Appointments.Include(a => a.Buyer).FirstOrDefaultAsync(a => a.AppointmentID == id);
            if (appointment == null) return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });
            if (appointment.SellerID != userId) return Json(new { success = false, message = "Bạn không có quyền thao tác." });

            var fullProposedDate = proposedDate.Date.Add(timeSpan);
            if (fullProposedDate <= DateTime.Now)
                return Json(new { success = false, message = "Thời gian dời lịch phải ở trong tương lai." });

            appointment.Status = "Rescheduled";
            appointment.ProposedAppointmentDate = fullProposedDate;
            appointment.NegotiationNote = $"Người bán đề xuất dời lịch: {reason}";
            appointment.UpdatedAt = DateTime.Now;

            _context.Notifications.Add(new Notification
            {
                UserID = appointment.BuyerID,
                Title = "Người bán yêu cầu dời lịch hẹn",
                Content = $"Người bán muốn dời lịch sang {fullProposedDate:dd/MM/yyyy HH:mm}. Vui lòng kiểm tra và xác nhận.",
                ActionUrl = "/Appointments/Index",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            if (appointment.Buyer != null && !string.IsNullOrWhiteSpace(appointment.Buyer.Email))
            {
                await _emailService.SendEmailAsync(appointment.Buyer.Email, "[BDS Khánh Hòa] Đề xuất dời lịch hẹn",
                    $"Người bán không thể gặp vào giờ cũ và đã đề xuất dời lịch sang <strong>{fullProposedDate:dd/MM/yyyy HH:mm}</strong>.<br/>Lý do: {reason}.<br/>Vui lòng đăng nhập vào hệ thống để đồng ý hoặc hủy bỏ.");
            }

            return Json(new { success = true, message = "Đã gửi đề xuất dời lịch cho người mua." });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BuyerAcceptReschedule(int id)
        {
            if (!TryGetCurrentUserId(out int userId)) return Json(new { success = false, message = "Vui lòng đăng nhập!" });

            var appointment = await _context.Appointments.Include(a => a.Seller).FirstOrDefaultAsync(a => a.AppointmentID == id);
            if (appointment == null) return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });
            if (appointment.BuyerID != userId) return Json(new { success = false, message = "Bạn không có quyền thao tác." });
            if (appointment.Status != "Rescheduled" || !appointment.ProposedAppointmentDate.HasValue)
                return Json(new { success = false, message = "Lịch hẹn không trong trạng thái chờ xác nhận dời lịch." });

            appointment.AppointmentDate = appointment.ProposedAppointmentDate.Value;
            appointment.ProposedAppointmentDate = null;
            appointment.Status = "Confirmed";
            appointment.NegotiationNote = "Người mua đã đồng ý thời gian dời lịch mới.";
            appointment.UpdatedAt = DateTime.Now;

            _context.Notifications.Add(new Notification
            {
                UserID = appointment.SellerID,
                Title = "Khách hàng đồng ý dời lịch",
                Content = $"Khách hàng đã đồng ý dời lịch sang {appointment.AppointmentDate:dd/MM/yyyy HH:mm}.",
                ActionUrl = "/Appointments/Index",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            if (appointment.Seller != null && !string.IsNullOrWhiteSpace(appointment.Seller.Email))
            {
                await _emailService.SendEmailAsync(appointment.Seller.Email, "[BDS Khánh Hòa] Khách hàng đồng ý dời lịch",
                    $"Khách hàng {appointment.CustomerName} đã ĐỒNG Ý dời lịch hẹn sang {appointment.AppointmentDate:dd/MM/yyyy HH:mm}.");
            }

            return Json(new { success = true, message = "Bạn đã xác nhận đồng ý dời lịch hẹn." });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BuyerRejectReschedule(int id, string reason)
        {
            if (!TryGetCurrentUserId(out int userId)) return Json(new { success = false, message = "Vui lòng đăng nhập!" });

            var appointment = await _context.Appointments.Include(a => a.Seller).FirstOrDefaultAsync(a => a.AppointmentID == id);
            if (appointment == null) return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });
            if (appointment.BuyerID != userId) return Json(new { success = false, message = "Bạn không có quyền thao tác." });

            appointment.Status = "Cancelled";
            appointment.NegotiationNote = $"Người mua hủy lịch. Lý do: {reason}";
            appointment.UpdatedAt = DateTime.Now;

            _context.Notifications.Add(new Notification
            {
                UserID = appointment.SellerID,
                Title = "Khách hàng hủy lịch",
                Content = $"Khách hàng đã hủy cuộc hẹn. Lý do: {reason}",
                ActionUrl = "/Appointments/Index",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            if (appointment.Seller != null && !string.IsNullOrWhiteSpace(appointment.Seller.Email))
            {
                await _emailService.SendEmailAsync(appointment.Seller.Email, "[BDS Khánh Hòa] Khách hàng hủy lịch hẹn",
                    $"Khách hàng {appointment.CustomerName} đã hủy lịch hẹn. Lý do: {reason}");
            }

            return Json(new { success = true, message = "Bạn đã hủy lịch hẹn thành công." });
        }

        // Tìm hàm UpdateOutcome trong file Controllers/AppointmentsController.cs của bạn và dán đè nội dung này:
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateOutcome([FromForm] int id, [FromForm] string resultStatus, [FromForm] string? resultNote)
        {
            if (!TryGetCurrentUserId(out int userId)) return Json(new { success = false, message = "Vui lòng đăng nhập!" });

            if (!AllowedResultStatus.Contains(resultStatus ?? string.Empty))
                return Json(new { success = false, message = "Kết quả phản hồi không hợp lệ." });

            var appointment = await _context.Appointments.FirstOrDefaultAsync(a => a.AppointmentID == id);
            if (appointment == null) return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });

            if (appointment.SellerID != userId && appointment.BuyerID != userId)
                return Json(new { success = false, message = "Bạn không có quyền cập nhật kết quả." });

            // CHỐT CHẶN BẢO MẬT: Đã đóng thì miễn sửa
            if (appointment.Status == "Completed" || appointment.Status == "Cancelled")
                return Json(new { success = false, message = "Lịch hẹn này đã bị khóa, không thể thay đổi kết quả!" });

            appointment.ResultStatus = resultStatus.Trim();
            appointment.ResultNote = string.IsNullOrWhiteSpace(resultNote) ? null : resultNote.Trim();
            appointment.UpdatedAt = DateTime.Now;

            if (!string.Equals(appointment.ResultStatus, "FollowUp", StringComparison.OrdinalIgnoreCase))
            {
                appointment.Status = "Completed";
                appointment.CompletedAt = DateTime.Now;
            }

            _context.Appointments.Update(appointment);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Đã lưu kết quả sau buổi xem thực tế." });
        }
    }
}