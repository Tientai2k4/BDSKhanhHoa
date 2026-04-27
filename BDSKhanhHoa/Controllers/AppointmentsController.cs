using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class AppointmentsController : Controller
    {
        private readonly ApplicationDbContext _context;

        private static readonly HashSet<string> AllowedStatus = new(StringComparer.OrdinalIgnoreCase)
        {
            "Pending",
            "Confirmed",
            "Cancelled",
            "Completed",
            "Rescheduled",
            "NoShow"
        };

        private static readonly HashSet<string> AllowedResultStatus = new(StringComparer.OrdinalIgnoreCase)
        {
            "Interested",
            "NotInterested",
            "DepositPending",
            "FollowUp"
        };

        public AppointmentsController(ApplicationDbContext context)
        {
            _context = context;
        }

        private bool TryGetCurrentUserId(out int userId)
        {
            userId = 0;
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(userIdStr, out userId);
        }

        private IQueryable<Appointment> BuildBusinessQuery(int userId)
        {
            return _context.Appointments
                .AsNoTracking()
                .Include(a => a.Property)
                    .ThenInclude(p => p.Project)
                .Include(a => a.Project)
                .Include(a => a.Buyer)
                .Include(a => a.Seller)
                .Include(a => a.Lead)
                .Where(a => a.BuyerID == userId || a.SellerID == userId);
        }

        private IQueryable<Appointment> BuildPublicQuery(int userId)
        {
            return _context.Appointments
                .AsNoTracking()
                .Include(a => a.Property)
                    .ThenInclude(p => p.Project)
                .Include(a => a.Project)
                .Include(a => a.Buyer)
                .Include(a => a.Seller)
                .Include(a => a.Lead)
                .Where(a => a.BuyerID == userId);
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

            var baseQuery = isBusinessMode
                ? BuildBusinessQuery(userId)
                : BuildPublicQuery(userId);

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
                    (a.AssignedStaffName != null && EF.Functions.Like(a.AssignedStaffName, $"%{keyword}%")) ||
                    (a.AssignedStaffPhone != null && EF.Functions.Like(a.AssignedStaffPhone, $"%{keyword}%")) ||
                    (a.Note != null && EF.Functions.Like(a.Note, $"%{keyword}%")) ||
                    (a.ResultNote != null && EF.Functions.Like(a.ResultNote, $"%{keyword}%")) ||
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

            if (isBusinessMode)
            {
                if (tab.Equals("incoming", StringComparison.OrdinalIgnoreCase))
                    baseQuery = baseQuery.Where(a => a.SellerID == userId);
                else if (tab.Equals("outgoing", StringComparison.OrdinalIgnoreCase))
                    baseQuery = baseQuery.Where(a => a.BuyerID == userId);
                else if (tab.Equals("upcoming", StringComparison.OrdinalIgnoreCase))
                    baseQuery = baseQuery.Where(a => a.Status == "Pending" || a.Status == "Confirmed");
                else if (tab.Equals("completed", StringComparison.OrdinalIgnoreCase))
                    baseQuery = baseQuery.Where(a => a.Status == "Completed");
                else if (tab.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
                    baseQuery = baseQuery.Where(a => a.Status == "Cancelled");
            }

            ViewBag.TotalAppointments = await baseQuery.CountAsync();
            ViewBag.PendingAppointments = await baseQuery.CountAsync(a => a.Status == "Pending");
            ViewBag.ConfirmedAppointments = await baseQuery.CountAsync(a => a.Status == "Confirmed");
            ViewBag.CompletedAppointments = await baseQuery.CountAsync(a => a.Status == "Completed");
            ViewBag.CancelledAppointments = await baseQuery.CountAsync(a => a.Status == "Cancelled");
            ViewBag.TodayAppointments = await baseQuery.CountAsync(a => a.AppointmentDate >= today && a.AppointmentDate < today.AddDays(1));

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
            int? propertyId,
            int? projectId,
            int? leadId,
            string? customerName,
            string? customerPhone,
            string? customerEmail,
            DateTime appointmentDate,
            TimeSpan appointmentTime,
            string? note,
            string? staffName,
            string? staffPhone,
            string? meetingLocation,
            string? mode = null)
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return Json(new { success = false, message = "Vui lòng đăng nhập để tạo lịch hẹn." });
            }

            Property? property = null;
            Project? project = null;
            ProjectLead? lead = null;

            if (propertyId.HasValue && propertyId.Value > 0)
            {
                property = await _context.Properties
                    .Include(p => p.Project)
                    .FirstOrDefaultAsync(p => p.PropertyID == propertyId.Value);

                if (property == null || property.IsDeleted == true)
                {
                    return Json(new { success = false, message = "Bất động sản không tồn tại hoặc đã bị gỡ." });
                }

                project ??= property.Project;
                if (project == null && property.ProjectID.HasValue)
                {
                    project = await _context.Projects.FirstOrDefaultAsync(p => p.ProjectID == property.ProjectID.Value && !p.IsDeleted);
                }
            }

            if (projectId.HasValue && projectId.Value > 0)
            {
                project ??= await _context.Projects.FirstOrDefaultAsync(p => p.ProjectID == projectId.Value && !p.IsDeleted);

                if (project == null)
                {
                    return Json(new { success = false, message = "Dự án không tồn tại hoặc đã bị gỡ." });
                }
            }

            if (leadId.HasValue && leadId.Value > 0)
            {
                lead = await _context.ProjectLeads
                    .Include(l => l.Project)
                    .FirstOrDefaultAsync(l => l.LeadID == leadId.Value);

                if (lead == null)
                {
                    return Json(new { success = false, message = "Không tìm thấy lead CRM." });
                }

                if (lead.HandledByUserID != userId)
                {
                    return Json(new { success = false, message = "Bạn không có quyền tạo lịch cho lead này." });
                }

                project ??= lead.Project;
                if (project == null && lead.ProjectID > 0)
                {
                    project = await _context.Projects.FirstOrDefaultAsync(p => p.ProjectID == lead.ProjectID && !p.IsDeleted);
                }

                customerName ??= lead.Name;
                customerPhone ??= lead.Phone;
                customerEmail ??= lead.Email;
            }

            if (property == null && project == null)
            {
                return Json(new { success = false, message = "Bạn cần chọn ít nhất một ngữ cảnh: dự án hoặc bất động sản." });
            }

            DateTime fullAppointmentDate = appointmentDate.Date.Add(appointmentTime);
            if (fullAppointmentDate <= DateTime.Now)
            {
                return Json(new { success = false, message = "Thời gian hẹn phải ở trong tương lai." });
            }

            var minTime = fullAppointmentDate.AddHours(-2);
            var maxTime = fullAppointmentDate.AddHours(2);

            IQueryable<Appointment> overlapQuery = _context.Appointments.AsNoTracking()
                .Where(a => a.Status != "Cancelled" && a.AppointmentDate > minTime && a.AppointmentDate < maxTime);

            if (property != null)
            {
                overlapQuery = overlapQuery.Where(a => a.PropertyID == property.PropertyID);
            }
            else if (project != null)
            {
                overlapQuery = overlapQuery.Where(a => a.ProjectID == project.ProjectID);
            }

            bool isOverlapping = await overlapQuery.AnyAsync();
            if (isOverlapping)
            {
                return Json(new { success = false, message = "Khung giờ này đã có lịch. Vui lòng chọn thời gian khác." });
            }

            var sellerId = property?.UserID
                           ?? project?.OwnerUserID
                           ?? lead?.HandledByUserID
                           ?? userId;

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
                AssignedStaffName = string.IsNullOrWhiteSpace(staffName) ? null : staffName.Trim(),
                AssignedStaffPhone = string.IsNullOrWhiteSpace(staffPhone) ? null : staffPhone.Trim(),
                MeetingLocation = string.IsNullOrWhiteSpace(meetingLocation) ? null : meetingLocation.Trim(),
                Status = "Pending",
                CreatedAt = DateTime.Now
            };

            _context.Appointments.Add(newAppointment);

            if (lead != null && string.Equals(lead.LeadStatus, "New", StringComparison.OrdinalIgnoreCase))
            {
                lead.LeadStatus = "Contacted";
                _context.ProjectLeads.Update(lead);
            }

            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                message = "Đã tạo lịch hẹn thành công."
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStatus([FromForm] int id, [FromForm] string status)
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return Json(new { success = false, message = "Vui lòng đăng nhập!" });
            }

            if (!AllowedStatus.Contains(status ?? string.Empty))
            {
                return Json(new { success = false, message = "Trạng thái không hợp lệ." });
            }

            var appointment = await _context.Appointments.FirstOrDefaultAsync(a => a.AppointmentID == id);
            if (appointment == null)
            {
                return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });
            }

            if (appointment.BuyerID != userId && appointment.SellerID != userId)
            {
                return Json(new { success = false, message = "Truy cập bị từ chối." });
            }

            if (appointment.Status == "Cancelled" || appointment.Status == "Completed")
            {
                return Json(new { success = false, message = "Lịch hẹn đã đóng, không thể thay đổi." });
            }

            if (appointment.BuyerID == userId && status != "Cancelled")
            {
                return Json(new { success = false, message = "Bạn chỉ có quyền hủy lịch hẹn của mình." });
            }

            appointment.Status = status.Trim();
            appointment.UpdatedAt = DateTime.Now;

            if (appointment.Status == "Completed")
            {
                appointment.CompletedAt = DateTime.Now;
            }

            _context.Appointments.Update(appointment);
            await _context.SaveChangesAsync();

            string statusMsg = status switch
            {
                "Confirmed" => "đã xác nhận",
                "Cancelled" => "đã hủy",
                "Completed" => "đã hoàn tất",
                "Rescheduled" => "đã dời lịch",
                "NoShow" => "đã đánh dấu vắng mặt",
                _ => "cập nhật"
            };

            return Json(new { success = true, message = $"Lịch hẹn {statusMsg} thành công." });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateOutcome([FromForm] int id, [FromForm] string resultStatus, [FromForm] string? resultNote)
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return Json(new { success = false, message = "Vui lòng đăng nhập!" });
            }

            if (!AllowedResultStatus.Contains(resultStatus ?? string.Empty))
            {
                return Json(new { success = false, message = "Kết quả phản hồi không hợp lệ." });
            }

            var appointment = await _context.Appointments.FirstOrDefaultAsync(a => a.AppointmentID == id);
            if (appointment == null)
            {
                return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });
            }

            if (appointment.SellerID != userId && appointment.BuyerID != userId)
            {
                return Json(new { success = false, message = "Bạn không có quyền cập nhật kết quả." });
            }

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

            return Json(new
            {
                success = true,
                message = "Đã lưu kết quả sau buổi xem thực tế."
            });
        }
    }
}