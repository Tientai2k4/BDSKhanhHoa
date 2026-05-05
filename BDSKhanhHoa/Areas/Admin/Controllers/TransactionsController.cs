using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
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

        public TransactionsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // Hàm hỗ trợ cắt bỏ phần đuôi _1, _2 để lấy Mã gốc (Base Code)
        private string GetBaseCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            int lastDash = code.LastIndexOf("_");
            return lastDash > 0 ? code.Substring(0, lastDash) : code;
        }

        // GET: /Admin/Transactions/Index
        public async Task<IActionResult> Index(string searchString, string statusFilter, DateTime? startDate, DateTime? endDate, int page = 1)
        {
            int pageSize = 15;

            // 1. Lấy dữ liệu và LOẠI BỎ TIN 0Đ (System Gift)
            var query = _context.Transactions
                .Include(t => t.User)
                .Include(t => t.Package)
                .Include(t => t.Property)
                .Where(t => t.PaymentMethod != "System Gift")
                .AsQueryable();

            if (!string.IsNullOrEmpty(searchString))
            {
                string searchLower = searchString.ToLower();
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

            // 2. Kéo dữ liệu về RAM để thực hiện Gộp nhóm (Group By)
            var rawList = await query.OrderByDescending(t => t.CreatedAt).ToListAsync();

            // 3. LOGIC GỘP NHÓM GIAO DỊCH (Gộp Số lượng & Tổng tiền theo Mã gốc)
            var groupedList = rawList
                .GroupBy(t => new {
                    BaseCode = GetBaseCode(t.TransactionCode),
                    t.UserID,
                    t.PackageID,
                    t.Status
                })
                .Select(g => new Transaction
                {
                    TransactionID = g.First().TransactionID,
                    TransactionCode = g.Key.BaseCode, // Sử dụng mã gốc
                    UserID = g.Key.UserID,
                    User = g.First().User,
                    PackageID = g.Key.PackageID,
                    Package = g.First().Package,
                    Amount = g.Sum(x => x.Amount),     // CỘNG DỒN TỔNG TIỀN
                    Quantity = g.Sum(x => x.Quantity), // CỘNG DỒN SỐ LƯỢNG
                    Status = g.Key.Status,
                    CreatedAt = g.First().CreatedAt,
                    Description = g.First().Description
                }).ToList();

            // 4. Phân trang trên dữ liệu đã gộp
            int totalItems = groupedList.Count;
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
            page = page < 1 ? 1 : (page > totalPages && totalPages > 0 ? totalPages : page);

            var pagedTransactions = groupedList.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            // Tính toán KPI Thống kê
            var today = DateTime.Today;
            var thisMonth = new DateTime(today.Year, today.Month, 1);
            var thisYear = new DateTime(today.Year, 1, 1);

            ViewBag.TotalRevenue = await _context.Transactions.Where(t => t.Status == "Success" && t.PaymentMethod != "System Gift").SumAsync(t => t.Amount);
            ViewBag.TodayRevenue = await _context.Transactions.Where(t => t.Status == "Success" && t.CreatedAt >= today && t.PaymentMethod != "System Gift").SumAsync(t => t.Amount);
            ViewBag.ThisMonthRevenue = await _context.Transactions.Where(t => t.Status == "Success" && t.CreatedAt >= thisMonth && t.PaymentMethod != "System Gift").SumAsync(t => t.Amount);
            ViewBag.ThisYearRevenue = await _context.Transactions.Where(t => t.Status == "Success" && t.CreatedAt >= thisYear && t.PaymentMethod != "System Gift").SumAsync(t => t.Amount);

            // Đếm số lượng dựa trên BaseCode
            ViewBag.PendingCount = groupedList.Count(t => t.Status == "Pending");
            ViewBag.SuccessCount = groupedList.Count(t => t.Status == "Success");
            ViewBag.TotalTransactions = totalItems;

            // Biểu đồ
            var last30Days = today.AddDays(-30);
            var chartDataQuery = await _context.Transactions
                .Where(t => t.Status == "Success" && t.CreatedAt >= last30Days && t.PaymentMethod != "System Gift")
                .GroupBy(t => t.CreatedAt.Date)
                .Select(g => new { Date = g.Key, Total = g.Sum(t => t.Amount) })
                .OrderBy(g => g.Date)
                .ToListAsync();

            var chartLabels = new List<string>();
            var chartValues = new List<decimal>();

            for (int i = 30; i >= 0; i--)
            {
                var date = today.AddDays(-i);
                chartLabels.Add(date.ToString("dd/MM"));
                var dataPoint = chartDataQuery.FirstOrDefault(d => d.Date == date);
                chartValues.Add(dataPoint?.Total ?? 0);
            }

            ViewBag.ChartLabels = JsonSerializer.Serialize(chartLabels);
            ViewBag.ChartValues = JsonSerializer.Serialize(chartValues);

            ViewBag.CurrentSearch = searchString;
            ViewBag.CurrentStatus = statusFilter;
            ViewBag.StartDate = startDate?.ToString("yyyy-MM-dd");
            ViewBag.EndDate = endDate?.ToString("yyyy-MM-dd");
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;

            ViewData["Title"] = "Quản lý Tài chính & Đối soát";
            return View(pagedTransactions);
        }

        // POST: /Admin/Transactions/UpdateStatus
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStatus(string baseCode, string status, string adminNote)
        {
            // Lấy TẤT CẢ các giao dịch bắt đầu bằng BaseCode (VD: TXN123_1, TXN123_2)
            var transactions = await _context.Transactions
                .Include(t => t.User)
                .Include(t => t.Property)
                .Include(t => t.Package)
                .Where(t => t.TransactionCode.StartsWith(baseCode))
                .ToListAsync();

            if (!transactions.Any() || transactions.Any(t => t.Status != "Pending"))
            {
                TempData["Error"] = "Cụm giao dịch không tồn tại hoặc đã được xử lý trước đó!";
                return RedirectToAction(nameof(Index));
            }

            string actionText = status == "Success" ? "Duyệt thành công" : "Từ chối";
            decimal totalAmount = 0;
            int totalQty = 0;
            int userId = transactions.First().UserID;

            // Xử lý cập nhật hàng loạt
            foreach (var t in transactions)
            {
                t.Status = status;
                totalAmount += t.Amount;
                totalQty += t.Quantity;

                // Nếu mua kèm cho 1 BĐS cụ thể (Thường ít dùng, chủ yếu mua vào ví)
                if (status == "Success" && t.PropertyID != null && t.PackageID != null)
                {
                    var property = t.Property;
                    var package = t.Package;
                    property.PackageID = package.PackageID;
                    property.Status = "Active";

                    if (property.VipExpiryDate == null || property.VipExpiryDate < DateTime.Now)
                        property.VipExpiryDate = DateTime.Now.AddDays(package.DurationDays * t.Quantity);
                    else
                        property.VipExpiryDate = property.VipExpiryDate.Value.AddDays(package.DurationDays * t.Quantity);
                }
            }

            // Gửi duy nhất 1 Thông báo (Notification) tổng hợp cho Khách hàng
            var notification = new Notification
            {
                UserID = userId,
                Title = status == "Success" ? $"Thanh toán thành công #{baseCode}" : $"Thanh toán thất bại #{baseCode}",
                Content = status == "Success"
                    ? $"Hệ thống đã ghi nhận tổng số tiền {totalAmount:N0}đ cho {totalQty} gói dịch vụ. Dịch vụ đã được nạp vào ví!"
                    : $"Giao dịch của bạn bị từ chối. Lời nhắn từ Admin: {adminNote ?? "Sai cú pháp hoặc chưa nhận được tiền"}.",
                IsRead = false,
                CreatedAt = DateTime.Now,
                ActionUrl = "/Payment/History",
                ActionText = "Xem lịch sử GD"
            };
            _context.Notifications.Add(notification);

            // Ghi AuditLog
            var currentAdminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            _context.AuditLogs.Add(new AuditLog
            {
                UserID = int.Parse(currentAdminId),
                Action = $"{actionText} cụm giao dịch {baseCode} ({totalQty} gói)",
                Target = $"Transactions (BaseCode: {baseCode})",
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();
            TempData["Success"] = $"Đã xử lý cụm giao dịch #{baseCode} thành công!";
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> ExportCsv(string searchString, string statusFilter, DateTime? startDate, DateTime? endDate)
        {
            var query = _context.Transactions
                .Include(t => t.User)
                .Include(t => t.Package)
                .Where(t => t.PaymentMethod != "System Gift")
                .AsQueryable();

            if (!string.IsNullOrEmpty(searchString))
            {
                string searchLower = searchString.ToLower();
                query = query.Where(t => t.TransactionCode.ToLower().Contains(searchLower) ||
                                         (t.User.FullName != null && t.User.FullName.ToLower().Contains(searchLower)) ||
                                         t.User.Username.ToLower().Contains(searchLower));
            }

            if (!string.IsNullOrEmpty(statusFilter) && statusFilter != "All") query = query.Where(t => t.Status == statusFilter);
            if (startDate.HasValue) query = query.Where(t => t.CreatedAt >= startDate.Value.Date);
            if (endDate.HasValue) query = query.Where(t => t.CreatedAt <= endDate.Value.Date.AddDays(1).AddTicks(-1));

            var rawList = await query.OrderByDescending(t => t.CreatedAt).ToListAsync();

            var groupedList = rawList
                .GroupBy(t => new { BaseCode = GetBaseCode(t.TransactionCode), t.UserID, t.PackageID, t.Status })
                .Select(g => new {
                    TransactionCode = g.Key.BaseCode,
                    User = g.First().User,
                    PackageName = g.First().Package?.PackageName,
                    Amount = g.Sum(x => x.Amount),
                    Quantity = g.Sum(x => x.Quantity),
                    Status = g.Key.Status,
                    Date = g.First().CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
                }).ToList();

            var builder = new StringBuilder();
            builder.Append("\uFEFF"); // Hỗ trợ UTF-8 Excel
            builder.AppendLine("Mã GD,Khách hàng,SĐT,Gói dịch vụ,Số lượng,Tổng tiền (VND),Thời gian tạo,Trạng thái");

            foreach (var t in groupedList)
            {
                string user = $"\"{t.User?.FullName ?? t.User?.Username}\"";
                string phone = $"\"{t.User?.Phone}\"";
                string package = $"\"{t.PackageName}\"";

                builder.AppendLine($"{t.TransactionCode},{user},{phone},{package},{t.Quantity},{t.Amount},{t.Date},{t.Status}");
            }

            return File(Encoding.UTF8.GetBytes(builder.ToString()), "text/csv", $"BaoCaoTaiChinh_{DateTime.Now:yyyyMMddHHmmss}.csv");
        }
    }
}