using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
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

        public TransactionsController(
            ApplicationDbContext context,
            IAuditLogService auditLogService)
        {
            _context = context;
            _auditLogService = auditLogService;
        }

        // =====================================================
        // DANH SÁCH GIAO DỊCH / TÀI CHÍNH
        // =====================================================
        [HttpGet]
        public async Task<IActionResult> Index(
            string? searchString,
            string? statusFilter,
            DateTime? startDate,
            DateTime? endDate,
            int page = 1)
        {
            const int pageSize = 15;

            searchString = searchString?.Trim();
            statusFilter = string.IsNullOrWhiteSpace(statusFilter) ? "All" : statusFilter.Trim();

            var query = _context.Transactions
                .Include(t => t.User)
                .Include(t => t.Package)
                .Where(t => t.PaymentMethod != "System Gift")
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(searchString))
            {
                string searchLower = searchString.ToLower();

                query = query.Where(t =>
                    t.TransactionCode.ToLower().Contains(searchLower) ||
                    (t.User != null && t.User.FullName != null && t.User.FullName.ToLower().Contains(searchLower)) ||
                    (t.User != null && t.User.Username != null && t.User.Username.ToLower().Contains(searchLower)) ||
                    (t.User != null && t.User.Phone != null && t.User.Phone.Contains(searchString)));
            }

            if (!string.IsNullOrWhiteSpace(statusFilter) && statusFilter != "All")
            {
                query = query.Where(t => t.Status == statusFilter);
            }

            if (startDate.HasValue)
            {
                query = query.Where(t => t.CreatedAt >= startDate.Value.Date);
            }

            if (endDate.HasValue)
            {
                DateTime endOfDay = endDate.Value.Date.AddDays(1).AddTicks(-1);
                query = query.Where(t => t.CreatedAt <= endOfDay);
            }

            var rawList = await query
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync();

            var groupedList = rawList
                .GroupBy(t => new
                {
                    BaseCode = GetBaseCode(t.TransactionCode),
                    t.UserID,
                    t.Status
                })
                .Select(g =>
                {
                    var first = g.OrderByDescending(x => x.CreatedAt).First();

                    return new Transaction
                    {
                        TransactionID = first.TransactionID,
                        TransactionCode = g.Key.BaseCode,
                        UserID = g.Key.UserID,
                        User = first.User,
                        PackageID = first.PackageID,
                        Package = first.Package,
                        Amount = g.Sum(x => x.Amount),
                        Quantity = g.Sum(x => x.Quantity),
                        Status = g.Key.Status,
                        CreatedAt = first.CreatedAt,
                        PaymentMethod = first.PaymentMethod,
                        BillImageUrl = g.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.BillImageUrl))?.BillImageUrl,
                        Description = g.Count() > 1
                            ? $"{first.Package?.PackageName ?? "Gói dịch vụ"} và {g.Count() - 1} gói khác"
                            : first.Package?.PackageName
                    };
                })
                .OrderByDescending(t => t.CreatedAt)
                .ToList();

            DateTime today = DateTime.Today;
            DateTime thisMonth = new DateTime(today.Year, today.Month, 1);
            DateTime thisYear = new DateTime(today.Year, 1, 1);

            var revenueQuery = _context.Transactions
                .Where(t => t.PaymentMethod != "System Gift" &&
                            (t.Status == "Success" || t.Status == "Completed"));

            ViewBag.TotalRevenue = await revenueQuery.SumAsync(t => t.Amount);
            ViewBag.TodayRevenue = await revenueQuery
                .Where(t => t.CreatedAt >= today)
                .SumAsync(t => t.Amount);

            ViewBag.ThisMonthRevenue = await revenueQuery
                .Where(t => t.CreatedAt >= thisMonth)
                .SumAsync(t => t.Amount);

            ViewBag.ThisYearRevenue = await revenueQuery
                .Where(t => t.CreatedAt >= thisYear)
                .SumAsync(t => t.Amount);

            ViewBag.PendingCount = groupedList.Count(t => t.Status == "Pending");
            ViewBag.SuccessCount = groupedList.Count(t => t.Status == "Success" || t.Status == "Completed");
            ViewBag.RejectedCount = groupedList.Count(t => t.Status == "Rejected" || t.Status == "Failed" || t.Status == "Cancelled");
            ViewBag.TotalTransactions = groupedList.Count;

            var chartData = await revenueQuery
                .Where(t => t.CreatedAt >= today.AddDays(-29))
                .GroupBy(t => t.CreatedAt.Date)
                .Select(g => new
                {
                    Date = g.Key,
                    Total = g.Sum(t => t.Amount)
                })
                .OrderBy(g => g.Date)
                .ToListAsync();

            var chartLabels = new List<string>();
            var chartValues = new List<decimal>();

            for (int i = 29; i >= 0; i--)
            {
                DateTime date = today.AddDays(-i);
                var found = chartData.FirstOrDefault(x => x.Date == date);

                chartLabels.Add(date.ToString("dd/MM"));
                chartValues.Add(found?.Total ?? 0m);
            }

            ViewBag.ChartLabels = JsonSerializer.Serialize(chartLabels);
            ViewBag.ChartValues = JsonSerializer.Serialize(chartValues);

            int totalItems = groupedList.Count;
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

            if (totalPages <= 0)
            {
                totalPages = 1;
            }

            page = Math.Clamp(page, 1, totalPages);

            var pagedTransactions = groupedList
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            ViewBag.CurrentSearch = searchString;
            ViewBag.CurrentStatus = statusFilter;
            ViewBag.StartDate = startDate?.ToString("yyyy-MM-dd");
            ViewBag.EndDate = endDate?.ToString("yyyy-MM-dd");
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;

            return View(pagedTransactions);
        }

        // =====================================================
        // DUYỆT / TỪ CHỐI GIAO DỊCH
        // =====================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStatus(
            string baseCode,
            string status,
            string? adminNote)
        {
            int adminId = GetCurrentAdminId();

            if (adminId <= 0)
            {
                return Unauthorized();
            }

            baseCode = baseCode?.Trim() ?? "";
            status = status?.Trim() ?? "";
            adminNote = adminNote?.Trim();

            if (string.IsNullOrWhiteSpace(baseCode))
            {
                TempData["Error"] = "Mã giao dịch không hợp lệ.";
                return RedirectToAction(nameof(Index));
            }

            if (status != "Success" && status != "Rejected")
            {
                TempData["Error"] = "Trạng thái xử lý giao dịch không hợp lệ.";
                return RedirectToAction(nameof(Index));
            }

            if (status == "Rejected" && string.IsNullOrWhiteSpace(adminNote))
            {
                TempData["Error"] = "Vui lòng nhập lý do từ chối giao dịch.";
                return RedirectToAction(nameof(Index));
            }

            var transactions = await _context.Transactions
                .Include(t => t.User)
                .Include(t => t.Property)
                .Include(t => t.Package)
                .Where(t =>
                    t.TransactionCode == baseCode ||
                    t.TransactionCode.StartsWith(baseCode + "_"))
                .OrderBy(t => t.TransactionID)
                .ToListAsync();

            if (!transactions.Any())
            {
                TempData["Error"] = "Không tìm thấy thông tin đơn hàng.";
                return RedirectToAction(nameof(Index));
            }

            if (transactions.All(t => t.Status != "Pending"))
            {
                TempData["Error"] = "Đơn hàng này đã được xử lý trước đó, không thể xử lý lại.";
                return RedirectToAction(nameof(Index));
            }

            string oldValues = BuildTransactionAuditJson(transactions, "Trước khi Admin xử lý giao dịch.");

            await using var dbTransaction = await _context.Database.BeginTransactionAsync();

            try
            {
                decimal totalAmount = transactions.Sum(t => t.Amount);
                int userId = transactions.First().UserID;

                foreach (var transaction in transactions)
                {
                    if (transaction.Status != "Pending")
                    {
                        continue;
                    }

                    transaction.Status = status;

                    if (status == "Success")
                    {
                        await ApplyPackageAfterSuccessAsync(transaction);
                    }
                }

                _context.Notifications.Add(new Notification
                {
                    UserID = userId,
                    Title = status == "Success" ? "Thanh toán thành công" : "Giao dịch bị từ chối",
                    Content = status == "Success"
                        ? $"Đơn hàng #{baseCode} đã được duyệt. Hệ thống đã ghi nhận thanh toán và kích hoạt gói dịch vụ tương ứng."
                        : $"Đơn hàng #{baseCode} không được duyệt. Lý do: {adminNote ?? "Thông tin thanh toán chưa chính xác."}",
                    CreatedAt = DateTime.Now,
                    IsRead = false,
                    ActionUrl = "/Payment/History",
                    ActionText = "Xem lịch sử giao dịch"
                });

                await _context.SaveChangesAsync();
                await dbTransaction.CommitAsync();

                var updatedTransactions = await _context.Transactions
                    .Include(t => t.User)
                    .Include(t => t.Property)
                    .Include(t => t.Package)
                    .Where(t =>
                        t.TransactionCode == baseCode ||
                        t.TransactionCode.StartsWith(baseCode + "_"))
                    .OrderBy(t => t.TransactionID)
                    .ToListAsync();

                string newValues = BuildTransactionAuditJson(
                    updatedTransactions,
                    status == "Success"
                        ? "Sau khi Admin duyệt giao dịch."
                        : "Sau khi Admin từ chối giao dịch.",
                    adminNote);

                await _auditLogService.LogAsync(
                    adminId,
                    status == "Success" ? "Duyệt giao dịch thanh toán" : "Từ chối giao dịch thanh toán",
                    "Finance",
                    $"OrderCode: {baseCode}",
                    oldValues: oldValues,
                    newValues: newValues,
                    severity: status == "Success" ? "Info" : "Warning");

                TempData["Success"] = status == "Success"
                    ? $"Đã duyệt đơn hàng #{baseCode} thành công."
                    : $"Đã từ chối đơn hàng #{baseCode}.";
            }
            catch (Exception ex)
            {
                await dbTransaction.RollbackAsync();

                await _auditLogService.LogAsync(
                    adminId,
                    "Lỗi xử lý giao dịch thanh toán",
                    "Finance",
                    $"OrderCode: {baseCode}",
                    oldValues: oldValues,
                    newValues: ex.Message,
                    severity: "Danger");

                TempData["Error"] = "Lỗi hệ thống khi xử lý giao dịch: " + ex.Message;
            }

            return RedirectToAction(nameof(Index));
        }

        // =====================================================
        // XUẤT CSV
        // =====================================================
        [HttpGet]
        public async Task<IActionResult> ExportCsv(
            string? searchString,
            string? statusFilter,
            DateTime? startDate,
            DateTime? endDate)
        {
            searchString = searchString?.Trim();
            statusFilter = string.IsNullOrWhiteSpace(statusFilter) ? "All" : statusFilter.Trim();

            var query = _context.Transactions
                .Include(t => t.User)
                .Include(t => t.Package)
                .Where(t => t.PaymentMethod != "System Gift")
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(searchString))
            {
                string searchLower = searchString.ToLower();

                query = query.Where(t =>
                    t.TransactionCode.ToLower().Contains(searchLower) ||
                    (t.User != null && t.User.FullName != null && t.User.FullName.ToLower().Contains(searchLower)) ||
                    (t.User != null && t.User.Username != null && t.User.Username.ToLower().Contains(searchLower)) ||
                    (t.User != null && t.User.Phone != null && t.User.Phone.Contains(searchString)));
            }

            if (!string.IsNullOrWhiteSpace(statusFilter) && statusFilter != "All")
            {
                query = query.Where(t => t.Status == statusFilter);
            }

            if (startDate.HasValue)
            {
                query = query.Where(t => t.CreatedAt >= startDate.Value.Date);
            }

            if (endDate.HasValue)
            {
                DateTime endOfDay = endDate.Value.Date.AddDays(1).AddTicks(-1);
                query = query.Where(t => t.CreatedAt <= endOfDay);
            }

            var data = await query
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync();

            var builder = new StringBuilder();

            builder.Append('\uFEFF');
            builder.AppendLine("\"Mã giao dịch\",\"Mã đơn\",\"Thời gian\",\"Khách hàng\",\"SĐT\",\"Email\",\"Dịch vụ\",\"Số lượng\",\"Số tiền (VNĐ)\",\"Phương thức\",\"Trạng thái\",\"Biên lai\"");

            foreach (var item in data)
            {
                builder.AppendLine(string.Join(",",
                    CsvText(item.TransactionCode),
                    CsvText(GetBaseCode(item.TransactionCode)),
                    CsvText(item.CreatedAt.ToString("dd/MM/yyyy HH:mm")),
                    CsvText(item.User?.FullName ?? item.User?.Username ?? "Không rõ"),
                    CsvText(item.User?.Phone ?? ""),
                    CsvText(item.User?.Email ?? ""),
                    CsvText(item.Package?.PackageName ?? "Gói đã xóa/không xác định"),
                    CsvText(item.Quantity.ToString()),
                    CsvText(item.Amount.ToString("F0", CultureInfo.InvariantCulture)),
                    CsvText(GetPaymentMethodText(item.PaymentMethod)),
                    CsvText(GetStatusText(item.Status)),
                    CsvText(item.BillImageUrl ?? "")
                ));
            }

            int adminId = GetCurrentAdminId();

            await _auditLogService.LogAsync(
                adminId,
                "Xuất báo cáo tài chính CSV",
                "Finance",
                "Export CSV",
                oldValues: null,
                newValues: JsonSerializer.Serialize(new
                {
                    Search = searchString,
                    Status = statusFilter,
                    StartDate = startDate?.ToString("yyyy-MM-dd"),
                    EndDate = endDate?.ToString("yyyy-MM-dd"),
                    TotalRows = data.Count,
                    ExportedAt = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss")
                }, JsonOptions()),
                severity: "Info");

            string fileName = $"Bao_Cao_Tai_Chinh_{DateTime.Now:yyyyMMdd_HHmm}.csv";

            return File(
                Encoding.UTF8.GetBytes(builder.ToString()),
                "text/csv; charset=utf-8",
                fileName);
        }

        // =====================================================
        // HÀM ÁP DỤNG GÓI SAU KHI THANH TOÁN THÀNH CÔNG
        // =====================================================
        private async Task ApplyPackageAfterSuccessAsync(Transaction transaction)
        {
            if (transaction.PropertyID == null || transaction.Package == null)
            {
                return;
            }

            var property = await _context.Properties
                .FirstOrDefaultAsync(p => p.PropertyID == transaction.PropertyID.Value);

            if (property == null)
            {
                return;
            }

            property.Status = "Approved";
            property.PackageID = transaction.PackageID ?? property.PackageID;
            property.IsAutoApproved = false;
            property.ApprovedAt ??= DateTime.Now;
            property.UpdatedAt = DateTime.Now;

            int durationDays = transaction.Package.DurationDays * Math.Max(transaction.Quantity, 1);

            if (durationDays > 0)
            {
                DateTime baseDate = property.VipExpiryDate.HasValue && property.VipExpiryDate.Value > DateTime.Now
                    ? property.VipExpiryDate.Value
                    : DateTime.Now;

                property.VipExpiryDate = baseDate.AddDays(durationDays);
            }
        }

        // =====================================================
        // HELPER
        // =====================================================
        private static string GetBaseCode(string? code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return "";
            }

            int lastSeparator = code.LastIndexOf("_", StringComparison.Ordinal);

            return lastSeparator > 0
                ? code.Substring(0, lastSeparator)
                : code;
        }

        private int GetCurrentAdminId()
        {
            string? adminIdText = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (int.TryParse(adminIdText, out int adminId))
            {
                return adminId;
            }

            return 0;
        }

        private static string GetPaymentMethodText(string? paymentMethod)
        {
            return paymentMethod switch
            {
                "BankTransfer" => "Chuyển khoản",
                "Momo" => "Ví MoMo",
                "VNPay" => "VNPay",
                "Cash" => "Tiền mặt",
                "System Gift" => "Hệ thống tặng",
                null or "" => "Chưa xác định",
                _ => paymentMethod
            };
        }

        private static string GetStatusText(string? status)
        {
            return status switch
            {
                "Success" => "Thành công",
                "Completed" => "Hoàn tất",
                "Pending" => "Chờ duyệt",
                "Rejected" => "Bị từ chối",
                "Failed" => "Thất bại",
                "Cancelled" => "Đã hủy",
                null or "" => "Chưa xác định",
                _ => status
            };
        }

        private static string CsvText(string? value)
        {
            value ??= "";
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        private static JsonSerializerOptions JsonOptions()
        {
            return new JsonSerializerOptions
            {
                WriteIndented = true
            };
        }

        private static string BuildTransactionAuditJson(
            List<Transaction> transactions,
            string note,
            string? adminNote = null)
        {
            var data = new
            {
                GhiChu = note,
                GhiChuAdmin = adminNote,
                ThoiGian = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"),
                TongSoDong = transactions.Count,
                TongTien = transactions.Sum(t => t.Amount),
                GiaoDich = transactions.Select(t => new
                {
                    t.TransactionID,
                    t.TransactionCode,
                    BaseCode = GetBaseCode(t.TransactionCode),
                    t.UserID,
                    NguoiDung = t.User?.FullName ?? t.User?.Username,
                    SoDienThoai = t.User?.Phone,
                    Email = t.User?.Email,
                    t.PackageID,
                    GoiTin = t.Package?.PackageName,
                    LoaiGoi = t.Package?.PackageType,
                    t.PropertyID,
                    TinDang = t.Property?.Title,
                    t.Amount,
                    t.Quantity,
                    t.PaymentMethod,
                    PhuongThucThanhToan = GetPaymentMethodText(t.PaymentMethod),
                    t.Status,
                    TrangThai = GetStatusText(t.Status),
                    t.BillImageUrl,
                    CreatedAt = t.CreatedAt.ToString("dd/MM/yyyy HH:mm:ss")
                })
            };

            return JsonSerializer.Serialize(data, JsonOptions());
        }
    }
}