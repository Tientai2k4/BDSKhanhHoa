using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class MemberProjectController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<MemberProjectController> _logger;

        private static readonly HashSet<string> AllowedLeadStatuses = new(StringComparer.OrdinalIgnoreCase)
        {
            "New",
            "Contacted",
            "Resolved",
            "Invalid"
        };

        private static readonly HashSet<string> ClosedLeadStatuses = new(StringComparer.OrdinalIgnoreCase)
        {
            "Resolved",
            "Invalid",
            "Đã chốt",
            "Không hợp lệ"
        };

        private static readonly HashSet<string> ActiveAppointmentStatuses = new(StringComparer.OrdinalIgnoreCase)
        {
            "Pending",
            "Confirmed",
            "Rescheduled",
            "Chờ xác nhận",
            "Đã xác nhận",
            "Đang dời lịch"
        };

        public MemberProjectController(ApplicationDbContext context, ILogger<MemberProjectController> logger)
        {
            _context = context;
            _logger = logger;
        }

        // =====================================================
        // TIỆN ÍCH CHUNG
        // =====================================================
        private bool TryGetCurrentUserId(out int userId)
        {
            userId = 0;

            string? userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);

            return int.TryParse(userIdStr, out userId);
        }

        private IActionResult SafeRedirect(string? returnUrl)
        {
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return RedirectToAction(nameof(Index));
        }

        private static bool IsClosedLead(string? status)
        {
            return !string.IsNullOrWhiteSpace(status) && ClosedLeadStatuses.Contains(status.Trim());
        }

        private static string NormalizeLeadStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return "New";
            }

            return status.Trim() switch
            {
                "Mới" => "New",
                "Khách mới" => "New",
                "Đã liên hệ" => "Contacted",
                "Đang chăm sóc" => "Contacted",
                "Đã chốt" => "Resolved",
                "Đã chốt thành công" => "Resolved",
                "Không hợp lệ" => "Invalid",
                "Hủy" => "Invalid",
                "Huỷ" => "Invalid",
                _ => status.Trim()
            };
        }

        private static string LeadStatusText(string? status)
        {
            return NormalizeLeadStatus(status) switch
            {
                "New" => "Khách mới",
                "Contacted" => "Đang chăm sóc",
                "Resolved" => "Đã chốt",
                "Invalid" => "Không hợp lệ",
                _ => "Chưa xác định"
            };
        }

        private static bool IsValidPhone(string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone))
            {
                return false;
            }

            phone = phone.Trim();

            return Regex.IsMatch(phone, @"^(0|\+84)[0-9\s\.\-]{8,15}$");
        }

        private static bool IsValidEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return true;
            }

            return Regex.IsMatch(email.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
        }

        private static string CleanText(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static string BuildNoteLine(string title, string? content)
        {
            string cleanContent = string.IsNullOrWhiteSpace(content)
                ? "Không có ghi chú chi tiết."
                : content.Trim();

            return $"[{DateTime.Now:dd/MM/yyyy HH:mm}] {title}: {cleanContent}";
        }

        private static void AppendLeadNote(ProjectLead lead, string title, string? content)
        {
            string line = BuildNoteLine(title, content);

            if (string.IsNullOrWhiteSpace(lead.Note))
            {
                lead.Note = line;
            }
            else
            {
                lead.Note = line + Environment.NewLine + lead.Note;
            }
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

        private async Task<ProjectLead?> GetOwnedLeadAsync(int leadId, int userId, bool tracking = true)
        {
            IQueryable<ProjectLead> query = _context.ProjectLeads
                .Include(l => l.Project)
                    .ThenInclude(p => p.Ward)
                        .ThenInclude(w => w.Area);

            if (!tracking)
            {
                query = query.AsNoTracking();
            }

            return await query.FirstOrDefaultAsync(l =>
                l.LeadID == leadId &&
                l.Project != null &&
                l.Project.OwnerUserID == userId &&
                !l.Project.IsDeleted);
        }

        private async Task LoadDashboardCountsAsync(int userId)
        {
            var myProjectsQuery = _context.Projects
                .AsNoTracking()
                .Where(p => p.OwnerUserID == userId && !p.IsDeleted);

            var myLeadsQuery = _context.ProjectLeads
                .AsNoTracking()
                .Include(l => l.Project)
                .Where(l => l.Project != null && l.Project.OwnerUserID == userId && !l.Project.IsDeleted);

            var myAppointmentsQuery = _context.Appointments
                .AsNoTracking()
                .Where(a => a.SellerID == userId || a.BuyerID == userId);

            DateTime today = DateTime.Now.Date;

            ViewBag.TotalProjects = await myProjectsQuery.CountAsync();
            ViewBag.TotalLeads = await myLeadsQuery.CountAsync();

            ViewBag.NewLeads = await myLeadsQuery.CountAsync(l => l.LeadStatus == "New" || l.LeadStatus == "Mới");
            ViewBag.ContactedLeads = await myLeadsQuery.CountAsync(l => l.LeadStatus == "Contacted" || l.LeadStatus == "Đã liên hệ");
            ViewBag.ResolvedLeads = await myLeadsQuery.CountAsync(l => l.LeadStatus == "Resolved" || l.LeadStatus == "Đã chốt");
            ViewBag.InvalidLeads = await myLeadsQuery.CountAsync(l => l.LeadStatus == "Invalid" || l.LeadStatus == "Không hợp lệ");

            ViewBag.TotalAppointments = await myAppointmentsQuery.CountAsync();
            ViewBag.PendingAppointments = await myAppointmentsQuery.CountAsync(a => a.Status == "Pending" || a.Status == "Chờ xác nhận");
            ViewBag.CompletedAppointments = await myAppointmentsQuery.CountAsync(a => a.Status == "Completed" || a.Status == "Đã hoàn tất");

            ViewBag.TodayAppointments = await myAppointmentsQuery.CountAsync(a =>
                a.AppointmentDate >= today &&
                a.AppointmentDate < today.AddDays(1));
        }

        private static bool IsValidStatusTransition(string currentStatus, string newStatus, out string message)
        {
            currentStatus = NormalizeLeadStatus(currentStatus);
            newStatus = NormalizeLeadStatus(newStatus);

            message = string.Empty;

            if (currentStatus == "Resolved" || currentStatus == "Invalid")
            {
                message = "Hồ sơ đã đóng nên không thể thay đổi trạng thái.";
                return false;
            }

            if (currentStatus == "Contacted" && newStatus == "New")
            {
                message = "Khách đã được chăm sóc nên không được lùi về trạng thái Khách mới.";
                return false;
            }

            if (currentStatus == "New")
            {
                return newStatus == "New" ||
                       newStatus == "Contacted" ||
                       newStatus == "Resolved" ||
                       newStatus == "Invalid";
            }

            if (currentStatus == "Contacted")
            {
                return newStatus == "Contacted" ||
                       newStatus == "Resolved" ||
                       newStatus == "Invalid";
            }

            message = "Luồng trạng thái không hợp lệ.";
            return false;
        }

        // =====================================================
        // PHỄU KHÁCH HÀNG
        // =====================================================
        [HttpGet]
        public async Task<IActionResult> Index(
            int? projectId,
            string? status,
            string? daterange,
            string? keyword,
            int page = 1)
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return Challenge();
            }

            if (!await CanAccessBusinessPortalAsync(userId))
            {
                TempData["Error"] = "Tài khoản của bạn chưa được cấp quyền quản lý dự án.";
                return RedirectToAction("Index", "Home");
            }

            const int pageSize = 12;

            await LoadDashboardCountsAsync(userId);

            var myProjectsList = await _context.Projects
                .AsNoTracking()
                .Where(p => p.OwnerUserID == userId && !p.IsDeleted)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            ViewBag.ProjectList = new SelectList(myProjectsList, "ProjectID", "ProjectName", projectId);

            var query = _context.ProjectLeads
                .AsNoTracking()
                .Include(l => l.Project)
                .Where(l =>
                    l.Project != null &&
                    l.Project.OwnerUserID == userId &&
                    !l.Project.IsDeleted)
                .AsQueryable();

            if (projectId.HasValue && projectId.Value > 0)
            {
                query = query.Where(l => l.ProjectID == projectId.Value);
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                string normalizedStatus = NormalizeLeadStatus(status);

                if (AllowedLeadStatuses.Contains(normalizedStatus))
                {
                    query = query.Where(l => l.LeadStatus == normalizedStatus);
                }
            }

            if (!string.IsNullOrWhiteSpace(daterange))
            {
                DateTime today = DateTime.Now.Date;

                switch (daterange.Trim().ToLowerInvariant())
                {
                    case "today":
                        query = query.Where(l => l.CreatedAt >= today && l.CreatedAt < today.AddDays(1));
                        break;

                    case "week":
                        query = query.Where(l => l.CreatedAt >= today.AddDays(-7));
                        break;

                    case "month":
                        query = query.Where(l => l.CreatedAt >= today.AddMonths(-1));
                        break;

                    case "quarter":
                        query = query.Where(l => l.CreatedAt >= today.AddMonths(-3));
                        break;
                }
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                keyword = keyword.Trim();

                query = query.Where(l =>
                    (l.Name != null && EF.Functions.Like(l.Name, $"%{keyword}%")) ||
                    (l.Phone != null && EF.Functions.Like(l.Phone, $"%{keyword}%")) ||
                    (l.Email != null && EF.Functions.Like(l.Email, $"%{keyword}%")) ||
                    (l.Project != null && l.Project.ProjectName != null && EF.Functions.Like(l.Project.ProjectName, $"%{keyword}%")));
            }

            int totalItems = await query.CountAsync();
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

            page = Math.Clamp(page, 1, Math.Max(1, totalPages));

            var leads = await query
                .OrderByDescending(l => l.LeadStatus == "New")
                .ThenByDescending(l => l.CreatedAt)
                .ThenByDescending(l => l.LeadID)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.CurrentProjectId = projectId;
            ViewBag.CurrentStatus = status;
            ViewBag.CurrentDateRange = daterange;
            ViewBag.CurrentKeyword = keyword;
            ViewBag.TotalFilteredItems = totalItems;

            return View(leads);
        }

        // =====================================================
        // CHI TIẾT CHĂM SÓC KHÁCH
        // =====================================================
        [HttpGet]
        public async Task<IActionResult> EditLead(int id, string? returnUrl = null)
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return Challenge();
            }

            var lead = await GetOwnedLeadAsync(id, userId);

            if (lead == null)
            {
                TempData["Error"] = "Không tìm thấy hồ sơ khách hàng hoặc bạn không có quyền truy cập.";
                return RedirectToAction(nameof(Index));
            }

            var activeAppointments = await _context.Appointments
                .AsNoTracking()
                .Where(a =>
                    a.LeadID == id &&
                    ActiveAppointmentStatuses.Contains(a.Status ?? ""))
                .OrderBy(a => a.AppointmentDate)
                .ToListAsync();

            ViewBag.ActiveAppointments = activeAppointments;
            ViewBag.ReturnUrl = string.IsNullOrWhiteSpace(returnUrl) ? Url.Action(nameof(Index)) : returnUrl;

            return View(lead);
        }

        [HttpGet]
        public async Task<IActionResult> LeadHistory(int id)
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return Challenge();
            }

            var lead = await GetOwnedLeadAsync(id, userId, tracking: false);

            if (lead == null)
            {
                TempData["Error"] = "Không tìm thấy hồ sơ khách hàng hoặc bạn không có quyền xem.";
                return RedirectToAction(nameof(Index));
            }

            var historyAppointments = await _context.Appointments
                .AsNoTracking()
                .Where(a => a.LeadID == id)
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync();

            ViewBag.HistoryAppointments = historyAppointments;

            return View(lead);
        }

        // =====================================================
        // CẬP NHẬT TRẠNG THÁI LEAD
        // =====================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateLeadStatus(
            int id,
            string status,
            string? note,
            string? returnUrl)
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return Challenge();
            }

            string newStatus = NormalizeLeadStatus(status);

            if (!AllowedLeadStatuses.Contains(newStatus))
            {
                TempData["Error"] = "Trạng thái khách hàng không hợp lệ.";
                return SafeRedirect(returnUrl);
            }

            try
            {
                var lead = await GetOwnedLeadAsync(id, userId);

                if (lead == null)
                {
                    TempData["Error"] = "Không tìm thấy hồ sơ khách hàng hoặc bạn không có quyền cập nhật.";
                    return SafeRedirect(returnUrl);
                }

                string currentStatus = NormalizeLeadStatus(lead.LeadStatus);

                if (!IsValidStatusTransition(currentStatus, newStatus, out string transitionMessage))
                {
                    TempData["Error"] = transitionMessage;
                    return SafeRedirect(returnUrl);
                }

                note = CleanText(note);

                if ((newStatus == "Resolved" || newStatus == "Invalid") && note.Length < 10)
                {
                    TempData["Error"] = "Khi chốt hoặc hủy hồ sơ, vui lòng nhập ghi chú tối thiểu 10 ký tự để lưu vết CRM.";
                    return SafeRedirect(returnUrl);
                }

                if (newStatus == "Contacted" && note.Length < 5)
                {
                    TempData["Error"] = "Khi chuyển sang Đang chăm sóc, vui lòng nhập ghi chú ngắn về nội dung đã trao đổi.";
                    return SafeRedirect(returnUrl);
                }

                string oldText = LeadStatusText(currentStatus);
                string newText = LeadStatusText(newStatus);

                if (!string.Equals(currentStatus, newStatus, StringComparison.OrdinalIgnoreCase))
                {
                    AppendLeadNote(lead, "Đổi trạng thái CRM", $"Từ \"{oldText}\" sang \"{newText}\". {note}");
                }
                else if (!string.IsNullOrWhiteSpace(note))
                {
                    AppendLeadNote(lead, "Cập nhật ghi chú chăm sóc", note);
                }

                lead.LeadStatus = newStatus;

                await _context.SaveChangesAsync();

                TempData["Success"] = $"Đã cập nhật hồ sơ khách hàng {lead.Name} sang trạng thái \"{newText}\".";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi cập nhật trạng thái lead {LeadId}", id);
                TempData["Error"] = "Hệ thống đang bận, vui lòng thử lại sau.";
            }

            return SafeRedirect(returnUrl);
        }

        // =====================================================
        // GHI NHANH ĐÃ GỌI
        // =====================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> QuickCall(
            int id,
            string? note,
            string? returnUrl)
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return Challenge();
            }

            try
            {
                var lead = await GetOwnedLeadAsync(id, userId);

                if (lead == null)
                {
                    TempData["Error"] = "Không tìm thấy hồ sơ khách hàng hoặc bạn không có quyền thao tác.";
                    return SafeRedirect(returnUrl);
                }

                if (IsClosedLead(lead.LeadStatus))
                {
                    TempData["Error"] = "Hồ sơ đã đóng nên không thể ghi thêm cuộc gọi.";
                    return SafeRedirect(returnUrl);
                }

                note = CleanText(note);

                if (note.Length < 5)
                {
                    TempData["Error"] = "Vui lòng nhập ghi chú cuộc gọi tối thiểu 5 ký tự.";
                    return SafeRedirect(returnUrl);
                }

                if (NormalizeLeadStatus(lead.LeadStatus) == "New")
                {
                    lead.LeadStatus = "Contacted";
                }

                AppendLeadNote(lead, "Đã gọi khách hàng", note);

                await _context.SaveChangesAsync();

                TempData["Success"] = $"Đã ghi nhận cuộc gọi với khách hàng {lead.Name}.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi ghi nhanh cuộc gọi lead {LeadId}", id);
                TempData["Error"] = "Không thể ghi nhận cuộc gọi lúc này.";
            }

            return SafeRedirect(returnUrl);
        }

        // =====================================================
        // ĐIỀU PHỐI LỊCH XEM DỰ ÁN
        // =====================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ScheduleSiteVisit(
            int leadId,
            DateTime appointmentDate,
            string appointmentTime,
            string assignedStaffName,
            string? assignedStaffPhone,
            string? assignedStaffEmail,
            string? note,
            string? returnUrl)
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return Challenge();
            }

            assignedStaffName = CleanText(assignedStaffName);
            assignedStaffPhone = CleanText(assignedStaffPhone);
            assignedStaffEmail = CleanText(assignedStaffEmail);
            note = CleanText(note);

            if (string.IsNullOrWhiteSpace(assignedStaffName))
            {
                TempData["Error"] = "Vui lòng nhập tên nhân viên phụ trách lịch xem.";
                return SafeRedirect(returnUrl);
            }

            if (!string.IsNullOrWhiteSpace(assignedStaffPhone) && !IsValidPhone(assignedStaffPhone))
            {
                TempData["Error"] = "Số điện thoại nhân viên phụ trách không hợp lệ.";
                return SafeRedirect(returnUrl);
            }

            if (!IsValidEmail(assignedStaffEmail))
            {
                TempData["Error"] = "Email nhân viên phụ trách không hợp lệ.";
                return SafeRedirect(returnUrl);
            }

            if (!TimeSpan.TryParse(appointmentTime, out TimeSpan timeSpan))
            {
                TempData["Error"] = "Giờ hẹn không hợp lệ.";
                return SafeRedirect(returnUrl);
            }

            DateTime fullAppointmentDate = appointmentDate.Date.Add(timeSpan);

            if (fullAppointmentDate <= DateTime.Now.AddMinutes(30))
            {
                TempData["Error"] = "Thời gian hẹn phải lớn hơn thời điểm hiện tại ít nhất 30 phút.";
                return SafeRedirect(returnUrl);
            }

            if (fullAppointmentDate.Hour < 7 || fullAppointmentDate.Hour > 21)
            {
                TempData["Error"] = "Giờ hẹn nên nằm trong khung 07:00 - 21:00 để thuận tiện chăm sóc khách.";
                return SafeRedirect(returnUrl);
            }

            try
            {
                var lead = await GetOwnedLeadAsync(leadId, userId);

                if (lead == null)
                {
                    TempData["Error"] = "Không tìm thấy khách hàng hoặc bạn không có quyền tạo lịch.";
                    return SafeRedirect(returnUrl);
                }

                if (IsClosedLead(lead.LeadStatus))
                {
                    TempData["Error"] = "Hồ sơ đã đóng. Không thể tạo lịch hẹn mới cho khách hàng này.";
                    return SafeRedirect(returnUrl);
                }

                bool hasActiveAppointment = await _context.Appointments
                    .AsNoTracking()
                    .AnyAsync(a =>
                        a.LeadID == leadId &&
                        ActiveAppointmentStatuses.Contains(a.Status ?? "") &&
                        a.AppointmentDate >= DateTime.Now);

                if (hasActiveAppointment)
                {
                    TempData["Error"] = "Khách hàng này đã có lịch hẹn đang mở. Vui lòng xử lý lịch hiện tại trước khi tạo lịch mới.";
                    return SafeRedirect(returnUrl);
                }

                bool staffBusyAtSameTime = false;

                if (!string.IsNullOrWhiteSpace(assignedStaffPhone))
                {
                    staffBusyAtSameTime = await _context.Appointments
                        .AsNoTracking()
                        .AnyAsync(a =>
                            a.SellerID == userId &&
                            a.AssignedStaffPhone == assignedStaffPhone &&
                            ActiveAppointmentStatuses.Contains(a.Status ?? "") &&
                            a.AppointmentDate >= fullAppointmentDate.AddMinutes(-45) &&
                            a.AppointmentDate <= fullAppointmentDate.AddMinutes(45));
                }

                if (staffBusyAtSameTime)
                {
                    TempData["Error"] = "Nhân viên này đang có lịch gần khung giờ đã chọn. Vui lòng chọn giờ khác hoặc đổi nhân viên.";
                    return SafeRedirect(returnUrl);
                }

                string meetingLocation = lead.Project?.AddressDetail;

                if (string.IsNullOrWhiteSpace(meetingLocation))
                {
                    meetingLocation = $"{lead.Project?.Ward?.WardName}, {lead.Project?.Ward?.Area?.AreaName}";
                }

                var appointment = new Appointment
                {
                    ProjectID = lead.ProjectID,
                    LeadID = lead.LeadID,
                    BuyerID = userId,
                    SellerID = lead.Project!.OwnerUserID,
                    CustomerName = lead.Name,
                    CustomerPhone = lead.Phone,
                    CustomerEmail = lead.Email,
                    AppointmentDate = fullAppointmentDate,
                    MeetingLocation = meetingLocation,
                    AssignedStaffName = assignedStaffName,
                    AssignedStaffPhone = string.IsNullOrWhiteSpace(assignedStaffPhone) ? null : assignedStaffPhone,
                    Note = string.IsNullOrWhiteSpace(note) ? null : note,
                    Status = "Pending",
                    CreatedAt = DateTime.Now
                };

                _context.Appointments.Add(appointment);

                if (NormalizeLeadStatus(lead.LeadStatus) == "New")
                {
                    lead.LeadStatus = "Contacted";
                }

                AppendLeadNote(
                    lead,
                    "Tạo lịch xem dự án",
                    $"Lịch hẹn lúc {fullAppointmentDate:HH:mm dd/MM/yyyy}. Nhân viên phụ trách: {assignedStaffName}. {note}");

                await _context.SaveChangesAsync();

                if (!string.IsNullOrWhiteSpace(assignedStaffEmail))
                {
                    _logger.LogInformation(
                        "Simulate email to {Email}. Staff: {Staff}. Lead: {Lead}. Appointment: {Appointment}",
                        assignedStaffEmail,
                        assignedStaffName,
                        lead.Name,
                        fullAppointmentDate);
                }

                TempData["Success"] = $"Đã tạo lịch xem dự án cho khách {lead.Name} vào {fullAppointmentDate:HH:mm dd/MM/yyyy}.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi tạo lịch hẹn cho lead {LeadId}", leadId);
                TempData["Error"] = "Có lỗi xảy ra khi tạo lịch hẹn. Vui lòng thử lại.";
            }

            return SafeRedirect(returnUrl);
        }

        // =====================================================
        // XUẤT FILE CSV
        // =====================================================
        [HttpGet]
        public async Task<IActionResult> ExportLeadsToCsv(int? projectId, string? status, string? keyword)
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return Challenge();
            }

            var query = _context.ProjectLeads
                .AsNoTracking()
                .Include(l => l.Project)
                .Where(l =>
                    l.Project != null &&
                    l.Project.OwnerUserID == userId &&
                    !l.Project.IsDeleted);

            if (projectId.HasValue && projectId.Value > 0)
            {
                query = query.Where(l => l.ProjectID == projectId.Value);
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                string normalizedStatus = NormalizeLeadStatus(status);

                if (AllowedLeadStatuses.Contains(normalizedStatus))
                {
                    query = query.Where(l => l.LeadStatus == normalizedStatus);
                }
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                keyword = keyword.Trim();

                query = query.Where(l =>
                    (l.Name != null && EF.Functions.Like(l.Name, $"%{keyword}%")) ||
                    (l.Phone != null && EF.Functions.Like(l.Phone, $"%{keyword}%")) ||
                    (l.Email != null && EF.Functions.Like(l.Email, $"%{keyword}%")) ||
                    (l.Project != null && l.Project.ProjectName != null && EF.Functions.Like(l.Project.ProjectName, $"%{keyword}%")));
            }

            var leads = await query
                .OrderByDescending(l => l.CreatedAt)
                .ToListAsync();

            var builder = new StringBuilder();

            builder.Append("\uFEFF");
            builder.AppendLine("ID,Họ tên,Số điện thoại,Email,Dự án,Trạng thái,Ngày tạo,Tin nhắn,Ghi chú");

            foreach (var l in leads)
            {
                builder.AppendLine(string.Join(",",
                    CsvText(l.LeadID.ToString()),
                    CsvText(l.Name),
                    CsvText(l.Phone),
                    CsvText(l.Email),
                    CsvText(l.Project?.ProjectName),
                    CsvText(LeadStatusText(l.LeadStatus)),
                    CsvText(l.CreatedAt.ToString("dd/MM/yyyy HH:mm")),
                    CsvText(l.Message),
                    CsvText(l.Note)));
            }

            byte[] result = Encoding.UTF8.GetBytes(builder.ToString());

            return File(result, "text/csv", $"KhachHangDuAn_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        }

        private static string CsvText(string? value)
        {
            value ??= "";
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        // =====================================================
        // DỰ ÁN CỦA TÔI
        // View thật nằm tại: Views/MemberProject/MyProjects.cshtml
        // =====================================================
        [HttpGet]
        [Route("MemberProject/MyProjects")]
        [Route("MyProjects")]
        [Route("MyProjects/Index")]
        public async Task<IActionResult> MyProjects()
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return Challenge();
            }

            if (!await CanAccessBusinessPortalAsync(userId))
            {
                TempData["Error"] = "Tài khoản của bạn chưa được cấp quyền quản lý dự án.";
                return RedirectToAction("Index", "Home");
            }

            await LoadDashboardCountsAsync(userId);

            var projects = await _context.Projects
                .AsNoTracking()
                .Include(p => p.Ward)
                    .ThenInclude(w => w.Area)
                .Include(p => p.Area)
                .Where(p => p.OwnerUserID == userId && !p.IsDeleted)
                .OrderByDescending(p =>
                    p.ApprovalStatus == "Approved" ||
                    p.ApprovalStatus == "Đã duyệt")
                .ThenByDescending(p => p.CreatedAt)
                .ToListAsync();

            var leadCounts = await _context.ProjectLeads
                .AsNoTracking()
                .Include(l => l.Project)
                .Where(l =>
                    l.Project != null &&
                    l.Project.OwnerUserID == userId &&
                    !l.Project.IsDeleted)
                .GroupBy(l => l.ProjectID)
                .Select(g => new
                {
                    ProjectID = g.Key,
                    Count = g.Count()
                })
                .ToDictionaryAsync(x => x.ProjectID, x => x.Count);

            ViewBag.LeadCounts = leadCounts;

            return View("MyProjects", projects);
        }
    }
}