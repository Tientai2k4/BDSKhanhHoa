using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BDSKhanhHoa.Controllers
{
    public class CartItemRequest
    {
        public int PackageId { get; set; }
        public int Quantity { get; set; }
    }

    public class VoucherRequest
    {
        public string Code { get; set; } = "";
        public string CartData { get; set; } = "";
    }

    [Authorize]
    public class PaymentController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _env;

        private const int PaymentExpireMinutes = 15;

        public PaymentController(
            ApplicationDbContext context,
            IConfiguration configuration,
            IWebHostEnvironment env)
        {
            _context = context;
            _configuration = configuration;
            _env = env;
        }

        [HttpGet]
        public async Task<IActionResult> Checkout(int? packageId)
        {
            await CloseExpiredPendingTransactionsForCurrentUserAsync();

            if (packageId.HasValue)
            {
                bool packageIsActive = await _context.PostServicePackages
                    .AsNoTracking()
                    .AnyAsync(p => p.PackageID == packageId.Value && p.IsActive);

                if (!packageIsActive)
                {
                    TempData["Error"] = "Gói đăng tin này hiện đã ngừng sử dụng, không thể thanh toán mới.";
                    return RedirectToAction("Buy", "Package");
                }
            }

            ViewBag.DirectPackageId = packageId;

            var allPackages = await _context.PostServicePackages
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.PriorityLevel)
                .ThenBy(p => p.Price)
                .ToListAsync();

            return View(allPackages);
        }

        [HttpPost]
        public async Task<IActionResult> ApplyVoucher([FromBody] VoucherRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.CartData))
            {
                return Json(new
                {
                    success = false,
                    message = "Giỏ hàng trống."
                });
            }

            List<CartItemRequest>? cartItems;

            try
            {
                cartItems = JsonSerializer.Deserialize<List<CartItemRequest>>(req.CartData);
            }
            catch
            {
                return Json(new
                {
                    success = false,
                    message = "Dữ liệu giỏ hàng không hợp lệ."
                });
            }

            if (cartItems == null || !cartItems.Any())
            {
                return Json(new
                {
                    success = false,
                    message = "Giỏ hàng lỗi."
                });
            }

            cartItems = cartItems
                .Where(x => x.PackageId > 0)
                .Select(x => new CartItemRequest
                {
                    PackageId = x.PackageId,
                    Quantity = x.Quantity > 0 ? x.Quantity : 1
                })
                .ToList();

            if (!cartItems.Any())
            {
                return Json(new
                {
                    success = false,
                    message = "Giỏ hàng không có gói hợp lệ."
                });
            }

            var packageIds = cartItems
                .Select(x => x.PackageId)
                .Distinct()
                .ToList();

            var activePackages = await _context.PostServicePackages
                .AsNoTracking()
                .Where(p => packageIds.Contains(p.PackageID) && p.IsActive)
                .ToListAsync();

            var activePackageIds = activePackages
                .Select(p => p.PackageID)
                .ToHashSet();

            bool hasInactiveOrDeletedPackage = cartItems.Any(x => !activePackageIds.Contains(x.PackageId));

            if (hasInactiveOrDeletedPackage)
            {
                return Json(new
                {
                    success = false,
                    message = "Giỏ hàng có gói đã ngừng sử dụng. Vui lòng xóa gói đó khỏi giỏ và chọn lại."
                });
            }

            decimal totalBeforeDiscount = 0;

            foreach (var item in cartItems)
            {
                var package = activePackages.FirstOrDefault(p => p.PackageID == item.PackageId);

                if (package != null)
                {
                    totalBeforeDiscount += package.Price * item.Quantity;
                }
            }

            if (totalBeforeDiscount <= 0)
            {
                return Json(new
                {
                    success = true,
                    discountAmount = 0,
                    finalPrice = 0,
                    message = ""
                });
            }

            if (string.IsNullOrWhiteSpace(req.Code))
            {
                return Json(new
                {
                    success = true,
                    discountAmount = 0,
                    finalPrice = totalBeforeDiscount,
                    message = ""
                });
            }

            string cleanCode = req.Code.Trim();

            var voucher = await _context.Vouchers
                .FirstOrDefaultAsync(v => v.Code.ToLower() == cleanCode.ToLower());

            if (voucher == null || !voucher.IsActive)
            {
                return Json(new
                {
                    success = false,
                    message = "Mã giảm giá không tồn tại hoặc đã bị khóa."
                });
            }

            if (DateTime.Now < voucher.StartDate)
            {
                return Json(new
                {
                    success = false,
                    message = $"Voucher chỉ có hiệu lực từ {voucher.StartDate:dd/MM/yyyy HH:mm}."
                });
            }

            if (DateTime.Now > voucher.ExpiryDate)
            {
                return Json(new
                {
                    success = false,
                    message = "Mã giảm giá đã hết hạn."
                });
            }

            if (voucher.UsedCount >= voucher.Quantity)
            {
                return Json(new
                {
                    success = false,
                    message = "Mã giảm giá đã hết lượt sử dụng."
                });
            }

            if (totalBeforeDiscount < voucher.MinOrderAmount)
            {
                return Json(new
                {
                    success = false,
                    message = $"Đơn hàng tối thiểu để áp dụng là {voucher.MinOrderAmount:N0}đ."
                });
            }

            int userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            string expectedDesc = $"[Voucher:{cleanCode.ToUpper()}]";

            bool hasUsed = await _context.Transactions.AnyAsync(t =>
                t.UserID == userId &&
                t.Description != null &&
                t.Description.Contains(expectedDesc) &&
                t.Status != "Cancelled" &&
                t.Status != "Failed");

            if (hasUsed)
            {
                return Json(new
                {
                    success = false,
                    message = "Bạn đã sử dụng mã này rồi. Mỗi tài khoản chỉ được dùng 1 lần!"
                });
            }

            decimal discountAmount = totalBeforeDiscount * voucher.DiscountPercent / 100;

            if (discountAmount > voucher.MaxDiscountAmount)
            {
                discountAmount = voucher.MaxDiscountAmount;
            }

            decimal finalPrice = totalBeforeDiscount - discountAmount;

            if (finalPrice < 0)
            {
                finalPrice = 0;
            }

            return Json(new
            {
                success = true,
                discountAmount,
                finalPrice,
                message = $"Áp dụng thành công! Giảm {voucher.DiscountPercent}%"
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcessPayment(string cartData, string paymentMethod, string? voucherCode)
        {
            await CloseExpiredPendingTransactionsForCurrentUserAsync();

            if (string.IsNullOrWhiteSpace(cartData))
            {
                TempData["Error"] = "Giỏ hàng trống!";
                return RedirectToAction("Buy", "Package");
            }

            List<CartItemRequest>? cartItems;

            try
            {
                cartItems = JsonSerializer.Deserialize<List<CartItemRequest>>(cartData);
            }
            catch
            {
                TempData["Error"] = "Dữ liệu giỏ hàng không hợp lệ!";
                return RedirectToAction("Buy", "Package");
            }

            if (cartItems == null || !cartItems.Any())
            {
                TempData["Error"] = "Lỗi dữ liệu giỏ hàng!";
                return RedirectToAction("Buy", "Package");
            }

            cartItems = cartItems
                .Where(x => x.PackageId > 0)
                .Select(x => new CartItemRequest
                {
                    PackageId = x.PackageId,
                    Quantity = x.Quantity > 0 ? x.Quantity : 1
                })
                .ToList();

            if (!cartItems.Any())
            {
                TempData["Error"] = "Giỏ hàng không có gói hợp lệ!";
                return RedirectToAction("Buy", "Package");
            }

            if (string.IsNullOrWhiteSpace(paymentMethod))
            {
                TempData["Error"] = "Vui lòng chọn phương thức thanh toán.";
                return RedirectToAction("Checkout");
            }

            int userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

            var packageIds = cartItems
                .Select(x => x.PackageId)
                .Distinct()
                .ToList();

            var activePackages = await _context.PostServicePackages
                .Where(p => packageIds.Contains(p.PackageID) && p.IsActive)
                .ToListAsync();

            var activePackageIds = activePackages
                .Select(p => p.PackageID)
                .ToHashSet();

            bool hasInactiveOrDeletedPackage = cartItems.Any(x => !activePackageIds.Contains(x.PackageId));

            if (hasInactiveOrDeletedPackage)
            {
                TempData["Error"] = "Giỏ hàng có gói đã ngừng sử dụng hoặc không còn tồn tại. Vui lòng xóa gói đó khỏi giỏ và chọn lại.";
                return RedirectToAction("Checkout");
            }

            decimal totalOriginalPrice = 0;
            var validItems = new List<(PostServicePackage pkg, int qty)>();

            foreach (var item in cartItems)
            {
                var pkg = activePackages.FirstOrDefault(p => p.PackageID == item.PackageId);

                if (pkg != null)
                {
                    int qty = item.Quantity > 0 ? item.Quantity : 1;
                    validItems.Add((pkg, qty));
                    totalOriginalPrice += pkg.Price * qty;
                }
            }

            if (!validItems.Any())
            {
                TempData["Error"] = "Không tìm thấy gói đang sử dụng trong giỏ hàng.";
                return RedirectToAction("Buy", "Package");
            }

            decimal finalPrice = totalOriginalPrice;
            decimal totalDiscount = 0;
            string txDescription = "Mua lượt đăng";

            if (!string.IsNullOrWhiteSpace(voucherCode))
            {
                string cleanVoucherCode = voucherCode.Trim();

                var voucher = await _context.Vouchers.FirstOrDefaultAsync(v =>
                    v.Code.ToLower() == cleanVoucherCode.ToLower() &&
                    v.IsActive);

                if (voucher != null &&
                    DateTime.Now >= voucher.StartDate &&
                    DateTime.Now <= voucher.ExpiryDate &&
                    voucher.UsedCount < voucher.Quantity &&
                    finalPrice >= voucher.MinOrderAmount)
                {
                    string expectedDesc = $"[Voucher:{cleanVoucherCode.ToUpper()}]";

                    bool hasUsed = await _context.Transactions.AnyAsync(t =>
                        t.UserID == userId &&
                        t.Description != null &&
                        t.Description.Contains(expectedDesc) &&
                        t.Status != "Cancelled" &&
                        t.Status != "Failed");

                    if (hasUsed)
                    {
                        TempData["Error"] = "Thanh toán thất bại! Bạn đã sử dụng mã Voucher này trước đó.";
                        return RedirectToAction("Checkout");
                    }

                    totalDiscount = finalPrice * voucher.DiscountPercent / 100;

                    if (totalDiscount > voucher.MaxDiscountAmount)
                    {
                        totalDiscount = voucher.MaxDiscountAmount;
                    }

                    finalPrice -= totalDiscount;

                    if (finalPrice < 0)
                    {
                        finalPrice = 0;
                    }

                    voucher.UsedCount += 1;
                    _context.Update(voucher);

                    txDescription = $"{expectedDesc} Mua lượt đăng";
                }
            }

            string txCodeBase = DateTime.Now.ToString("yyyyMMddHHmmssfff") + userId;
            DateTime now = DateTime.Now;
            DateTime expiresAt = now.AddMinutes(PaymentExpireMinutes);

            for (int i = 0; i < validItems.Count; i++)
            {
                var item = validItems[i];

                decimal itemOriginalTotal = item.pkg.Price * item.qty;
                decimal itemRatio = totalOriginalPrice > 0
                    ? itemOriginalTotal / totalOriginalPrice
                    : 0;

                decimal itemDiscount = totalDiscount * itemRatio;
                decimal itemFinalPrice = itemOriginalTotal - itemDiscount;

                if (itemFinalPrice < 0)
                {
                    itemFinalPrice = 0;
                }

                var transaction = new Transaction
                {
                    UserID = userId,
                    PackageID = item.pkg.PackageID,
                    Quantity = item.qty,
                    Amount = itemFinalPrice,
                    PaymentMethod = paymentMethod,
                    TransactionCode = txCodeBase + "_" + i,
                    Status = "Pending",
                    Type = "Mua lượt đăng",
                    Description = $"{txDescription} - {item.qty} lượt - {item.pkg.PackageName}",
                    CreatedAt = now,
                    ExpiresAt = expiresAt,
                    CancelledAt = null,
                    BillImageUrl = null
                };

                _context.Transactions.Add(transaction);
            }

            await _context.SaveChangesAsync();

            if (finalPrice <= 0)
            {
                await ConfirmPayment(txCodeBase);
                TempData["Success"] = "Đơn hàng đã được thanh toán thành công.";
                return RedirectToAction("History");
            }

            switch (paymentMethod)
            {
                case "VNPay":
                    return Redirect(CreateVnPayPaymentUrl(txCodeBase, finalPrice));

                case "PayOS":
                    return RedirectToAction("CreatePayOSPayment", new
                    {
                        baseCode = txCodeBase
                    });

                case "SePay":
                    return RedirectToAction("SePayInfo", new
                    {
                        baseCode = txCodeBase
                    });

                case "BankTransfer":
                    return RedirectToAction("TransferInfo", new
                    {
                        baseCode = txCodeBase,
                        amount = finalPrice
                    });

                case "Cash":
                    TempData["Success"] = "Đã ghi nhận đơn thanh toán tiền mặt. Vui lòng liên hệ quản trị viên để xác nhận.";
                    return RedirectToAction("History");

                default:
                    TempData["Error"] = "Phương thức thanh toán không hợp lệ.";
                    return RedirectToAction("Checkout");
            }
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> PaymentCallback()
        {
            string hashSecret = _configuration["VnPay:HashSecret"] ?? _configuration["VNPay:HashSecret"] ?? "";

            if (string.IsNullOrWhiteSpace(hashSecret))
            {
                TempData["Error"] = "Chưa cấu hình VNPay HashSecret.";
                return RedirectToAction("History");
            }

            var vnpParams = Request.Query
                .Where(x =>
                    x.Key.StartsWith("vnp_") &&
                    x.Key != "vnp_SecureHash" &&
                    x.Key != "vnp_SecureHashType")
                .ToDictionary(x => x.Key, x => x.Value.ToString());

            var sortedParams = new SortedDictionary<string, string>(vnpParams);

            string hashData = string.Join("&", sortedParams.Select(kvp =>
                $"{WebUtility.UrlEncode(kvp.Key)}={WebUtility.UrlEncode(kvp.Value)}"));

            string secureHash = Request.Query["vnp_SecureHash"].ToString();
            string checkHash = HmacSHA512(hashSecret, hashData);

            if (!string.Equals(secureHash, checkHash, StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Chữ ký VNPay không hợp lệ. Giao dịch không được xác nhận.";
                return RedirectToAction("History");
            }

            string responseCode = Request.Query["vnp_ResponseCode"].ToString();
            string transactionStatus = Request.Query["vnp_TransactionStatus"].ToString();
            string baseCode = Request.Query["vnp_TxnRef"].ToString();
            string vnpTransactionNo = Request.Query["vnp_TransactionNo"].ToString();
            string bankCode = Request.Query["vnp_BankCode"].ToString();
            string payDate = Request.Query["vnp_PayDate"].ToString();

            if (string.IsNullOrWhiteSpace(baseCode))
            {
                TempData["Error"] = "Không tìm thấy mã đơn hàng từ VNPay.";
                return RedirectToAction("History");
            }

            var transactions = await _context.Transactions
                .Where(t =>
                    t.TransactionCode != null &&
                    (
                        t.TransactionCode == baseCode ||
                        t.TransactionCode.StartsWith(baseCode + "_")
                    ) &&
                    t.PaymentMethod == "VNPay")
                .ToListAsync();

            if (!transactions.Any())
            {
                TempData["Error"] = "Không tìm thấy đơn hàng VNPay trong hệ thống.";
                return RedirectToAction("History");
            }

            if (responseCode == "00" && transactionStatus == "00")
            {
                foreach (var tx in transactions)
                {
                    if (tx.Status == "Pending")
                    {
                        tx.Status = "Success";
                        tx.CancelledAt = null;
                        tx.Description = (tx.Description ?? "") +
                            $" | VNPay thanh toán thành công. Mã GD VNPay: {vnpTransactionNo}. Ngân hàng: {bankCode}. PayDate: {payDate}";
                    }
                }

                await _context.SaveChangesAsync();

                TempData["Success"] = "Thanh toán VNPay thành công.";
                return RedirectToAction("History");
            }

            foreach (var tx in transactions)
            {
                if (tx.Status == "Pending")
                {
                    tx.Status = "Failed";
                    tx.CancelledAt = DateTime.Now;
                    tx.Description = (tx.Description ?? "") +
                        $" | VNPay thanh toán thất bại hoặc người dùng hủy. ResponseCode: {responseCode}. TransactionStatus: {transactionStatus}";
                }
            }

            await _context.SaveChangesAsync();

            TempData["Error"] = "Thanh toán VNPay không thành công hoặc đã bị hủy.";
            return RedirectToAction("History");
        }

        [HttpGet]
        public async Task<IActionResult> PayAgain(string baseCode)
        {
            if (string.IsNullOrWhiteSpace(baseCode))
            {
                TempData["Error"] = "Mã đơn hàng không hợp lệ.";
                return RedirectToAction("History");
            }

            int userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

            var transactions = await GetPendingTransactionsByBaseCodeAsync(userId, baseCode);

            if (!transactions.Any())
            {
                TempData["Error"] = "Không tìm thấy đơn chờ thanh toán hoặc đơn đã được xử lý.";
                return RedirectToAction("History");
            }

            bool isExpired = await CloseOrderIfExpiredAsync(transactions);

            if (isExpired)
            {
                TempData["Error"] = "Đơn hàng đã quá thời gian thanh toán và đã bị hủy. Vui lòng tạo đơn mới.";
                return RedirectToAction("History");
            }

            decimal amount = transactions.Sum(t => t.Amount);

            if (amount < 0)
            {
                amount = 0;
            }

            string paymentMethod = transactions.First().PaymentMethod ?? "BankTransfer";

            switch (paymentMethod)
            {
                case "VNPay":
                    return Redirect(CreateVnPayPaymentUrl(baseCode, amount));

                case "PayOS":
                    return RedirectToAction("CreatePayOSPayment", new
                    {
                        baseCode
                    });

                case "SePay":
                    return RedirectToAction("SePayInfo", new
                    {
                        baseCode
                    });

                case "BankTransfer":
                    return RedirectToAction("TransferInfo", new
                    {
                        baseCode,
                        amount
                    });

                case "Cash":
                    TempData["Info"] = "Đơn tiền mặt cần liên hệ quản trị viên để xác nhận.";
                    return RedirectToAction("History");

                default:
                    TempData["Error"] = "Phương thức thanh toán của đơn hàng không hợp lệ.";
                    return RedirectToAction("History");
            }
        }

        [HttpGet]
        public async Task<IActionResult> TransferInfo(string baseCode, decimal? amount)
        {
            if (string.IsNullOrWhiteSpace(baseCode))
            {
                TempData["Error"] = "Mã thanh toán không hợp lệ.";
                return RedirectToAction("History");
            }

            int userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

            var txns = await GetPendingTransactionsByBaseCodeAsync(userId, baseCode);

            if (!txns.Any())
            {
                TempData["Error"] = "Không tìm thấy đơn hàng đang chờ thanh toán.";
                return RedirectToAction("History");
            }

            bool isExpired = await CloseOrderIfExpiredAsync(txns);

            if (isExpired)
            {
                TempData["Error"] = "Đơn hàng đã quá thời gian thanh toán và đã bị hủy. Vui lòng tạo đơn mới.";
                return RedirectToAction("History");
            }

            decimal realAmount = txns.Sum(t => t.Amount);

            if (amount.HasValue && amount.Value > 0 && amount.Value == realAmount)
            {
                realAmount = amount.Value;
            }

            var bankAccount = await _context.BankAccounts
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.IsActive);

            if (bankAccount == null)
            {
                TempData["Error"] = "Chưa cấu hình tài khoản ngân hàng nhận tiền.";
                return RedirectToAction("History");
            }

            DateTime createdAt = txns.Min(t => t.CreatedAt);
            DateTime expireAt = txns.Min(t => t.ExpiresAt ?? t.CreatedAt.AddMinutes(PaymentExpireMinutes));

            ViewBag.BaseCode = baseCode;
            ViewBag.Amount = realAmount;
            ViewBag.CreatedAt = createdAt;
            ViewBag.ExpireAt = expireAt;
            ViewBag.PaymentExpireMinutes = PaymentExpireMinutes;

            return View(bankAccount);
        }

        [HttpGet]
        public async Task<IActionResult> CloseExpiredPayment(string baseCode)
        {
            if (string.IsNullOrWhiteSpace(baseCode))
            {
                TempData["Error"] = "Mã thanh toán không hợp lệ.";
                return RedirectToAction("History");
            }

            int userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

            var txns = await GetPendingTransactionsByBaseCodeAsync(userId, baseCode);

            if (!txns.Any())
            {
                TempData["Info"] = "Đơn hàng không còn ở trạng thái chờ thanh toán.";
                return RedirectToAction("History");
            }

            bool isExpired = await CloseOrderIfExpiredAsync(txns, forceClose: true);

            if (isExpired)
            {
                TempData["Error"] = "Đơn hàng đã hết thời gian thanh toán và đã được hủy.";
            }
            else
            {
                TempData["Info"] = "Đơn hàng vẫn còn thời gian thanh toán.";
            }

            return RedirectToAction("History");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitBill(string baseCode, IFormFile billImage)
        {
            if (string.IsNullOrWhiteSpace(baseCode))
            {
                TempData["Error"] = "Mã thanh toán không hợp lệ.";
                return RedirectToAction("History");
            }

            int userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

            var txns = await GetPendingTransactionsByBaseCodeAsync(userId, baseCode);

            if (!txns.Any())
            {
                TempData["Error"] = "Không tìm thấy đơn hàng đang chờ thanh toán.";
                return RedirectToAction("History");
            }

            bool isExpired = await CloseOrderIfExpiredAsync(txns);

            if (isExpired)
            {
                TempData["Error"] = "Đơn hàng đã quá thời gian thanh toán và đã bị hủy. Không thể gửi biên lai.";
                return RedirectToAction("History");
            }

            if (billImage == null || billImage.Length <= 0)
            {
                TempData["Error"] = "Vui lòng đính kèm hình ảnh biên lai trước khi xác nhận.";
                return RedirectToAction("PayAgain", new { baseCode });
            }

            if (billImage.Length > 5 * 1024 * 1024)
            {
                TempData["Error"] = "Ảnh biên lai tối đa 5MB.";
                return RedirectToAction("PayAgain", new { baseCode });
            }

            string ext = Path.GetExtension(billImage.FileName).ToLowerInvariant();

            if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".webp")
            {
                TempData["Error"] = "Chỉ chấp nhận file hình ảnh JPG, PNG hoặc WEBP.";
                return RedirectToAction("PayAgain", new { baseCode });
            }

            string uploadsFolder = Path.Combine(_env.WebRootPath, "uploads", "bills");

            if (!Directory.Exists(uploadsFolder))
            {
                Directory.CreateDirectory(uploadsFolder);
            }

            string uniqueFileName = $"{baseCode}_{DateTime.Now:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}{ext}";
            string filePath = Path.Combine(uploadsFolder, uniqueFileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await billImage.CopyToAsync(stream);
            }

            string dbImageUrl = "/uploads/bills/" + uniqueFileName;

            foreach (var tx in txns)
            {
                tx.BillImageUrl = dbImageUrl;
                tx.PaymentMethod = "BankTransfer";
                tx.Description = (tx.Description ?? "") + $" | Đã gửi biên lai: {DateTime.Now:dd/MM/yyyy HH:mm}";
            }

            await _context.SaveChangesAsync();

            TempData["Success"] = "Đã gửi biên lai thành công. Đơn hàng đang chờ Admin xác nhận.";
            return RedirectToAction("History");
        }

        [HttpGet]
        public async Task<IActionResult> History(string status = "All", string? fromDate = null, string? toDate = null)
        {
            await CloseExpiredPendingTransactionsForCurrentUserAsync();

            int userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

            var query = _context.Transactions
                .Include(t => t.Package)
                .Where(t => t.UserID == userId)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(status) && status != "All")
            {
                query = query.Where(t => t.Status == status);
            }

            if (DateTime.TryParse(fromDate, out DateTime from))
            {
                query = query.Where(t => t.CreatedAt.Date >= from.Date);
            }

            if (DateTime.TryParse(toDate, out DateTime to))
            {
                query = query.Where(t => t.CreatedAt.Date <= to.Date);
            }

            var transactions = await query
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync();

            ViewBag.Status = status;
            ViewBag.FromDate = fromDate;
            ViewBag.ToDate = toDate;
            ViewBag.PaymentExpireMinutes = PaymentExpireMinutes;

            return View(transactions);
        }

        [HttpGet]
        public async Task<IActionResult> Invoice(string baseCode)
        {
            if (string.IsNullOrWhiteSpace(baseCode))
            {
                TempData["Error"] = "Mã hóa đơn không hợp lệ.";
                return RedirectToAction("History");
            }

            await CloseExpiredPendingTransactionsForCurrentUserAsync();

            int userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

            var transactions = await _context.Transactions
                .Include(t => t.Package)
                .Include(t => t.User)
                .Where(t =>
                    t.UserID == userId &&
                    t.TransactionCode != null &&
                    (
                        t.TransactionCode == baseCode ||
                        t.TransactionCode.StartsWith(baseCode + "_")
                    ))
                .OrderBy(t => t.TransactionCode)
                .ToListAsync();

            if (!transactions.Any())
            {
                TempData["Error"] = "Không tìm thấy hóa đơn.";
                return RedirectToAction("History");
            }

            ViewBag.BaseCode = baseCode;

            return View(transactions);
        }

        [HttpGet]
        public async Task<IActionResult> CreatePayOSPayment(string baseCode)
        {
            TempData["Info"] = "PayOS cần gọi API tạo link thanh toán riêng. Hiện tại chưa triển khai trong action này.";
            return RedirectToAction("History");
        }

        [HttpGet]
        public async Task<IActionResult> SePayInfo(string baseCode)
        {
            TempData["Info"] = "SePay cần cấu hình webhook đối soát riêng. Hiện tại chưa triển khai trong action này.";
            return RedirectToAction("History");
        }

        private string CreateVnPayPaymentUrl(string baseCode, decimal amount)
        {
            string vnpUrl = _configuration["VnPay:BaseUrl"] ?? _configuration["VNPay:BaseUrl"] ?? "";
            string tmnCode = _configuration["VnPay:TmnCode"] ?? _configuration["VNPay:TmnCode"] ?? "";
            string hashSecret = _configuration["VnPay:HashSecret"] ?? _configuration["VNPay:HashSecret"] ?? "";
            string returnUrl = _configuration["VnPay:ReturnUrl"] ?? _configuration["VNPay:ReturnUrl"] ?? "";

            if (string.IsNullOrWhiteSpace(vnpUrl) ||
                string.IsNullOrWhiteSpace(tmnCode) ||
                string.IsNullOrWhiteSpace(hashSecret) ||
                string.IsNullOrWhiteSpace(returnUrl))
            {
                throw new InvalidOperationException("Chưa cấu hình đầy đủ VNPay trong appsettings.json.");
            }

            string ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";

            if (ipAddress == "::1")
            {
                ipAddress = "127.0.0.1";
            }

            string createDate = DateTime.Now.ToString("yyyyMMddHHmmss");
            string expireDate = DateTime.Now.AddMinutes(PaymentExpireMinutes).ToString("yyyyMMddHHmmss");

            long vnpAmount = (long)Math.Round(amount * 100, 0);

            var vnpParams = new SortedDictionary<string, string>
            {
                { "vnp_Version", "2.1.0" },
                { "vnp_Command", "pay" },
                { "vnp_TmnCode", tmnCode },
                { "vnp_Amount", vnpAmount.ToString() },
                { "vnp_CreateDate", createDate },
                { "vnp_CurrCode", "VND" },
                { "vnp_IpAddr", ipAddress },
                { "vnp_Locale", "vn" },
                { "vnp_OrderInfo", $"Thanh toan don hang {baseCode}" },
                { "vnp_OrderType", "other" },
                { "vnp_ReturnUrl", returnUrl },
                { "vnp_TxnRef", baseCode },
                { "vnp_ExpireDate", expireDate }
            };

            string hashData = string.Join("&", vnpParams.Select(kvp =>
                $"{WebUtility.UrlEncode(kvp.Key)}={WebUtility.UrlEncode(kvp.Value)}"));

            string query = string.Join("&", vnpParams.Select(kvp =>
                $"{WebUtility.UrlEncode(kvp.Key)}={WebUtility.UrlEncode(kvp.Value)}"));

            string secureHash = HmacSHA512(hashSecret, hashData);

            return $"{vnpUrl}?{query}&vnp_SecureHash={secureHash}";
        }

        private async Task<List<Transaction>> GetPendingTransactionsByBaseCodeAsync(int userId, string baseCode)
        {
            var transactions = await _context.Transactions
                .Where(t =>
                    t.UserID == userId &&
                    t.TransactionCode != null &&
                    (
                        t.TransactionCode == baseCode ||
                        t.TransactionCode.StartsWith(baseCode + "_")
                    ) &&
                    t.Status == "Pending")
                .ToListAsync();

            return transactions;
        }

        private async Task<bool> CloseOrderIfExpiredAsync(List<Transaction> transactions, bool forceClose = false)
        {
            if (transactions == null || !transactions.Any())
            {
                return false;
            }

            DateTime expireAt = transactions
                .Min(t => t.ExpiresAt ?? t.CreatedAt.AddMinutes(PaymentExpireMinutes));

            bool isExpired = forceClose || DateTime.Now >= expireAt;

            if (!isExpired)
            {
                return false;
            }

            foreach (var tx in transactions.Where(t => t.Status == "Pending"))
            {
                tx.Status = "Cancelled";
                tx.CancelledAt = DateTime.Now;
                tx.Description = (tx.Description ?? "") + $" | Hủy tự động do quá hạn thanh toán lúc {DateTime.Now:dd/MM/yyyy HH:mm}";
            }

            await _context.SaveChangesAsync();

            return true;
        }

        private async Task CloseExpiredPendingTransactionsForCurrentUserAsync()
        {
            if (User.Identity?.IsAuthenticated != true)
            {
                return;
            }

            string? userIdText = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (!int.TryParse(userIdText, out int userId))
            {
                return;
            }

            DateTime now = DateTime.Now;
            DateTime expiredBefore = now.AddMinutes(-PaymentExpireMinutes);

            var expiredTransactions = await _context.Transactions
                .Where(t =>
                    t.UserID == userId &&
                    t.Status == "Pending" &&
                    (
                        (t.ExpiresAt != null && t.ExpiresAt <= now) ||
                        (t.ExpiresAt == null && t.CreatedAt <= expiredBefore)
                    ))
                .ToListAsync();

            if (!expiredTransactions.Any())
            {
                return;
            }

            foreach (var tx in expiredTransactions)
            {
                tx.Status = "Cancelled";
                tx.CancelledAt = now;
                tx.Description = (tx.Description ?? "") + $" | Hủy tự động do quá hạn thanh toán lúc {now:dd/MM/yyyy HH:mm}";
            }

            await _context.SaveChangesAsync();
        }

        private async Task ConfirmPayment(string baseCode)
        {
            if (string.IsNullOrWhiteSpace(baseCode))
            {
                return;
            }

            var transactions = await _context.Transactions
                .Where(t =>
                    t.TransactionCode != null &&
                    (
                        t.TransactionCode == baseCode ||
                        t.TransactionCode.StartsWith(baseCode + "_")
                    ) &&
                    t.Status == "Pending")
                .ToListAsync();

            foreach (var t in transactions)
            {
                t.Status = "Success";
                t.CancelledAt = null;
                t.Description = (t.Description ?? "") + $" | Hệ thống xác nhận thanh toán lúc {DateTime.Now:dd/MM/yyyy HH:mm}";
            }

            await _context.SaveChangesAsync();
        }

        private string HmacSHA512(string key, string inputData)
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);

            using var hmac = new HMACSHA512(keyBytes);
            byte[] hashValue = hmac.ComputeHash(Encoding.UTF8.GetBytes(inputData));

            return BitConverter
                .ToString(hashValue)
                .Replace("-", "")
                .ToLower();
        }

        private string HmacSHA256(string key, string inputData)
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);

            using var hmac = new HMACSHA256(keyBytes);
            byte[] hashValue = hmac.ComputeHash(Encoding.UTF8.GetBytes(inputData));

            return BitConverter
                .ToString(hashValue)
                .Replace("-", "")
                .ToLower();
        }
    }
}