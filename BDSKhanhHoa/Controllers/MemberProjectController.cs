using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class MemberProjectController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<MemberProjectController> _logger;

        // Quản lý 4 trạng thái phễu khách hàng (Bao gồm cả Invalid - Lead Rác)
        private static readonly HashSet<string> AllowedLeadStatuses = new(StringComparer.OrdinalIgnoreCase)
        {
            "New", "Contacted", "Resolved", "Invalid"
        };

        public MemberProjectController(ApplicationDbContext context, ILogger<MemberProjectController> logger)
        {
            _context = context;
            _logger = logger;
        }

        private bool TryGetCurrentUserId(out int userId)
        {
            userId = 0;
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(userIdStr, out userId);
        }

        private async Task LoadDashboardCountsAsync(int userId)
        {
            var myProjectsQuery = _context.Projects.AsNoTracking().Where(p => p.OwnerUserID == userId && !p.IsDeleted);
            var myLeadsQuery = _context.ProjectLeads.AsNoTracking().Include(l => l.Project).Where(l => l.Project != null && l.Project.OwnerUserID == userId);
            var myAppointmentsQuery = _context.Appointments.AsNoTracking().Where(a => a.SellerID == userId || a.BuyerID == userId);

            ViewBag.TotalProjects = await myProjectsQuery.CountAsync();
            ViewBag.TotalLeads = await myLeadsQuery.CountAsync();
            ViewBag.NewLeads = await myLeadsQuery.CountAsync(l => l.LeadStatus == "New");
            ViewBag.ContactedLeads = await myLeadsQuery.CountAsync(l => l.LeadStatus == "Contacted");
            ViewBag.ResolvedLeads = await myLeadsQuery.CountAsync(l => l.LeadStatus == "Resolved");
            ViewBag.InvalidLeads = await myLeadsQuery.CountAsync(l => l.LeadStatus == "Invalid");

            ViewBag.TotalAppointments = await myAppointmentsQuery.CountAsync();
            ViewBag.PendingAppointments = await myAppointmentsQuery.CountAsync(a => a.Status == "Pending");
            ViewBag.CompletedAppointments = await myAppointmentsQuery.CountAsync(a => a.Status == "Completed");

            var today = DateTime.Now.Date;
            ViewBag.TodayAppointments = await myAppointmentsQuery.CountAsync(a => a.AppointmentDate >= today && a.AppointmentDate < today.AddDays(1));
        }

        [HttpGet]
        public async Task<IActionResult> Index(int? projectId, string? status, string? daterange, string? keyword, int page = 1)
        {
            if (!TryGetCurrentUserId(out int userId)) return Challenge();

            const int pageSize = 12;
            await LoadDashboardCountsAsync(userId);

            var myProjectsList = await _context.Projects
                .AsNoTracking()
                .Where(p => p.OwnerUserID == userId && !p.IsDeleted)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            ViewBag.ProjectList = new SelectList(myProjectsList, "ProjectID", "ProjectName", projectId);

            // Bảo mật: Chỉ lấy Lead thuộc Dự án mà User này làm Chủ
            var query = _context.ProjectLeads
                .AsNoTracking()
                .Include(l => l.Project)
                .Where(l => l.Project != null && l.Project.OwnerUserID == userId)
                .AsQueryable();

            if (projectId.HasValue && projectId.Value > 0) query = query.Where(l => l.ProjectID == projectId.Value);
            if (!string.IsNullOrWhiteSpace(status) && AllowedLeadStatuses.Contains(status)) query = query.Where(l => l.LeadStatus == status);

            if (!string.IsNullOrWhiteSpace(daterange))
            {
                var today = DateTime.Now.Date;
                switch (daterange.Trim().ToLowerInvariant())
                {
                    case "today": query = query.Where(l => l.CreatedAt >= today && l.CreatedAt < today.AddDays(1)); break;
                    case "week": query = query.Where(l => l.CreatedAt >= today.AddDays(-7)); break;
                    case "month": query = query.Where(l => l.CreatedAt >= today.AddMonths(-1)); break;
                }
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                keyword = keyword.Trim();
                query = query.Where(l =>
                    (l.Name != null && EF.Functions.Like(l.Name, $"%{keyword}%")) ||
                    (l.Phone != null && EF.Functions.Like(l.Phone, $"%{keyword}%")) ||
                    (l.Project != null && l.Project.ProjectName != null && EF.Functions.Like(l.Project.ProjectName, $"%{keyword}%"))
                );
            }

            int totalItems = await query.CountAsync();
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
            page = Math.Clamp(page, 1, Math.Max(1, totalPages));

            var leads = await query
                .OrderByDescending(l => l.CreatedAt)
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

            return View(leads);
        }

        [HttpGet]
        public async Task<IActionResult> EditLead(int id, string? returnUrl = null)
        {
            if (!TryGetCurrentUserId(out int userId)) return Challenge();

            var lead = await _context.ProjectLeads
                .Include(l => l.Project).ThenInclude(p => p.Ward).ThenInclude(w => w.Area)
                .FirstOrDefaultAsync(l => l.LeadID == id);

            if (lead == null)
            {
                TempData["Error"] = "Không tìm thấy hồ sơ khách hàng.";
                return RedirectToAction(nameof(Index));
            }

            if (lead.Project?.OwnerUserID != userId)
            {
                _logger.LogWarning($"User {userId} attempted to access unauthorized Lead {id}");
                TempData["Error"] = "Bạn không có quyền truy cập hồ sơ khách hàng này.";
                return RedirectToAction(nameof(Index));
            }

            ViewBag.ReturnUrl = string.IsNullOrWhiteSpace(returnUrl) ? Url.Action(nameof(Index)) : returnUrl;
            return View(lead);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateLeadStatus(int id, string status, string? note, string? returnUrl)
        {
            if (!TryGetCurrentUserId(out int userId)) return Challenge();

            if (!AllowedLeadStatuses.Contains(status ?? string.Empty))
            {
                TempData["Error"] = "Trạng thái không hợp lệ.";
                return SafeRedirect(returnUrl);
            }

            try
            {
                var lead = await _context.ProjectLeads.Include(l => l.Project).FirstOrDefaultAsync(l => l.LeadID == id);
                if (lead == null || lead.Project?.OwnerUserID != userId)
                {
                    TempData["Error"] = "Lỗi bảo mật hoặc không tìm thấy dữ liệu.";
                    return SafeRedirect(returnUrl);
                }

                // QUY TRÌNH NGHIỆP VỤ: ĐÓNG BĂNG HỒ SƠ. Nếu đã chốt hoặc hủy thì không cho sửa nữa.
                if (lead.LeadStatus == "Resolved" || lead.LeadStatus == "Invalid")
                {
                    TempData["Error"] = "Hồ sơ này đã ĐÓNG (Đã chốt hoặc Hủy). Không thể thay đổi dữ liệu để bảo vệ tính toàn vẹn.";
                    return SafeRedirect(returnUrl);
                }

                // Ghi nhận lịch sử Note cũ để lưu vết CRM
                if (!string.IsNullOrWhiteSpace(note) && lead.Note != note)
                {
                    lead.Note = $"[{DateTime.Now.ToString("dd/MM/yyyy HH:mm")}] {note}\n{lead.Note}";
                }

                lead.LeadStatus = status.Trim();
                _context.ProjectLeads.Update(lead);
                await _context.SaveChangesAsync();

                TempData["Success"] = $"Đã cập nhật và lưu vết chăm sóc khách hàng {lead.Name} thành công.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi cập nhật trạng thái Lead");
                TempData["Error"] = "Hệ thống đang bận, vui lòng thử lại sau.";
            }

            return SafeRedirect(returnUrl);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ScheduleSiteVisit(
            int leadId,
            DateTime appointmentDate,
            string appointmentTime,
            string assignedStaffName,
            string assignedStaffEmail,
            string note,
            string returnUrl)
        {
            if (!TryGetCurrentUserId(out int userId)) return Challenge();

            if (!TimeSpan.TryParse(appointmentTime, out var timeSpan))
            {
                TempData["Error"] = "Giờ hẹn không hợp lệ.";
                return SafeRedirect(returnUrl);
            }

            try
            {
                var lead = await _context.ProjectLeads.Include(l => l.Project).FirstOrDefaultAsync(l => l.LeadID == leadId);
                if (lead == null || lead.Project?.OwnerUserID != userId)
                {
                    TempData["Error"] = "Không tìm thấy khách hàng hoặc lỗi bảo mật.";
                    return SafeRedirect(returnUrl);
                }

                // QUY TRÌNH NGHIỆP VỤ: ĐÓNG BĂNG HỒ SƠ
                if (lead.LeadStatus == "Resolved" || lead.LeadStatus == "Invalid")
                {
                    TempData["Error"] = "Hồ sơ đã ĐÓNG. Không thể khởi tạo lịch hẹn mới cho khách hàng này.";
                    return SafeRedirect(returnUrl);
                }

                var fullAppointmentDate = appointmentDate.Date.Add(timeSpan);
                if (fullAppointmentDate <= DateTime.Now)
                {
                    TempData["Error"] = "Thời gian hẹn phải ở trong tương lai.";
                    return SafeRedirect(returnUrl);
                }

                // 1. Lưu Appointment mới vào DB
                var newAppointment = new Appointment
                {
                    ProjectID = lead.ProjectID,
                    LeadID = lead.LeadID,
                    BuyerID = userId,
                    SellerID = lead.Project.OwnerUserID,
                    CustomerName = lead.Name,
                    CustomerPhone = lead.Phone,
                    CustomerEmail = lead.Email,
                    AppointmentDate = fullAppointmentDate,
                    Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                    AssignedStaffName = assignedStaffName.Trim(),
                    Status = "Pending",
                    CreatedAt = DateTime.Now
                };

                _context.Appointments.Add(newAppointment);

                // Cập nhật trạng thái Lead sang "Contacted" nếu đang là "New"
                if (lead.LeadStatus == "New")
                {
                    lead.LeadStatus = "Contacted";
                    _context.ProjectLeads.Update(lead);
                }

                await _context.SaveChangesAsync();

                // 2. MÔ PHỎNG GỬI EMAIL ĐIỀU PHỐI (Cho báo cáo đồ án)
                if (!string.IsNullOrWhiteSpace(assignedStaffEmail))
                {
                    string emailSubject = $"[BDS KHÁNH HÒA] Điều phối lịch hẹn dự án {lead.Project.ProjectName}";
                    string emailBody = $"Chào {assignedStaffName},\n\nBạn được phân công đón khách hàng {lead.Name} (SĐT: {lead.Phone}) vào lúc {appointmentDate:dd/MM/yyyy} {appointmentTime}.\n\nGhi chú: {note}\n\nVui lòng chuẩn bị chu đáo.";

                    _logger.LogInformation("=== SIMULATING EMAIL SENDING ===");
                    _logger.LogInformation($"To: {assignedStaffEmail}");
                    _logger.LogInformation($"Subject: {emailSubject}");
                    _logger.LogInformation($"Body: {emailBody}");
                    _logger.LogInformation("================================");
                }

                TempData["Success"] = "Đã điều phối lịch hẹn và gửi Email thông báo công việc cho nhân viên thành công.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tạo lịch hẹn từ CRM");
                TempData["Error"] = "Có lỗi xảy ra khi tạo lịch hẹn.";
            }

            return SafeRedirect(returnUrl);
        }

        [HttpGet]
        public async Task<IActionResult> ExportLeadsToCsv(int? projectId)
        {
            if (!TryGetCurrentUserId(out int userId)) return Challenge();

            var query = _context.ProjectLeads
                .Include(l => l.Project)
                .Where(l => l.Project != null && l.Project.OwnerUserID == userId);

            if (projectId.HasValue && projectId.Value > 0)
            {
                query = query.Where(l => l.ProjectID == projectId.Value);
            }

            var leads = await query.OrderByDescending(l => l.CreatedAt).ToListAsync();

            var builder = new StringBuilder();
            builder.AppendLine("ID,HoTen,SoDienThoai,Email,DuAn,TrangThai,NgayTao,GhiChu");

            foreach (var l in leads)
            {
                var projectName = l.Project?.ProjectName?.Replace(",", " ") ?? "N/A";
                var note = l.Note?.Replace("\n", " ").Replace(",", " ") ?? "";
                builder.AppendLine($"{l.LeadID},{l.Name},{l.Phone},{l.Email},{projectName},{l.LeadStatus},{l.CreatedAt:dd/MM/yyyy HH:mm},{note}");
            }

            var result = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray();
            return File(result, "text/csv", $"Data_KhachHang_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        }

        [HttpGet]
        public async Task<IActionResult> MyProjects()
        {
            if (!TryGetCurrentUserId(out int userId)) return Challenge();
            await LoadDashboardCountsAsync(userId);

            var projects = await _context.Projects
                .AsNoTracking()
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Where(p => p.OwnerUserID == userId && !p.IsDeleted)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            var leadCounts = await _context.ProjectLeads
                .AsNoTracking()
                .Include(l => l.Project)
                .Where(l => l.Project != null && l.Project.OwnerUserID == userId)
                .GroupBy(l => l.ProjectID)
                .Select(g => new { ProjectID = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ProjectID, x => x.Count);

            ViewBag.LeadCounts = leadCounts;
            return View(projects);
        }

        [HttpGet]
        public async Task<IActionResult> LeadHistory(int id)
        {
            if (!TryGetCurrentUserId(out int userId)) return Challenge();

            var lead = await _context.ProjectLeads
                .Include(l => l.Project).ThenInclude(p => p.Ward).ThenInclude(w => w.Area)
                .FirstOrDefaultAsync(l => l.LeadID == id);

            if (lead == null)
            {
                TempData["Error"] = "Không tìm thấy hồ sơ khách hàng.";
                return RedirectToAction(nameof(Index));
            }

            if (lead.Project?.OwnerUserID != userId)
            {
                TempData["Error"] = "Bạn không có quyền xem hồ sơ khách hàng này.";
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

        private IActionResult SafeRedirect(string? returnUrl)
        {
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
            return RedirectToAction(nameof(Index));
        }
    }
}