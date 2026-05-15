using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class TransactionsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IAuditLogService _auditLogService;

        public TransactionsController(ApplicationDbContext context, IAuditLogService auditLogService)
        {
            _context = context;
            _auditLogService = auditLogService;
        }

        // Hàm tiện ích tách mã cơ sở (BaseCode) từ mã giao dịch (ví dụ: 1715312345_0 -> 1715312345)
        private string GetBaseCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            int lastDash = code.LastIndexOf("_");
            return lastDash > 0 ? code.Substring(0, lastDash) : code;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? searchString, string? statusFilter, DateTime? startDate, DateTime? endDate, int page = 1)
        {
            const int pageSize = 15;
            var query = _context.Transactions
                .Include(t => t.User)
                .Include(t => t.Package)
                .Where(t => t.PaymentMethod != "System Gift")
                .AsQueryable();

            // 1. Áp dụng các bộ lọc dữ liệu
            if (!string.IsNullOrEmpty(searchString))
            {
                string searchLower = searchString.ToLower().Trim();
                query = query.Where(t => t.TransactionCode.ToLower().Contains(searchLower) ||
                                         (t.User.FullName != null && t.User.FullName.ToLower().Contains(searchLower)) ||
                                         t.User.Username.ToLower().Contains(searchLower));
            }

            if (!string.IsNullOrEmpty(statusFilter) && statusFilter != "All")
            {
                query = query.Where(t => t.Status == statusFilter);
            }

            if (startDate.HasValue) query = query.Where(t => t.CreatedAt >= startDate.Value.Date);
            if (endDate.HasValue) query = query.Where(t => t.CreatedAt <= endDate.Value.Date.AddDays(1).AddTicks(-1));

            // 2. Lấy dữ liệu thô từ DB (Sắp xếp theo thời gian mới nhất)
            var rawList = await query.OrderByDescending(t => t.CreatedAt).ToListAsync();

            // 3. Logic Gộp Nhóm: Giao diện hiển thị theo Đơn hàng (BaseCode) thay vì từng dòng lẻ
            var groupedList = rawList
                .GroupBy(t => new {
                    BaseCode = GetBaseCode(t.TransactionCode),
                    t.UserID,
                    t.Status
                })
                .Select(g => new Transaction
                {
                    TransactionID = g.First().TransactionID,
                    TransactionCode = g.Key.BaseCode, // Hiển thị mã đơn chung
                    UserID = g.Key.UserID,
                    User = g.First().User,
                    // Hiển thị tên gói đầu tiên kèm hậu tố nếu có nhiều gói khác nhau
                    Package = g.First().Package,
                    Amount = g.Sum(x => x.Amount),
                    Quantity = g.Sum(x => x.Quantity),
                    Status = g.Key.Status,
                    CreatedAt = g.First().CreatedAt,
                    PaymentMethod = g.First().PaymentMethod,
                    BillImageUrl = g.First().BillImageUrl,
                    Description = g.Count() > 1 ? $"{g.First().Package?.PackageName} và {g.Count() - 1} gói khác" : g.First().Package?.PackageName
                }).ToList();

            // 4. Thống kê nhanh cho Dashboard Tài chính
            var today = DateTime.Today;
            var thisMonth = new DateTime(today.Year, today.Month, 1);

            ViewBag.TotalRevenue = await _context.Transactions.Where(t => t.Status == "Success").SumAsync(t => t.Amount);
            ViewBag.TodayRevenue = await _context.Transactions.Where(t => t.Status == "Success" && t.CreatedAt >= today).SumAsync(t => t.Amount);
            ViewBag.ThisMonthRevenue = await _context.Transactions.Where(t => t.Status == "Success" && t.CreatedAt >= thisMonth).SumAsync(t => t.Amount);

            ViewBag.PendingCount = groupedList.Count(t => t.Status == "Pending");
            ViewBag.TotalTransactions = groupedList.Count;

            // 5. Chuẩn bị dữ liệu biểu đồ 30 ngày
            var chartData = await _context.Transactions
                .Where(t => t.Status == "Success" && t.CreatedAt >= today.AddDays(-30))
                .GroupBy(t => t.CreatedAt.Date)
                .Select(g => new { Date = g.Key, Total = g.Sum(t => t.Amount) })
                .OrderBy(g => g.Date)
                .ToListAsync();

            ViewBag.ChartLabels = JsonSerializer.Serialize(chartData.Select(d => d.Date.ToString("dd/MM")));
            ViewBag.ChartValues = JsonSerializer.Serialize(chartData.Select(d => d.Total));

            // 6. Phân trang cho danh sách đã gộp
            int totalItems = groupedList.Count;
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
            page = Math.Clamp(page, 1, totalPages > 0 ? totalPages : 1);
            var pagedTransactions = groupedList.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            ViewBag.CurrentSearch = searchString;
            ViewBag.CurrentStatus = statusFilter;
            ViewBag.StartDate = startDate?.ToString("yyyy-MM-dd");
            ViewBag.EndDate = endDate?.ToString("yyyy-MM-dd");
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;

            return View(pagedTransactions);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStatus(string baseCode, string status, string? adminNote)
        {
            var adminIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(adminIdStr)) return Unauthorized();

            // Tìm toàn bộ các transaction con thuộc cụm đơn hàng này
            var transactions = await _context.Transactions
                .Include(t => t.User)
                .Include(t => t.Property)
                .Include(t => t.Package)
                .Where(t => t.TransactionCode.StartsWith(baseCode))
                .ToListAsync();

            if (!transactions.Any())
            {
                TempData["Error"] = "Không tìm thấy thông tin đơn hàng!";
                return RedirectToAction(nameof(Index));
            }

            if (transactions.All(t => t.Status != "Pending"))
            {
                TempData["Error"] = "Đơn hàng này đã được xử lý trước đó.";
                return RedirectToAction(nameof(Index));
            }

            using var dbTransaction = await _context.Database.BeginTransactionAsync();
            try
            {
                decimal totalAmount = 0;
                int userId = transactions.First().UserID;

                foreach (var t in transactions)
                {
                    t.Status = status;
                    totalAmount += t.Amount;

                    // Nếu Duyệt thành công: Kích hoạt gói dịch vụ cho User/BĐS
                    if (status == "Success")
                    {
                        if (t.PropertyID != null && t.Package != null)
                        {
                            var prop = await _context.Properties.FindAsync(t.PropertyID);
                            if (prop != null)
                            {
                                prop.Status = "Approved";
                                prop.PackageID = t.PackageID ?? prop.PackageID;
                                // Cộng dồn ngày hết hạn
                                DateTime baseDate = (prop.VipExpiryDate > DateTime.Now) ? prop.VipExpiryDate.Value : DateTime.Now;
                                prop.VipExpiryDate = baseDate.AddDays(t.Package.DurationDays * t.Quantity);
                            }
                        }
                    }
                }

                // Gửi thông báo cho User
                _context.Notifications.Add(new Notification
                {
                    UserID = userId,
                    Title = status == "Success" ? "Thanh toán thành công" : "Giao dịch bị từ chối",
                    Content = status == "Success"
                        ? $"Đơn hàng #{baseCode} đã được duyệt. Cảm ơn bạn đã sử dụng dịch vụ!"
                        : $"Đơn hàng #{baseCode} không thành công. Lý do: {adminNote ?? "Thông tin chưa chính xác"}",
                    CreatedAt = DateTime.Now,
                    IsRead = false,
                    ActionUrl = "/Payment/History"
                });

                await _context.SaveChangesAsync();
                await dbTransaction.CommitAsync();

                // Ghi log hệ thống
                await _auditLogService.LogAsync(int.Parse(adminIdStr),
                    $"Xử lý đơn hàng #{baseCode}", "Finance",
                    $"Status: {status}, Total: {totalAmount:N0}đ",
                    severity: status == "Success" ? "Info" : "Warning");

                TempData["Success"] = $"Đã cập nhật trạng thái đơn hàng #{baseCode} thành công!";
            }
            catch (Exception ex)
            {
                await dbTransaction.RollbackAsync();
                TempData["Error"] = "Lỗi hệ thống: " + ex.Message;
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> ExportCsv(string? searchString, string? statusFilter, DateTime? startDate, DateTime? endDate)
        {
            var query = _context.Transactions
                .Include(t => t.User)
                .Include(t => t.Package)
                .Where(t => t.PaymentMethod != "System Gift")
                .AsQueryable();

            // Tái áp dụng lọc giống hệt trang Index
            if (!string.IsNullOrEmpty(searchString))
            {
                string s = searchString.ToLower();
                query = query.Where(t => t.TransactionCode.Contains(s) || t.User.FullName.Contains(s));
            }
            if (!string.IsNullOrEmpty(statusFilter) && statusFilter != "All") query = query.Where(t => t.Status == statusFilter);
            if (startDate.HasValue) query = query.Where(t => t.CreatedAt >= startDate.Value.Date);
            if (endDate.HasValue) query = query.Where(t => t.CreatedAt <= endDate.Value.Date.AddDays(1).AddTicks(-1));

            var data = await query.OrderByDescending(t => t.CreatedAt).ToListAsync();

            var builder = new StringBuilder();
            // CHỐT CHẶN LỖI FONT: Thêm BOM cho UTF-8 để Excel nhận diện được Tiếng Việt
            builder.Append('\uFEFF');

            // Header - Sử dụng dấu phẩy và bao đóng ngoặc kép chuẩn CSV
            builder.AppendLine("\"Mã GD\",\"Thời gian\",\"Khách hàng\",\"SĐT\",\"Dịch vụ\",\"Số lượng\",\"Số tiền (VNĐ)\",\"Phương thức\",\"Trạng thái\"");

            foreach (var item in data)
            {
                builder.AppendLine(string.Format("\"{0}\",\"{1}\",\"{2}\",\"{3}\",\"{4}\",\"{5}\",\"{6}\",\"{7}\",\"{8}\"",
                    item.TransactionCode,
                    item.CreatedAt.ToString("dd/MM/yyyy HH:mm"),
                    item.User?.FullName ?? item.User?.Username,
                    item.User?.Phone ?? "N/A",
                    item.Package?.PackageName ?? "Gói đã xóa",
                    item.Quantity,
                    item.Amount.ToString("F0"),
                    item.PaymentMethod,
                    item.Status
                ));
            }

            var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            await _auditLogService.LogAsync(int.Parse(adminId ?? "0"), "Xuất báo cáo tài chính CSV", "Finance", "Tất cả bản ghi theo bộ lọc");

            string fileName = $"Bao_Cao_Tai_Chinh_{DateTime.Now:yyyyMMdd_HHmm}.csv";
            return File(Encoding.UTF8.GetBytes(builder.ToString()), "text/csv; charset=utf-8", fileName);
        }
    }
}