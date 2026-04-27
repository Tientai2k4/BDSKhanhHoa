using BDSKhanhHoa.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize]
    [Route("Admin/[controller]/[action]")]
    public class BackupController : Controller
    {
        private readonly ApplicationDbContext _context;

        public BackupController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            ViewBag.Users = await _context.Users.CountAsync();
            ViewBag.Properties = await _context.Properties.CountAsync();
            ViewBag.Projects = await _context.Projects.CountAsync();
            ViewBag.Appointments = await _context.Appointments.CountAsync();
            ViewBag.Consultations = await _context.Consultations.CountAsync();
            ViewBag.ContactMessages = await _context.ContactMessages.CountAsync();
            ViewBag.ProjectLeads = await _context.ProjectLeads.CountAsync();
            ViewBag.Reports = await _context.PropertyReports.CountAsync();
            ViewBag.AuditLogs = await _context.AuditLogs.CountAsync();

            return View();
        }

        [HttpGet]
        public async Task<IActionResult> DownloadSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Entity,Count");
            sb.AppendLine($"Users,{await _context.Users.CountAsync()}");
            sb.AppendLine($"Properties,{await _context.Properties.CountAsync()}");
            sb.AppendLine($"Projects,{await _context.Projects.CountAsync()}");
            sb.AppendLine($"Appointments,{await _context.Appointments.CountAsync()}");
            sb.AppendLine($"Consultations,{await _context.Consultations.CountAsync()}");
            sb.AppendLine($"ContactMessages,{await _context.ContactMessages.CountAsync()}");
            sb.AppendLine($"ProjectLeads,{await _context.ProjectLeads.CountAsync()}");
            sb.AppendLine($"PropertyReports,{await _context.PropertyReports.CountAsync()}");
            sb.AppendLine($"AuditLogs,{await _context.AuditLogs.CountAsync()}");

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            var fileName = $"backup-summary-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
            return File(bytes, "text/csv", fileName);
        }
    }
}