using BDSKhanhHoa.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")] // Đảm bảo chỉ Admin cấp cao mới được đụng vào Backup
    [Route("Admin/[controller]/[action]")]
    public class BackupController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _env;

        public BackupController(ApplicationDbContext context, IWebHostEnvironment env)
        {
            _context = context;
            _env = env;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            // 1. Lấy thống kê nhanh
            ViewBag.Users = await _context.Users.CountAsync();
            ViewBag.Properties = await _context.Properties.CountAsync();
            ViewBag.Projects = await _context.Projects.CountAsync();
            ViewBag.Appointments = await _context.Appointments.CountAsync();
            ViewBag.Consultations = await _context.Consultations.CountAsync();
            ViewBag.ContactMessages = await _context.ContactMessages.CountAsync();
            ViewBag.ProjectLeads = await _context.ProjectLeads.CountAsync();
            ViewBag.Reports = await _context.PropertyReports.CountAsync();
            ViewBag.AuditLogs = await _context.AuditLogs.CountAsync();

            // 2. Lấy danh sách các file backup hiện có trong thư mục
            var backupFolder = Path.Combine(_env.WebRootPath, "backups");
            if (!Directory.Exists(backupFolder))
            {
                Directory.CreateDirectory(backupFolder);
            }

            var backupFiles = new DirectoryInfo(backupFolder)
                .GetFiles("*.bak")
                .OrderByDescending(f => f.CreationTime)
                .Select(f => new
                {
                    Name = f.Name,
                    Size = (f.Length / 1024.0 / 1024.0).ToString("0.00") + " MB",
                    CreatedAt = f.CreationTime
                })
                .ToList();

            ViewBag.BackupFiles = backupFiles;

            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateBackup()
        {
            try
            {
                // Lấy tên Database tự động từ ConnectionString
                var dbName = _context.Database.GetDbConnection().Database;

                var backupFolder = Path.Combine(_env.WebRootPath, "backups");
                if (!Directory.Exists(backupFolder)) Directory.CreateDirectory(backupFolder);

                var fileName = $"BDSKhanhHoa_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.bak";
                var filePath = Path.Combine(backupFolder, fileName);

                // Lệnh SQL Server chuẩn để tạo file .bak
                var sqlCommand = $"BACKUP DATABASE [{dbName}] TO DISK = '{filePath}' WITH FORMAT, MEDIANAME = 'DbBackup', NAME = 'Full Backup of {dbName}'";

                // Thực thi lệnh bỏ qua tracking của EF
                await _context.Database.ExecuteSqlRawAsync(sqlCommand);

                TempData["SuccessMessage"] = "Tạo bản sao lưu toàn bộ dữ liệu thành công!";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Lỗi khi sao lưu: " + ex.Message + " (Lưu ý: Service SQL Server cần quyền Write vào thư mục wwwroot/backups).";
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public IActionResult DownloadBackup(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return BadRequest();

            var filePath = Path.Combine(_env.WebRootPath, "backups", fileName);
            if (!System.IO.File.Exists(filePath)) return NotFound();

            var bytes = System.IO.File.ReadAllBytes(filePath);
            return File(bytes, "application/octet-stream", fileName);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteBackup(string fileName)
        {
            try
            {
                var filePath = Path.Combine(_env.WebRootPath, "backups", fileName);
                if (System.IO.File.Exists(filePath))
                {
                    System.IO.File.Delete(filePath);
                    TempData["SuccessMessage"] = $"Đã xóa bản sao lưu {fileName}.";
                }
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Không thể xóa file: " + ex.Message;
            }

            return RedirectToAction(nameof(Index));
        }

        // Giữ lại hàm xuất CSV gốc của bạn để tải báo cáo nhanh
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
            var fileName = $"ThongKe_DuLieu_BDSKhanhHoa_{DateTime.Now:yyyyMMdd}.csv";
            return File(bytes, "text/csv", fileName);
        }
    }
}