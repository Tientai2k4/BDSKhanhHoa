using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
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

    public class PayOSItemRequest
    {
        public string name { get; set; } = "";
        public int quantity { get; set; }
        public int price { get; set; }
    }

    public class PayOSCreatePaymentRequest
    {
        public long orderCode { get; set; }
        public int amount { get; set; }
        public string description { get; set; } = "";
        public List<PayOSItemRequest> items { get; set; } = new();
        public string cancelUrl { get; set; } = "";
        public string returnUrl { get; set; } = "";
        public int expiredAt { get; set; }
        public string signature { get; set; } = "";
    }

    [Authorize]
    public class PaymentController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _env;

        private const int PaymentExpireMinutes = 15;
        private const int DescriptionMaxLength = 500;

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

            paymentMethod = paymentMethod.Trim();

            if (paymentMethod != "VNPay" && paymentMethod != "PayOS")
            {
                TempData["Error"] = "Phương thức thanh toán không hợp lệ. Hệ thống chỉ hỗ trợ VNPay và PayOS / VietQR.";
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

            string txCodeBase = GeneratePaymentBaseCode();
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
                    Description = SafeText($"{txDescription} - {item.qty} lượt - {item.pkg.PackageName}"),
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
                    return RedirectToAction(nameof(CreatePayOSPayment), new
                    {
                        baseCode = txCodeBase
                    });

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
                        tx.Description = SafeAppendDescription(
                            tx.Description,
                            $" | VNPay thành công. Mã GD: {vnpTransactionNo}. Ngân hàng: {bankCode}."
                        );
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
                    tx.Description = SafeAppendDescription(
                        tx.Description,
                        $" | VNPay thất bại/hủy. ResponseCode: {responseCode}. Status: {transactionStatus}."
                    );
                }
            }

            await _context.SaveChangesAsync();

            TempData["Error"] = "Thanh toán VNPay không thành công hoặc đã bị hủy.";
            return RedirectToAction("History");
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> PayOSCallback()
        {
            string orderCodeText = Request.Query["orderCode"].ToString();
            string status = Request.Query["status"].ToString();
            string code = Request.Query["code"].ToString();
            string cancel = Request.Query["cancel"].ToString();

            if (string.IsNullOrWhiteSpace(orderCodeText))
            {
                TempData["Error"] = "PayOS không trả về mã đơn hàng.";
                return RedirectToAction("History");
            }

            try
            {
                var payOSStatus = await GetPayOSPaymentStatusAsync(orderCodeText);

                if (payOSStatus.IsPaid)
                {
                    decimal amountToConfirm = payOSStatus.AmountPaid;

                    if (amountToConfirm <= 0)
                    {
                        amountToConfirm = await GetExpectedAmountByBaseCodeAsync(orderCodeText, "PayOS");
                    }

                    bool confirmed = await ConfirmPayOSPaymentAsync(
                        orderCodeText,
                        amountToConfirm,
                        "PayOS ReturnUrl + API Status",
                        payOSStatus.Reference
                    );

                    TempData[confirmed ? "Success" : "Info"] = confirmed
                        ? "Thanh toán PayOS / VietQR thành công. Hệ thống đã tự động ghi nhận giao dịch."
                        : "PayOS báo đã thanh toán nhưng hệ thống chưa tìm thấy đơn tương ứng. Vui lòng liên hệ Admin.";

                    return RedirectToAction("History");
                }

                if (payOSStatus.IsCancelled ||
                    string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cancel, "true", StringComparison.OrdinalIgnoreCase))
                {
                    await CancelPayOSPaymentAsync(orderCodeText, "Người dùng hủy thanh toán hoặc PayOS trả trạng thái hủy.");
                    TempData["Error"] = "Thanh toán PayOS đã bị hủy.";
                    return RedirectToAction("History");
                }
            }
            catch
            {
                if (string.Equals(code, "00", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(status, "PAID", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(cancel, "true", StringComparison.OrdinalIgnoreCase))
                {
                    decimal expectedAmount = await GetExpectedAmountByBaseCodeAsync(orderCodeText, "PayOS");

                    bool confirmed = await ConfirmPayOSPaymentAsync(
                        orderCodeText,
                        expectedAmount,
                        "PayOS ReturnUrl Fallback",
                        "returnUrl"
                    );

                    if (confirmed)
                    {
                        TempData["Success"] = "Thanh toán PayOS / VietQR thành công. Hệ thống đã tự động ghi nhận giao dịch.";
                        return RedirectToAction("History");
                    }
                }
            }

            TempData["Info"] = "Giao dịch PayOS đang chờ xử lý. Nếu bạn đã thanh toán, hệ thống sẽ tự đồng bộ lại khi vào lịch sử giao dịch.";
            return RedirectToAction("History");
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> PayOSCancel()
        {
            string orderCodeText = Request.Query["orderCode"].ToString();

            if (!string.IsNullOrWhiteSpace(orderCodeText))
            {
                await CancelPayOSPaymentAsync(orderCodeText, "Người dùng hủy thanh toán trên PayOS.");
            }

            TempData["Error"] = "Bạn đã hủy thanh toán PayOS / VietQR.";
            return RedirectToAction("History");
        }

        [HttpPost]
        [AllowAnonymous]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> PayOSWebhook()
        {
            string rawBody;

            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            {
                rawBody = await reader.ReadToEndAsync();
            }

            if (string.IsNullOrWhiteSpace(rawBody))
            {
                return BadRequest("Empty webhook body");
            }

            JsonDocument doc;

            try
            {
                doc = JsonDocument.Parse(rawBody);
            }
            catch
            {
                return BadRequest("Invalid JSON");
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;

                if (!root.TryGetProperty("signature", out JsonElement signatureEl))
                {
                    return BadRequest("Missing signature");
                }

                if (!root.TryGetProperty("data", out JsonElement dataEl))
                {
                    return BadRequest("Missing data");
                }

                string receivedSignature = signatureEl.GetString() ?? "";
                string calculatedSignature = CreatePayOSWebhookSignature(dataEl);

                if (!string.Equals(receivedSignature, calculatedSignature, StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest("Invalid webhook signature");
                }

                string webhookCode = GetJsonString(root, "code");
                bool success = GetJsonBool(root, "success");

                long orderCode = GetJsonLong(dataEl, "orderCode");
                decimal paidAmount = GetJsonDecimal(dataEl, "amount");
                string reference = GetJsonString(dataEl, "reference");
                string dataCode = GetJsonString(dataEl, "code");
                string desc = GetJsonString(dataEl, "desc");

                if (orderCode <= 0)
                {
                    return BadRequest("Invalid orderCode");
                }

                string baseCode = orderCode.ToString();

                if (webhookCode == "00" && success && dataCode == "00")
                {
                    bool confirmed = await ConfirmPayOSPaymentAsync(
                        baseCode,
                        paidAmount,
                        "PayOS Webhook",
                        reference
                    );

                    return Ok(new
                    {
                        success = true,
                        message = confirmed
                            ? "Đã tự động xác nhận thanh toán PayOS."
                            : "Webhook hợp lệ nhưng đơn không còn ở trạng thái cần xử lý hoặc số tiền chưa khớp."
                    });
                }

                await CancelPayOSPaymentAsync(baseCode, $"PayOS webhook không thành công. Code: {webhookCode}. DataCode: {dataCode}. Desc: {desc}");

                return Ok(new
                {
                    success = true,
                    message = "Webhook đã nhận nhưng giao dịch không thành công."
                });
            }
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

            string paymentMethod = transactions.First().PaymentMethod ?? "PayOS";

            switch (paymentMethod)
            {
                case "VNPay":
                    return Redirect(CreateVnPayPaymentUrl(baseCode, amount));

                case "PayOS":
                    return RedirectToAction(nameof(CreatePayOSPayment), new
                    {
                        baseCode
                    });

                default:
                    TempData["Error"] = "Phương thức thanh toán của đơn hàng không hợp lệ.";
                    return RedirectToAction("History");
            }
        }


        [HttpGet]
        public async Task<IActionResult> History(string status = "All", string? fromDate = null, string? toDate = null)
        {
            int userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

            await SyncPendingPayOSOrdersForUserAsync(userId);

            // Tự hủy các đơn Pending quá hạn để người dùng tạo/thanh toán lại bằng VNPay hoặc PayOS.
            await CloseExpiredPendingTransactionsForCurrentUserAsync();

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
            if (string.IsNullOrWhiteSpace(baseCode))
            {
                TempData["Error"] = "Mã thanh toán PayOS / VietQR không hợp lệ.";
                return RedirectToAction("History");
            }

            int userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

            var txns = await GetPendingTransactionsByBaseCodeAsync(userId, baseCode);

            if (!txns.Any())
            {
                TempData["Error"] = "Không tìm thấy đơn hàng PayOS / VietQR đang chờ thanh toán.";
                return RedirectToAction("History");
            }

            bool isExpired = await CloseOrderIfExpiredAsync(txns);

            if (isExpired)
            {
                TempData["Error"] = "Đơn hàng đã quá thời gian thanh toán và đã bị hủy. Vui lòng tạo đơn mới.";
                return RedirectToAction("History");
            }

            decimal realAmount = txns.Sum(t => t.Amount);

            if (realAmount <= 0)
            {
                await ConfirmPayment(baseCode);
                TempData["Success"] = "Đơn hàng đã được thanh toán thành công.";
                return RedirectToAction("History");
            }

            if (!long.TryParse(baseCode, out long orderCode))
            {
                TempData["Error"] = "Mã đơn PayOS phải là dạng số. Vui lòng tạo lại đơn thanh toán.";
                return RedirectToAction("Checkout");
            }

            int payOSAmount = Convert.ToInt32(Math.Round(realAmount, 0));

            if (payOSAmount <= 0)
            {
                await ConfirmPayment(baseCode);
                TempData["Success"] = "Đơn hàng đã được thanh toán thành công.";
                return RedirectToAction("History");
            }

            try
            {
                string checkoutUrl = await CreatePayOSCheckoutUrlAsync(baseCode, orderCode, payOSAmount, txns);

                if (string.IsNullOrWhiteSpace(checkoutUrl))
                {
                    TempData["Error"] = "PayOS không trả về link thanh toán. Vui lòng kiểm tra cấu hình PayOS.";
                    return RedirectToAction("History");
                }

                return Redirect(checkoutUrl);
            }
            catch (Exception ex)
            {
                TempData["Error"] = "Không thể tạo thanh toán PayOS / VietQR. Chi tiết: " + ex.Message;
                return RedirectToAction("History");
            }
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
            return await _context.Transactions
                .Where(t =>
                    t.UserID == userId &&
                    t.TransactionCode != null &&
                    (
                        t.TransactionCode == baseCode ||
                        t.TransactionCode.StartsWith(baseCode + "_")
                    ) &&
                    t.Status == "Pending")
                .ToListAsync();
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
                tx.Description = SafeAppendDescription(
                    tx.Description,
                    $" | Hủy tự động do quá hạn lúc {DateTime.Now:dd/MM/yyyy HH:mm}"
                );
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
                tx.Description = SafeAppendDescription(
                    tx.Description,
                    $" | Hủy tự động do quá hạn lúc {now:dd/MM/yyyy HH:mm}"
                );
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
                t.Description = SafeAppendDescription(
                    t.Description,
                    $" | Hệ thống xác nhận lúc {DateTime.Now:dd/MM/yyyy HH:mm}"
                );
            }

            await _context.SaveChangesAsync();
        }

        private string GeneratePaymentBaseCode()
        {
            string unixMs = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString();
            int random = RandomNumberGenerator.GetInt32(10, 99);

            return unixMs + random.ToString();
        }

        private async Task<string> CreatePayOSCheckoutUrlAsync(
            string baseCode,
            long orderCode,
            int amount,
            List<Transaction> txns)
        {
            string clientId = _configuration["PayOS:ClientId"] ?? "";
            string apiKey = _configuration["PayOS:ApiKey"] ?? "";
            string checksumKey = _configuration["PayOS:ChecksumKey"] ?? "";
            string returnUrl = _configuration["PayOS:ReturnUrl"] ?? "";
            string cancelUrl = _configuration["PayOS:CancelUrl"] ?? "";

            if (string.IsNullOrWhiteSpace(clientId) ||
                string.IsNullOrWhiteSpace(apiKey) ||
                string.IsNullOrWhiteSpace(checksumKey) ||
                string.IsNullOrWhiteSpace(returnUrl) ||
                string.IsNullOrWhiteSpace(cancelUrl))
            {
                throw new InvalidOperationException("Chưa cấu hình đầy đủ PayOS trong appsettings.json.");
            }

            string description = baseCode;

            int expiredAt = (int)new DateTimeOffset(
                txns.Min(t => t.ExpiresAt ?? t.CreatedAt.AddMinutes(PaymentExpireMinutes))
            ).ToUnixTimeSeconds();

            var items = new List<PayOSItemRequest>
            {
                new PayOSItemRequest
                {
                    name = $"Don hang {baseCode}",
                    quantity = 1,
                    price = amount
                }
            };

            string signatureData =
                $"amount={amount}&cancelUrl={cancelUrl}&description={description}&orderCode={orderCode}&returnUrl={returnUrl}";

            string signature = HmacSHA256(checksumKey, signatureData);

            var requestModel = new PayOSCreatePaymentRequest
            {
                orderCode = orderCode,
                amount = amount,
                description = description,
                items = items,
                cancelUrl = cancelUrl,
                returnUrl = returnUrl,
                expiredAt = expiredAt,
                signature = signature
            };

            using var httpClient = new HttpClient();

            httpClient.DefaultRequestHeaders.Add("x-client-id", clientId);
            httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);

            string json = JsonSerializer.Serialize(requestModel);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await httpClient.PostAsync("https://api-merchant.payos.vn/v2/payment-requests", content);
            string responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"PayOS lỗi HTTP {(int)response.StatusCode}: {responseBody}");
            }

            using var doc = JsonDocument.Parse(responseBody);
            JsonElement root = doc.RootElement;

            string code = GetJsonString(root, "code");

            if (code != "00")
            {
                string desc = GetJsonString(root, "desc");
                throw new InvalidOperationException($"PayOS không tạo được link. Code: {code}. Desc: {desc}");
            }

            if (!root.TryGetProperty("data", out JsonElement data))
            {
                throw new InvalidOperationException("PayOS không trả về data.");
            }

            string checkoutUrl = GetJsonString(data, "checkoutUrl");

            foreach (var tx in txns)
            {
                tx.Description = SafeAppendDescription(
                    tx.Description,
                    $" | PayOS tạo link. OrderCode:{orderCode}."
                );
            }

            await _context.SaveChangesAsync();

            if (string.IsNullOrWhiteSpace(checkoutUrl))
            {
                throw new InvalidOperationException("PayOS không trả về checkoutUrl.");
            }

            return checkoutUrl;
        }

        private async Task<(bool IsPaid, bool IsCancelled, decimal AmountPaid, string Reference)> GetPayOSPaymentStatusAsync(string baseCode)
        {
            string clientId = _configuration["PayOS:ClientId"] ?? "";
            string apiKey = _configuration["PayOS:ApiKey"] ?? "";

            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("Chưa cấu hình ClientId hoặc ApiKey của PayOS.");
            }

            using var httpClient = new HttpClient();

            httpClient.DefaultRequestHeaders.Add("x-client-id", clientId);
            httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);

            using var response = await httpClient.GetAsync($"https://api-merchant.payos.vn/v2/payment-requests/{baseCode}");
            string responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Không lấy được trạng thái PayOS. HTTP {(int)response.StatusCode}: {responseBody}");
            }

            using var doc = JsonDocument.Parse(responseBody);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("data", out JsonElement data))
            {
                return (false, false, 0, "");
            }

            string status = GetJsonString(data, "status");
            decimal amountPaid = GetJsonDecimal(data, "amountPaid");

            if (amountPaid <= 0)
            {
                amountPaid = GetJsonDecimal(data, "amount");
            }

            string reference = "";

            if (data.TryGetProperty("transactions", out JsonElement transEl))
            {
                reference = transEl.GetRawText();
            }

            bool isPaid = string.Equals(status, "PAID", StringComparison.OrdinalIgnoreCase);
            bool isCancelled = string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase);

            return (isPaid, isCancelled, amountPaid, reference);
        }

        private async Task<bool> ConfirmPayOSPaymentAsync(
            string baseCode,
            decimal paidAmount,
            string source,
            string reference)
        {
            if (string.IsNullOrWhiteSpace(baseCode))
            {
                return false;
            }

            var transactions = await _context.Transactions
                .Where(t =>
                    t.TransactionCode != null &&
                    (
                        t.TransactionCode == baseCode ||
                        t.TransactionCode.StartsWith(baseCode + "_")
                    ) &&
                    t.PaymentMethod == "PayOS")
                .ToListAsync();

            if (!transactions.Any())
            {
                return false;
            }

            decimal expectedAmount = transactions.Sum(t => t.Amount);

            if (paidAmount <= 0)
            {
                paidAmount = expectedAmount;
            }

            if (Math.Round(expectedAmount, 0) != Math.Round(paidAmount, 0))
            {
                foreach (var tx in transactions)
                {
                    tx.Description = SafeAppendDescription(
                        tx.Description,
                        $" | PayOS lệch tiền. Cần:{expectedAmount:N0}, nhận:{paidAmount:N0}."
                    );
                }

                await _context.SaveChangesAsync();
                return false;
            }

            bool hasChanged = false;

            foreach (var tx in transactions)
            {
                if (tx.Status != "Success")
                {
                    tx.Status = "Success";
                    tx.CancelledAt = null;
                    tx.BillImageUrl = null;
                    hasChanged = true;
                }

                if (tx.Description == null || !tx.Description.Contains("PayOS xác nhận"))
                {
                    tx.Description = SafeAppendDescription(
                        tx.Description,
                        $" | PayOS xác nhận lúc {DateTime.Now:dd/MM/yyyy HH:mm}. Nguồn:{source}. Tiền:{paidAmount:N0}."
                    );
                    hasChanged = true;
                }
            }

            if (hasChanged)
            {
                await _context.SaveChangesAsync();
            }

            return true;
        }

        private async Task SyncPendingPayOSOrdersForUserAsync(int userId)
        {
            var pendingCodes = await _context.Transactions
                .Where(t =>
                    t.UserID == userId &&
                    t.PaymentMethod == "PayOS" &&
                    t.Status == "Pending" &&
                    t.TransactionCode != null)
                .Select(t => t.TransactionCode!)
                .ToListAsync();

            var baseCodes = pendingCodes
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Contains("_") ? x.Split('_')[0] : x)
                .Distinct()
                .Take(10)
                .ToList();

            foreach (string baseCode in baseCodes)
            {
                try
                {
                    var payOSStatus = await GetPayOSPaymentStatusAsync(baseCode);

                    if (payOSStatus.IsPaid)
                    {
                        decimal amountToConfirm = payOSStatus.AmountPaid;

                        if (amountToConfirm <= 0)
                        {
                            amountToConfirm = await GetExpectedAmountByBaseCodeAsync(baseCode, "PayOS");
                        }

                        await ConfirmPayOSPaymentAsync(
                            baseCode,
                            amountToConfirm,
                            "PayOS History Auto Sync",
                            payOSStatus.Reference
                        );
                    }
                    else if (payOSStatus.IsCancelled)
                    {
                        await CancelPayOSPaymentAsync(baseCode, "PayOS API trả trạng thái CANCELLED khi đồng bộ lịch sử.");
                    }
                }
                catch
                {
                    // Không làm sập trang History nếu PayOS tạm lỗi mạng/API.
                }
            }
        }

        private async Task<decimal> GetExpectedAmountByBaseCodeAsync(string baseCode, string paymentMethod)
        {
            if (string.IsNullOrWhiteSpace(baseCode))
            {
                return 0;
            }

            return await _context.Transactions
                .Where(t =>
                    t.TransactionCode != null &&
                    (
                        t.TransactionCode == baseCode ||
                        t.TransactionCode.StartsWith(baseCode + "_")
                    ) &&
                    t.PaymentMethod == paymentMethod)
                .SumAsync(t => t.Amount);
        }

        private async Task CancelPayOSPaymentAsync(string baseCode, string reason)
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
                    t.PaymentMethod == "PayOS" &&
                    t.Status == "Pending")
                .ToListAsync();

            if (!transactions.Any())
            {
                return;
            }

            foreach (var tx in transactions)
            {
                tx.Status = "Cancelled";
                tx.CancelledAt = DateTime.Now;
                tx.Description = SafeAppendDescription(
                    tx.Description,
                    $" | PayOS hủy lúc {DateTime.Now:dd/MM/yyyy HH:mm}."
                );
            }

            await _context.SaveChangesAsync();
        }


        private string CreatePayOSWebhookSignature(JsonElement dataElement)
        {
            string checksumKey = _configuration["PayOS:ChecksumKey"] ?? "";

            if (string.IsNullOrWhiteSpace(checksumKey))
            {
                return "";
            }

            var pairs = new SortedDictionary<string, string>(StringComparer.Ordinal);

            foreach (JsonProperty prop in dataElement.EnumerateObject())
            {
                pairs[prop.Name] = PayOSJsonValueToString(prop.Value);
            }

            string rawData = string.Join("&", pairs.Select(x => $"{x.Key}={x.Value}"));

            return HmacSHA256(checksumKey, rawData);
        }

        private string PayOSJsonValueToString(JsonElement value)
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "",
                _ => value.GetRawText()
            };
        }

        private string GetJsonString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value))
            {
                return "";
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? "";
            }

            return value.GetRawText().Trim('"');
        }

        private bool GetJsonBool(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value))
            {
                return false;
            }

            if (value.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            string text = GetJsonString(element, propertyName);

            return bool.TryParse(text, out bool result) && result;
        }

        private long GetJsonLong(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value))
            {
                return 0;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
            {
                return number;
            }

            string text = GetJsonString(element, propertyName);

            return long.TryParse(text, out long result) ? result : 0;
        }

        private decimal GetJsonDecimal(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value))
            {
                return 0;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number))
            {
                return number;
            }

            string text = GetJsonString(element, propertyName);

            if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal invariantResult))
            {
                return invariantResult;
            }

            return decimal.TryParse(text, NumberStyles.Any, new CultureInfo("vi-VN"), out decimal viResult)
                ? viResult
                : 0;
        }

        private string SafeText(string? text)
        {
            string value = text ?? "";

            if (value.Length > DescriptionMaxLength)
            {
                return value.Substring(0, DescriptionMaxLength);
            }

            return value;
        }

        private string SafeAppendDescription(string? currentDescription, string appendText)
        {
            string current = currentDescription ?? "";
            string append = appendText ?? "";
            string result = current + append;

            if (result.Length > DescriptionMaxLength)
            {
                result = result.Substring(0, DescriptionMaxLength);
            }

            return result;
        }

        private bool FixedTimeEquals(string value1, string value2)
        {
            if (value1.Length != value2.Length)
            {
                return false;
            }

            byte[] bytes1 = Encoding.UTF8.GetBytes(value1);
            byte[] bytes2 = Encoding.UTF8.GetBytes(value2);

            return CryptographicOperations.FixedTimeEquals(bytes1, bytes2);
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