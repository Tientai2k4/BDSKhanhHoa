using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class AnalyticsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public AnalyticsController(ApplicationDbContext context)
        {
            _context = context;
        }

        public class ClosedDealReportRow
        {
            public int ProjectID { get; set; }
            public int? PropertyID { get; set; }
            public int? LeadID { get; set; }
            public int? AppointmentID { get; set; }
            public string ProjectName { get; set; } = "Chưa rõ dự án";
            public string PropertyName { get; set; } = "Theo dự án";
            public string CustomerName { get; set; } = "Khách hàng";
            public string CustomerPhone { get; set; } = "";
            public string SourceType { get; set; } = "CRM";
            public string StatusText { get; set; } = "Đã chốt";
            public DateTime? ClosedAt { get; set; }
            public string Note { get; set; } = "";
        }

        public class HotProjectReportRow
        {
            public int ProjectID { get; set; }
            public string ProjectName { get; set; } = "Chưa rõ dự án";
            public string LocationName { get; set; } = "Chưa có khu vực";
            public int Leads { get; set; }
            public int Closed { get; set; }
            public int Appointments { get; set; }
            public int Views { get; set; }
            public decimal ConversionRate { get; set; }
        }

        private bool TryGetCurrentUserId(out int userId)
        {
            userId = 0;
            string? userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(userIdStr, out userId);
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

        private static string NormalizeLeadStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status)) return "Mới";

            return status.Trim() switch
            {
                "New" => "Mới",
                "Contacted" => "Đã liên hệ",
                "Resolved" => "Đã chốt",
                "Invalid" => "Không hợp lệ",
                "Mới" => "Mới",
                "Khách mới" => "Mới",
                "Đã liên hệ" => "Đã liên hệ",
                "Đang chăm sóc" => "Đã liên hệ",
                "Đã chốt" => "Đã chốt",
                "Đã chốt thành công" => "Đã chốt",
                "Không hợp lệ" => "Không hợp lệ",
                "Hủy" => "Không hợp lệ",
                "Huỷ" => "Không hợp lệ",
                _ => status.Trim()
            };
        }

        private static string NormalizeAppointmentStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status)) return "Chờ xác nhận";

            return status.Trim() switch
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
                _ => status.Trim()
            };
        }

        private static string NormalizeAppointmentResult(string? resultStatus)
        {
            if (string.IsNullOrWhiteSpace(resultStatus)) return "";

            return resultStatus.Trim() switch
            {
                "Interested" => "Khách quan tâm",
                "NotInterested" => "Khách không quan tâm",
                "DepositPending" => "Chờ đặt cọc",
                "FollowUp" => "Cần chăm sóc thêm",
                "Khách quan tâm" => "Khách quan tâm",
                "Khách không quan tâm" => "Khách không quan tâm",
                "Chờ đặt cọc" => "Chờ đặt cọc",
                "Cần chăm sóc thêm" => "Cần chăm sóc thêm",
                _ => ""
            };
        }

        private static string NormalizeSupportStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status)) return "Chờ xử lý";

            return status.Trim() switch
            {
                "Pending" => "Chờ xử lý",
                "Processing" => "Đang xử lý",
                "Resolved" => "Đã xử lý",
                "Closed" => "Đã đóng",
                "Chưa xử lý" => "Chờ xử lý",
                "Chờ xử lý" => "Chờ xử lý",
                "Đang xử lý" => "Đang xử lý",
                "Đã xử lý" => "Đã xử lý",
                "Đã đóng" => "Đã đóng",
                _ => status.Trim()
            };
        }

        private static bool IsClosedAppointmentDeal(Appointment appointment)
        {
            string status = NormalizeAppointmentStatus(appointment.Status);
            string result = NormalizeAppointmentResult(appointment.ResultStatus);

            return status == "Đã hoàn tất" && result == "Chờ đặt cọc";
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return RedirectToAction("Login", "Account");
            }

            if (!await CanAccessBusinessPortalAsync(userId))
            {
                TempData["Error"] = "Tài khoản của bạn chưa được cấp quyền quản lý dự án.";
                return RedirectToAction("Index", "Home");
            }

            var businessProfile = await _context.BusinessProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.UserID == userId);

            var projects = await _context.Projects
                .AsNoTracking()
                .Include(p => p.Area)
                .Include(p => p.Ward)
                .Where(p => p.OwnerUserID == userId && !p.IsDeleted)
                .OrderByDescending(p => p.Views)
                .ThenByDescending(p => p.CreatedAt)
                .ToListAsync();

            var projectIds = projects.Select(p => p.ProjectID).ToList();

            var leads = projectIds.Any()
                ? await _context.ProjectLeads
                    .AsNoTracking()
                    .Include(l => l.Project)
                    .Where(l => projectIds.Contains(l.ProjectID))
                    .ToListAsync()
                : new List<ProjectLead>();

            var appointments = projectIds.Any()
                ? await _context.Appointments
                    .AsNoTracking()
                    .Include(a => a.Property)
                    .Include(a => a.Project)
                    .Include(a => a.Lead).ThenInclude(l => l.Project)
                    .Where(a =>
                        (a.ProjectID.HasValue && projectIds.Contains(a.ProjectID.Value)) ||
                        (a.Property != null && a.Property.ProjectID.HasValue && projectIds.Contains(a.Property.ProjectID.Value)) ||
                        (a.Lead != null && projectIds.Contains(a.Lead.ProjectID)))
                    .ToListAsync()
                : new List<Appointment>();

            var propertiesLinkedToProjects = projectIds.Any()
                ? await _context.Properties
                    .AsNoTracking()
                    .Where(p =>
                        p.ProjectID.HasValue &&
                        projectIds.Contains(p.ProjectID.Value) &&
                        p.IsDeleted != true)
                    .ToListAsync()
                : new List<Property>();

            var supportTickets = projectIds.Any()
                ? await _context.ContactMessages
                    .AsNoTracking()
                    .Where(x =>
                        x.UserID == userId &&
                        x.ProjectID.HasValue &&
                        projectIds.Contains(x.ProjectID.Value))
                    .ToListAsync()
                : new List<ContactMessage>();

            int newLeads = leads.Count(l => NormalizeLeadStatus(l.LeadStatus) == "Mới");
            int contactedLeads = leads.Count(l => NormalizeLeadStatus(l.LeadStatus) == "Đã liên hệ");
            int resolvedLeads = leads.Count(l => NormalizeLeadStatus(l.LeadStatus) == "Đã chốt");
            int invalidLeads = leads.Count(l => NormalizeLeadStatus(l.LeadStatus) == "Không hợp lệ");

            int pendingAppointments = appointments.Count(a =>
                NormalizeAppointmentStatus(a.Status) == "Chờ xác nhận" ||
                NormalizeAppointmentStatus(a.Status) == "Đang dời lịch");

            int confirmedAppointments = appointments.Count(a => NormalizeAppointmentStatus(a.Status) == "Đã xác nhận");
            int completedAppointments = appointments.Count(a => NormalizeAppointmentStatus(a.Status) == "Đã hoàn tất");

            int cancelledAppointments = appointments.Count(a =>
                NormalizeAppointmentStatus(a.Status) == "Đã hủy" ||
                NormalizeAppointmentStatus(a.Status) == "Khách không đến");

            int depositPendingAppointments = appointments.Count(IsClosedAppointmentDeal);

            int openSupportTickets = supportTickets.Count(t =>
                NormalizeSupportStatus(t.Status) == "Chờ xử lý" ||
                NormalizeSupportStatus(t.Status) == "Đang xử lý");

            int closedSupportTickets = supportTickets.Count(t =>
                NormalizeSupportStatus(t.Status) == "Đã xử lý" ||
                NormalizeSupportStatus(t.Status) == "Đã đóng");

            var leadMap = leads.GroupBy(x => x.ProjectID).ToDictionary(g => g.Key, g => g.Count());
            var newLeadMap = leads.Where(x => NormalizeLeadStatus(x.LeadStatus) == "Mới").GroupBy(x => x.ProjectID).ToDictionary(g => g.Key, g => g.Count());
            var contactedLeadMap = leads.Where(x => NormalizeLeadStatus(x.LeadStatus) == "Đã liên hệ").GroupBy(x => x.ProjectID).ToDictionary(g => g.Key, g => g.Count());
            var resolvedLeadMap = leads.Where(x => NormalizeLeadStatus(x.LeadStatus) == "Đã chốt").GroupBy(x => x.ProjectID).ToDictionary(g => g.Key, g => g.Count());
            var invalidLeadMap = leads.Where(x => NormalizeLeadStatus(x.LeadStatus) == "Không hợp lệ").GroupBy(x => x.ProjectID).ToDictionary(g => g.Key, g => g.Count());

            var appointmentMap = appointments
                .Select(a => new { Appointment = a, ProjectID = a.ProjectID ?? a.Property?.ProjectID ?? a.Lead?.ProjectID })
                .Where(x => x.ProjectID.HasValue)
                .GroupBy(x => x.ProjectID!.Value)
                .ToDictionary(g => g.Key, g => g.Count());

            var completedAppointmentMap = appointments
                .Where(x => NormalizeAppointmentStatus(x.Status) == "Đã hoàn tất")
                .Select(a => new { Appointment = a, ProjectID = a.ProjectID ?? a.Property?.ProjectID ?? a.Lead?.ProjectID })
                .Where(x => x.ProjectID.HasValue)
                .GroupBy(x => x.ProjectID!.Value)
                .ToDictionary(g => g.Key, g => g.Count());

            var depositPendingAppointmentMap = appointments
                .Where(IsClosedAppointmentDeal)
                .Select(a => new { Appointment = a, ProjectID = a.ProjectID ?? a.Property?.ProjectID ?? a.Lead?.ProjectID })
                .Where(x => x.ProjectID.HasValue)
                .GroupBy(x => x.ProjectID!.Value)
                .ToDictionary(g => g.Key, g => g.Count());

            var propertyViewMap = propertiesLinkedToProjects
                .Where(x => x.ProjectID.HasValue)
                .GroupBy(x => x.ProjectID!.Value)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Views ?? 0));

            var totalViewMap = projects.ToDictionary(
                p => p.ProjectID,
                p => p.Views + (propertyViewMap.ContainsKey(p.ProjectID) ? propertyViewMap[p.ProjectID] : 0));

            var propertyCountMap = propertiesLinkedToProjects
                .Where(x => x.ProjectID.HasValue)
                .GroupBy(x => x.ProjectID!.Value)
                .ToDictionary(g => g.Key, g => g.Count());

            var supportMap = supportTickets
                .Where(x => x.ProjectID.HasValue)
                .GroupBy(x => x.ProjectID!.Value)
                .ToDictionary(g => g.Key, g => g.Count());

            var closedDealRows = new List<ClosedDealReportRow>();

            closedDealRows.AddRange(
                appointments
                    .Where(IsClosedAppointmentDeal)
                    .OrderByDescending(a => a.CompletedAt ?? a.UpdatedAt ?? a.AppointmentDate)
                    .Select(a =>
                    {
                        int pid = a.ProjectID ?? a.Property?.ProjectID ?? a.Lead?.ProjectID ?? 0;
                        return new ClosedDealReportRow
                        {
                            ProjectID = pid,
                            PropertyID = a.PropertyID,
                            LeadID = a.LeadID,
                            AppointmentID = a.AppointmentID,
                            ProjectName = a.Project?.ProjectName ?? a.Property?.Project?.ProjectName ?? a.Lead?.Project?.ProjectName ?? "Chưa rõ dự án",
                            PropertyName = a.Property?.Title ?? "Theo dự án",
                            CustomerName = !string.IsNullOrWhiteSpace(a.CustomerName) ? a.CustomerName! : (a.Lead?.Name ?? "Khách hàng"),
                            CustomerPhone = !string.IsNullOrWhiteSpace(a.CustomerPhone) ? a.CustomerPhone! : (a.Lead?.Phone ?? ""),
                            SourceType = "Lịch hẹn",
                            StatusText = "Chờ đặt cọc",
                            ClosedAt = a.CompletedAt ?? a.UpdatedAt ?? a.AppointmentDate,
                            Note = a.ResultNote ?? a.NegotiationNote ?? ""
                        };
                    }));

            closedDealRows.AddRange(
                leads
                    .Where(l => NormalizeLeadStatus(l.LeadStatus) == "Đã chốt")
                    .OrderByDescending(l => l.CreatedAt)
                    .Select(l => new ClosedDealReportRow
                    {
                        ProjectID = l.ProjectID,
                        LeadID = l.LeadID,
                        ProjectName = l.Project?.ProjectName ?? "Chưa rõ dự án",
                        PropertyName = "Theo dự án",
                        CustomerName = !string.IsNullOrWhiteSpace(l.Name) ? l.Name! : "Khách hàng",
                        CustomerPhone = l.Phone ?? "",
                        SourceType = "CRM",
                        StatusText = "Đã chốt",
                        ClosedAt = l.CreatedAt,
                        Note = l.Note ?? l.Message ?? ""
                    }));

            closedDealRows = closedDealRows
                .OrderByDescending(x => x.ClosedAt ?? DateTime.MinValue)
                .Take(12)
                .ToList();

            int totalProjectViews = projects.Sum(p => p.Views);
            int totalPropertyViews = propertiesLinkedToProjects.Sum(p => p.Views ?? 0);
            int totalViews = totalProjectViews + totalPropertyViews;
            int totalClosedDeals = resolvedLeads + depositPendingAppointments;
            int totalInteractions = leads.Count + appointments.Count + supportTickets.Count;

            var hotProjects = projects
                .Select(p =>
                {
                    int pLead = leadMap.ContainsKey(p.ProjectID) ? leadMap[p.ProjectID] : 0;
                    int pClosed = (resolvedLeadMap.ContainsKey(p.ProjectID) ? resolvedLeadMap[p.ProjectID] : 0)
                                + (depositPendingAppointmentMap.ContainsKey(p.ProjectID) ? depositPendingAppointmentMap[p.ProjectID] : 0);
                    int pAppointments = appointmentMap.ContainsKey(p.ProjectID) ? appointmentMap[p.ProjectID] : 0;
                    int pViews = totalViewMap.ContainsKey(p.ProjectID) ? totalViewMap[p.ProjectID] : p.Views;

                    return new HotProjectReportRow
                    {
                        ProjectID = p.ProjectID,
                        ProjectName = p.ProjectName ?? "Chưa rõ dự án",
                        LocationName = p.Ward?.WardName ?? p.Area?.AreaName ?? "Chưa có khu vực",
                        Leads = pLead,
                        Closed = pClosed,
                        Appointments = pAppointments,
                        Views = pViews,
                        ConversionRate = pLead > 0 ? Math.Round((decimal)pClosed * 100 / pLead, 1) : 0
                    };
                })
                .OrderByDescending(x => x.Closed)
                .ThenByDescending(x => x.Leads)
                .ThenByDescending(x => x.Views)
                .Take(5)
                .ToList();

            ViewBag.BusinessName = businessProfile?.BusinessName ?? "Doanh nghiệp đối tác";
            ViewBag.TotalProjects = projects.Count;
            ViewBag.TotalProperties = propertiesLinkedToProjects.Count;

            ViewBag.TotalLeads = leads.Count;
            ViewBag.NewLeads = newLeads;
            ViewBag.ContactedLeads = contactedLeads;
            ViewBag.ResolvedLeads = resolvedLeads;
            ViewBag.InvalidLeads = invalidLeads;

            ViewBag.TotalAppointments = appointments.Count;
            ViewBag.PendingAppointments = pendingAppointments;
            ViewBag.ConfirmedAppointments = confirmedAppointments;
            ViewBag.CompletedAppointments = completedAppointments;
            ViewBag.CancelledAppointments = cancelledAppointments;
            ViewBag.DepositPendingAppointments = depositPendingAppointments;

            ViewBag.TotalClosedDeals = totalClosedDeals;
            ViewBag.TotalProjectViews = totalProjectViews;
            ViewBag.TotalPropertyViews = totalPropertyViews;
            ViewBag.TotalViews = totalViews;
            ViewBag.TotalInteractions = totalInteractions;

            ViewBag.TotalSupportTickets = supportTickets.Count;
            ViewBag.OpenSupportTickets = openSupportTickets;
            ViewBag.ClosedSupportTickets = closedSupportTickets;

            ViewBag.LeadMap = leadMap;
            ViewBag.NewLeadMap = newLeadMap;
            ViewBag.ContactedLeadMap = contactedLeadMap;
            ViewBag.ResolvedLeadMap = resolvedLeadMap;
            ViewBag.InvalidLeadMap = invalidLeadMap;

            ViewBag.AppointmentMap = appointmentMap;
            ViewBag.CompletedAppointmentMap = completedAppointmentMap;
            ViewBag.DepositPendingAppointmentMap = depositPendingAppointmentMap;

            ViewBag.PropertyViewMap = propertyViewMap;
            ViewBag.TotalViewMap = totalViewMap;
            ViewBag.PropertyCountMap = propertyCountMap;
            ViewBag.SupportMap = supportMap;

            ViewBag.ClosedDealRows = closedDealRows;
            ViewBag.HotProjects = hotProjects;

            return View(projects);
        }
    }
}
