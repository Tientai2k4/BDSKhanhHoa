using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Security.Claims;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")] // Chỉ Admin tối cao mới có quyền quản trị dữ liệu gốc
    [Route("Admin/[controller]/[action]")]
    public class BackupController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _env;
        private readonly IAuditLogService _auditLogService;

        public BackupController(ApplicationDbContext context, IWebHostEnvironment env, IAuditLogService auditLogService)
        {
            _context = context;
            _env = env;
            _auditLogService = auditLogService;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            // 1. Thống kê quy mô dữ liệu thực tế
            ViewBag.Users = await _context.Users.CountAsync();
            ViewBag.Properties = await _context.Properties.CountAsync();
            ViewBag.Projects = await _context.Projects.CountAsync();
            ViewBag.Leads = await _context.ProjectLeads.CountAsync();
            ViewBag.Transactions = await _context.Transactions.CountAsync();
            ViewBag.AuditLogs = await _context.AuditLogs.CountAsync();

            // 2. Quản lý tệp tin sao lưu
            var backupFolder = Path.Combine(_env.WebRootPath, "backups");
            if (!Directory.Exists(backupFolder)) Directory.CreateDirectory(backupFolder);

            var backupFiles = new DirectoryInfo(backupFolder)
                .GetFiles("*.bak")
                .OrderByDescending(f => f.CreationTime)
                .Select(f => new {
                    Name = f.Name,
                    Size = (f.Length / 1024.0 / 1024.0).ToString("0.00") + " MB",
                    CreatedAt = f.CreationTime
                }).ToList();

            ViewBag.BackupFiles = backupFiles;
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateBackup()
        {
            var adminId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
            var dbName = _context.Database.GetDbConnection().Database;
            var fileName = $"BDSKhanhHoa_Full_{DateTime.Now:yyyyMMdd_HHmmss}.bak";

            // Bước 1: Dùng thư mục chung mà SQL Server dễ truy cập hơn (Ví dụ ổ C hoặc thư mục mặc định của SQL)
            // Hoặc đơn giản nhất là dùng thư mục gốc ổ C nhưng phải tạo trước
            string sqlBackupFolder = @"C:\BDS_Backups_Temp";
            if (!Directory.Exists(sqlBackupFolder)) Directory.CreateDirectory(sqlBackupFolder);

            var tempSqlPath = Path.Combine(sqlBackupFolder, fileName);
            var finalWebPath = Path.Combine(_env.WebRootPath, "backups", fileName);

            try
            {
                // Bước 2: Lệnh SQL yêu cầu SQL Server ghi vào thư mục chung
                string sqlCommand = $"BACKUP DATABASE [{dbName}] TO DISK = '{tempSqlPath}' WITH FORMAT, NAME = 'Full Backup of {dbName}'";
                await _context.Database.ExecuteSqlRawAsync(sqlCommand);

                // Bước 3: Ứng dụng Web di chuyển file từ thư mục chung về wwwroot/backups
                if (System.IO.File.Exists(tempSqlPath))
                {
                    string webBackupDir = Path.Combine(_env.WebRootPath, "backups");
                    if (!Directory.Exists(webBackupDir)) Directory.CreateDirectory(webBackupDir);

                    System.IO.File.Move(tempSqlPath, finalWebPath);
                }

                await _auditLogService.LogAsync(adminId, "Tạo bản sao lưu hệ thống", "SystemBackup", fileName, severity: "Info");
                TempData["SuccessMessage"] = "Khởi tạo bản sao lưu thành công!";
            }
            catch (Exception ex)
            {
                // Gợi ý hướng dẫn sửa quyền nếu vẫn lỗi
                TempData["ErrorMessage"] = "Lỗi: " + ex.Message + ". Hướng dẫn: Click chuột phải thư mục C:\\BDS_Backups_Temp -> Properties -> Security -> Edit -> Add 'Everyone' -> Check 'Full Control'.";
            }

            return RedirectToAction(nameof(Index));
        }
        [HttpGet]
        public IActionResult DownloadBackup(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return BadRequest();
            var filePath = Path.Combine(_env.WebRootPath, "backups", fileName);

            if (!System.IO.File.Exists(filePath)) return NotFound();

            var adminId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
            _auditLogService.LogAsync(adminId, "Tải bản sao lưu về máy", "SystemBackup", fileName);

            var bytes = System.IO.File.ReadAllBytes(filePath);
            return File(bytes, "application/octet-stream", fileName);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteBackup(string fileName)
        {
            try
            {
                var filePath = Path.Combine(_env.WebRootPath, "backups", fileName);
                if (System.IO.File.Exists(filePath))
                {
                    System.IO.File.Delete(filePath);

                    var adminId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
                    await _auditLogService.LogAsync(adminId, "Xóa tệp tin sao lưu", "SystemBackup", fileName, severity: "Warning");

                    TempData["SuccessMessage"] = $"Đã tiêu hủy bản sao lưu: {fileName}";
                }
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Lỗi khi xóa: " + ex.Message;
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> DownloadSummary()
        {
            var sb = new StringBuilder();
            sb.Append('\uFEFF'); // Thêm BOM để Excel nhận diện tiếng Việt
            sb.AppendLine("Hạng mục dữ liệu,Số lượng bản ghi");
            sb.AppendLine($"Người dùng,{await _context.Users.CountAsync()}");
            sb.AppendLine($"Tin đăng Bất động sản,{await _context.Properties.CountAsync()}");
            sb.AppendLine($"Dự án khu đô thị,{await _context.Projects.CountAsync()}");
            sb.AppendLine($"Lịch hẹn khách hàng,{await _context.Appointments.CountAsync()}");
            sb.AppendLine($"Yêu cầu tư vấn,{await _context.Consultations.CountAsync()}");
            sb.AppendLine($"Giao dịch tài chính,{await _context.Transactions.CountAsync()}");
            sb.AppendLine($"Nhật ký kiểm toán,{await _context.AuditLogs.CountAsync()}");

            var adminId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
            await _auditLogService.LogAsync(adminId, "Xuất báo cáo thống kê quy mô dữ liệu", "SystemBackup", "CSV Summary");

            return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"ThongKe_Data_{DateTime.Now:yyyyMMdd}.csv");
        }
    }
}