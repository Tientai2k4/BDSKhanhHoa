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

        // GET: /Admin/Transactions/Index
        public async Task<IActionResult> Index(string searchString, string statusFilter, DateTime? startDate, DateTime? endDate, int page = 1)
        {
            int pageSize = 15;

            var query = _context.Transactions
                .Include(t => t.User)
                .Include(t => t.Package)
                .Include(t => t.Property)
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

            if (startDate.HasValue)
            {
                query = query.Where(t => t.CreatedAt >= startDate.Value.Date);
            }
            if (endDate.HasValue)
            {
                query = query.Where(t => t.CreatedAt <= endDate.Value.Date.AddDays(1).AddTicks(-1));
            }

            var today = DateTime.Today;
            var thisMonth = new DateTime(today.Year, today.Month, 1);
            var thisYear = new DateTime(today.Year, 1, 1);

            ViewBag.TotalRevenue = await _context.Transactions.Where(t => t.Status == "Success").SumAsync(t => t.Amount);
            ViewBag.TodayRevenue = await _context.Transactions.Where(t => t.Status == "Success" && t.CreatedAt >= today).SumAsync(t => t.Amount);
            ViewBag.ThisMonthRevenue = await _context.Transactions.Where(t => t.Status == "Success" && t.CreatedAt >= thisMonth).SumAsync(t => t.Amount);
            ViewBag.ThisYearRevenue = await _context.Transactions.Where(t => t.Status == "Success" && t.CreatedAt >= thisYear).SumAsync(t => t.Amount);

            ViewBag.PendingCount = await _context.Transactions.CountAsync(t => t.Status == "Pending");
            ViewBag.SuccessCount = await _context.Transactions.CountAsync(t => t.Status == "Success");
            ViewBag.TotalTransactions = await query.CountAsync();

            var last30Days = today.AddDays(-30);
            var chartDataQuery = await _context.Transactions
                .Where(t => t.Status == "Success" && t.CreatedAt >= last30Days)
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

            int totalItems = await query.CountAsync();
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
            page = page < 1 ? 1 : (page > totalPages && totalPages > 0 ? totalPages : page);

            var transactions = await query
                .OrderByDescending(t => t.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.CurrentSearch = searchString;
            ViewBag.CurrentStatus = statusFilter;
            ViewBag.StartDate = startDate?.ToString("yyyy-MM-dd");
            ViewBag.EndDate = endDate?.ToString("yyyy-MM-dd");
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;

            ViewData["Title"] = "Quản lý Tài chính & Đối soát";
            return View(transactions);
        }

        [HttpGet]
        public async Task<IActionResult> ExportCsv(string searchString, string statusFilter, DateTime? startDate, DateTime? endDate)
        {
            var query = _context.Transactions
                .Include(t => t.User)
                .Include(t => t.Package)
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

            var transactions = await query.OrderByDescending(t => t.CreatedAt).ToListAsync();

            var builder = new StringBuilder();
            builder.AppendLine("Mã GD,Khách hàng,SĐT,Nội dung,Gói dịch vụ,Số lượng,Số tiền (VND),Thời gian tạo,Trạng thái");

            foreach (var t in transactions)
            {
                string user = $"\"{t.User?.FullName ?? t.User?.Username}\"";
                string phone = $"\"{t.User?.Phone}\"";
                string desc = $"\"{t.Description?.Replace("\"", "\"\"")}\"";
                string package = $"\"{t.Package?.PackageName}\"";
                string date = t.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");

                builder.AppendLine($"{t.TransactionCode},{user},{phone},{desc},{package},{t.Quantity},{t.Amount},{date},{t.Status}");
            }

            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray();
            return File(bytes, "text/csv", $"BaoCaoTaiChinh_{DateTime.Now:yyyyMMddHHmmss}.csv");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStatus(int id, string status, string adminNote)
        {
            var transaction = await _context.Transactions
                .Include(t => t.User)
                .Include(t => t.Property)
                .Include(t => t.Package)
                .FirstOrDefaultAsync(t => t.TransactionID == id);

            if (transaction == null || transaction.Status != "Pending")
            {
                TempData["Error"] = "Giao dịch không tồn tại hoặc đã được xử lý trước đó!";
                return RedirectToAction(nameof(Index));
            }

            transaction.Status = status;
            string actionText = "";

            if (status == "Success")
            {
                actionText = "Duyệt thành công";
                if (transaction.PropertyID != null && transaction.PackageID != null)
                {
                    var property = transaction.Property;
                    var package = transaction.Package;

                    property.PackageID = package.PackageID;
                    property.Status = "Active";

                    if (property.VipExpiryDate == null || property.VipExpiryDate < DateTime.Now)
                    {
                        property.VipExpiryDate = DateTime.Now.AddDays(package.DurationDays * transaction.Quantity);
                    }
                    else
                    {
                        property.VipExpiryDate = property.VipExpiryDate.Value.AddDays(package.DurationDays * transaction.Quantity);
                    }
                }
            }
            else if (status == "Rejected")
            {
                actionText = "Từ chối giao dịch";
            }

            var notification = new Notification
            {
                UserID = transaction.UserID,
                Title = status == "Success" ? $"Thanh toán thành công #{transaction.TransactionCode}" : $"Thanh toán thất bại #{transaction.TransactionCode}",
                Content = status == "Success"
                    ? $"Hệ thống đã ghi nhận số tiền {transaction.Amount:N0}đ. Dịch vụ của bạn đã được kích hoạt thành công!"
                    : $"Giao dịch của bạn bị từ chối. Lời nhắn từ Admin: {adminNote ?? "Sai cú pháp hoặc chưa nhận được tiền"}.",
                IsRead = false,
                CreatedAt = DateTime.Now,
                ActionUrl = "/Payment/History",
                ActionText = "Xem lịch sử GD"
            };
            _context.Notifications.Add(notification);

            var currentAdminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            _context.AuditLogs.Add(new AuditLog
            {
                UserID = int.Parse(currentAdminId),
                Action = $"{actionText} giao dịch {transaction.TransactionCode}",
                Target = $"Transactions (ID: {transaction.TransactionID})",
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();
            TempData["Success"] = $"Đã xử lý giao dịch #{transaction.TransactionCode} thành công!";
            return RedirectToAction(nameof(Index));
        }
    }
}