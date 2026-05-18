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
            if (string.IsNullOrWhiteSpace(status))
            {
                return "Mới";
            }

            return status.Trim() switch
            {
                "New" => "Mới",
                "Contacted" => "Đã liên hệ",
                "Resolved" => "Đã chốt",
                "Invalid" => "Không hợp lệ",

                "Mới" => "Mới",
                "Đã liên hệ" => "Đã liên hệ",
                "Đã chốt" => "Đã chốt",
                "Không hợp lệ" => "Không hợp lệ",

                _ => status.Trim()
            };
        }

        private static string NormalizeAppointmentStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return "Chờ xác nhận";
            }

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

        private static string NormalizeSupportStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return "Chờ xử lý";
            }

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

            var projectIds = projects
                .Select(p => p.ProjectID)
                .ToList();

            var leads = projectIds.Any()
                ? await _context.ProjectLeads
                    .AsNoTracking()
                    .Where(l => projectIds.Contains(l.ProjectID))
                    .ToListAsync()
                : new List<ProjectLead>();

            var appointments = projectIds.Any()
                ? await _context.Appointments
                    .AsNoTracking()
                    .Where(a =>
                        a.ProjectID.HasValue &&
                        projectIds.Contains(a.ProjectID.Value))
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

            int confirmedAppointments = appointments.Count(a =>
                NormalizeAppointmentStatus(a.Status) == "Đã xác nhận");

            int completedAppointments = appointments.Count(a =>
                NormalizeAppointmentStatus(a.Status) == "Đã hoàn tất");

            int cancelledAppointments = appointments.Count(a =>
                NormalizeAppointmentStatus(a.Status) == "Đã hủy" ||
                NormalizeAppointmentStatus(a.Status) == "Khách không đến");

            int openSupportTickets = supportTickets.Count(t =>
                NormalizeSupportStatus(t.Status) == "Chờ xử lý" ||
                NormalizeSupportStatus(t.Status) == "Đang xử lý");

            int closedSupportTickets = supportTickets.Count(t =>
                NormalizeSupportStatus(t.Status) == "Đã xử lý" ||
                NormalizeSupportStatus(t.Status) == "Đã đóng");

            var leadMap = leads
                .GroupBy(x => x.ProjectID)
                .ToDictionary(g => g.Key, g => g.Count());

            var newLeadMap = leads
                .Where(x => NormalizeLeadStatus(x.LeadStatus) == "Mới")
                .GroupBy(x => x.ProjectID)
                .ToDictionary(g => g.Key, g => g.Count());

            var resolvedLeadMap = leads
                .Where(x => NormalizeLeadStatus(x.LeadStatus) == "Đã chốt")
                .GroupBy(x => x.ProjectID)
                .ToDictionary(g => g.Key, g => g.Count());

            var appointmentMap = appointments
                .Where(x => x.ProjectID.HasValue)
                .GroupBy(x => x.ProjectID!.Value)
                .ToDictionary(g => g.Key, g => g.Count());

            var completedAppointmentMap = appointments
                .Where(x =>
                    x.ProjectID.HasValue &&
                    NormalizeAppointmentStatus(x.Status) == "Đã hoàn tất")
                .GroupBy(x => x.ProjectID!.Value)
                .ToDictionary(g => g.Key, g => g.Count());

            var projectViewMap = projects
                .ToDictionary(p => p.ProjectID, p => p.Views);

            var propertyViewMap = propertiesLinkedToProjects
                .Where(x => x.ProjectID.HasValue)
                .GroupBy(x => x.ProjectID!.Value)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Views ?? 0));

            var totalViewMap = projects
                .ToDictionary(
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

            int totalProjectViews = projects.Sum(p => p.Views);
            int totalPropertyViews = propertiesLinkedToProjects.Sum(p => p.Views ?? 0);
            int totalViews = totalProjectViews + totalPropertyViews;

            ViewBag.BusinessName = businessProfile?.BusinessName ?? "Doanh nghiệp đối tác";

            ViewBag.TotalProjects = projects.Count;

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

            ViewBag.TotalProjectViews = totalProjectViews;
            ViewBag.TotalPropertyViews = totalPropertyViews;
            ViewBag.TotalViews = totalViews;

            ViewBag.TotalProperties = propertiesLinkedToProjects.Count;

            ViewBag.TotalSupportTickets = supportTickets.Count;
            ViewBag.OpenSupportTickets = openSupportTickets;
            ViewBag.ClosedSupportTickets = closedSupportTickets;

            ViewBag.LeadMap = leadMap;
            ViewBag.NewLeadMap = newLeadMap;
            ViewBag.ResolvedLeadMap = resolvedLeadMap;

            ViewBag.AppointmentMap = appointmentMap;
            ViewBag.CompletedAppointmentMap = completedAppointmentMap;

            ViewBag.ProjectViewMap = projectViewMap;
            ViewBag.PropertyViewMap = propertyViewMap;
            ViewBag.TotalViewMap = totalViewMap;

            ViewBag.PropertyCountMap = propertyCountMap;
            ViewBag.SupportMap = supportMap;

            return View(projects);
        }
    }
}