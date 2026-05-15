using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Net;
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
        public string Code { get; set; }
        public string CartData { get; set; }
    }

    [Authorize]
    public class PaymentController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _env;

        public PaymentController(ApplicationDbContext context, IConfiguration configuration, IWebHostEnvironment env)
        {
            _context = context;
            _configuration = configuration;
            _env = env;
        }

        [HttpGet]
        public async Task<IActionResult> Checkout(int? packageId)
        {
            ViewBag.DirectPackageId = packageId;
            var allPackages = await _context.PostServicePackages.ToListAsync();
            return View(allPackages);
        }

        [HttpPost]
        public async Task<IActionResult> ApplyVoucher([FromBody] VoucherRequest req)
        {
            if (string.IsNullOrEmpty(req.CartData)) return Json(new { success = false, message = "Giỏ hàng trống." });

            var cartItems = JsonSerializer.Deserialize<List<CartItemRequest>>(req.CartData);
            if (cartItems == null || !cartItems.Any()) return Json(new { success = false, message = "Giỏ hàng lỗi." });

            decimal totalBeforeDiscount = 0;
            foreach (var item in cartItems)
            {
                var package = await _context.PostServicePackages.FindAsync(item.PackageId);
                if (package != null)
                {
                    totalBeforeDiscount += package.Price * (item.Quantity > 0 ? item.Quantity : 1);
                }
            }

            if (string.IsNullOrEmpty(req.Code)) return Json(new { success = true, discountAmount = 0, finalPrice = totalBeforeDiscount, message = "" });

            var voucher = await _context.Vouchers.FirstOrDefaultAsync(v => v.Code.ToLower() == req.Code.ToLower());
            if (voucher == null || !voucher.IsActive)
                return Json(new { success = false, message = "Mã giảm giá không tồn tại hoặc đã bị khóa." });

            if (DateTime.Now < voucher.StartDate)
                return Json(new { success = false, message = $"Voucher chỉ có hiệu lực từ {voucher.StartDate.ToString("dd/MM/yyyy HH:mm")}." });
            if (DateTime.Now > voucher.ExpiryDate)
                return Json(new { success = false, message = "Mã giảm giá đã hết hạn." });

            if (voucher.UsedCount >= voucher.Quantity)
                return Json(new { success = false, message = "Mã giảm giá đã hết lượt sử dụng." });

            if (totalBeforeDiscount < voucher.MinOrderAmount)
                return Json(new { success = false, message = $"Đơn hàng tối thiểu để áp dụng là {voucher.MinOrderAmount:N0}đ." });

            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            string expectedDesc = $"[Voucher:{req.Code.ToUpper()}]";

            bool hasUsed = await _context.Transactions.AnyAsync(t =>
                t.UserID == userId &&
                t.Description != null &&
                t.Description.Contains(expectedDesc) &&
                t.Status != "Cancelled" &&
                t.Status != "Failed");

            if (hasUsed)
            {
                return Json(new { success = false, message = "Bạn đã sử dụng mã này rồi. Mỗi tài khoản chỉ được dùng 1 lần!" });
            }

            decimal discountAmount = (totalBeforeDiscount * voucher.DiscountPercent) / 100;
            if (discountAmount > voucher.MaxDiscountAmount) discountAmount = voucher.MaxDiscountAmount;

            decimal finalPrice = totalBeforeDiscount - discountAmount;
            if (finalPrice < 0) finalPrice = 0;

            return Json(new { success = true, discountAmount = discountAmount, finalPrice = finalPrice, message = $"Áp dụng thành công! Giảm {voucher.DiscountPercent}%" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcessPayment(string cartData, string paymentMethod, string? voucherCode)
        {
            if (string.IsNullOrEmpty(cartData))
            {
                TempData["Error"] = "Giỏ hàng trống!";
                return RedirectToAction("Buy", "Package");
            }

            var cartItems = JsonSerializer.Deserialize<List<CartItemRequest>>(cartData);
            if (cartItems == null || !cartItems.Any())
            {
                TempData["Error"] = "Lỗi dữ liệu giỏ hàng!";
                return RedirectToAction("Buy", "Package");
            }

            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

            decimal totalOriginalPrice = 0;
            var validItems = new List<(PostServicePackage pkg, int qty)>();

            foreach (var item in cartItems)
            {
                var pkg = await _context.PostServicePackages.FindAsync(item.PackageId);
                if (pkg != null)
                {
                    int q = item.Quantity > 0 ? item.Quantity : 1;
                    validItems.Add((pkg, q));
                    totalOriginalPrice += pkg.Price * q;
                }
            }

            decimal finalPrice = totalOriginalPrice;
            decimal totalDiscount = 0;
            string txDescription = "Mua lượt đăng";

            if (!string.IsNullOrEmpty(voucherCode))
            {
                var voucher = await _context.Vouchers.FirstOrDefaultAsync(v => v.Code.ToLower() == voucherCode.ToLower() && v.IsActive);

                if (voucher != null &&
                    DateTime.Now >= voucher.StartDate &&
                    DateTime.Now <= voucher.ExpiryDate &&
                    voucher.UsedCount < voucher.Quantity &&
                    finalPrice >= voucher.MinOrderAmount)
                {
                    string expectedDesc = $"[Voucher:{voucherCode.ToUpper()}]";
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

                    totalDiscount = (finalPrice * voucher.DiscountPercent) / 100;
                    if (totalDiscount > voucher.MaxDiscountAmount) totalDiscount = voucher.MaxDiscountAmount;
                    finalPrice -= totalDiscount;
                    if (finalPrice < 0) finalPrice = 0;

                    voucher.UsedCount += 1;
                    _context.Update(voucher);

                    txDescription = $"{expectedDesc} Mua lượt đăng";
                }
            }

            long unixTimestamp = DateTimeOffset.Now.ToUnixTimeSeconds();
            string txCodeBase = unixTimestamp.ToString();

            for (int i = 0; i < validItems.Count; i++)
            {
                var item = validItems[i];
                decimal itemRatio = totalOriginalPrice > 0 ? (item.pkg.Price * item.qty) / totalOriginalPrice : 0;
                decimal itemFinalPrice = (item.pkg.Price * item.qty) - (totalDiscount * itemRatio);

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
                    Description = txDescription,
                    CreatedAt = DateTime.Now
                };
                _context.Transactions.Add(transaction);
            }
            await _context.SaveChangesAsync();

            switch (paymentMethod)
            {
                case "VNPay":
                    return Redirect(GenerateVnPayUrl(finalPrice, txCodeBase));

                case "PayOS":
                    string payosUrl = await GeneratePayOSUrl(finalPrice, txCodeBase, "Thanh toan don hang BDS");
                    return Redirect(payosUrl);

                case "SePay":
                    TempData["Info"] = "Hệ thống SePay sẽ tự động đối soát giao dịch của bạn trong 30s.";
                    return RedirectToAction("TransferInfo", new { baseCode = txCodeBase, amount = finalPrice });

                case "BankTransfer":
                    return RedirectToAction("TransferInfo", new { baseCode = txCodeBase, amount = finalPrice });

                default:
                    return RedirectToAction("History");
            }
        }

        [HttpGet]
        public async Task<IActionResult> History(string? status, DateTime? fromDate, DateTime? toDate)
        {
            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var query = _context.Transactions.Include(t => t.Package).Where(t => t.UserID == userId).AsQueryable();

            if (!string.IsNullOrEmpty(status) && status != "All")
            {
                query = query.Where(t => t.Status == status);
            }
            if (fromDate.HasValue)
            {
                query = query.Where(t => t.CreatedAt >= fromDate.Value.Date);
            }
            if (toDate.HasValue)
            {
                query = query.Where(t => t.CreatedAt <= toDate.Value.Date.AddDays(1).AddTicks(-1));
            }

            var transactions = await query.OrderByDescending(t => t.CreatedAt).ToListAsync();

            ViewBag.Status = status;
            ViewBag.FromDate = fromDate?.ToString("yyyy-MM-dd");
            ViewBag.ToDate = toDate?.ToString("yyyy-MM-dd");

            return View(transactions);
        }

        private string GenerateVnPayUrl(decimal amount, string orderId)
        {
            var vnp_Params = new SortedList<string, string>
            {
                { "vnp_Version", "2.1.0" }, { "vnp_Command", "pay" },
                { "vnp_TmnCode", _configuration["VnPay:TmnCode"] },
                { "vnp_Amount", (amount * 100).ToString("0") },
                { "vnp_CreateDate", DateTime.Now.ToString("yyyyMMddHHmmss") },
                { "vnp_CurrCode", "VND" },
                { "vnp_IpAddr", HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1" },
                { "vnp_Locale", "vn" },
                { "vnp_OrderInfo", "Thanh toan don hang " + orderId },
                { "vnp_OrderType", "other" },
                { "vnp_ReturnUrl", _configuration["VnPay:ReturnUrl"] },
                { "vnp_TxnRef", orderId }
            };
            var queryString = string.Join("&", vnp_Params.Select(kvp => $"{kvp.Key}={WebUtility.UrlEncode(kvp.Value)}"));
            string vnp_SecureHash = HmacSHA512(_configuration["VnPay:HashSecret"], queryString);
            return $"{_configuration["VnPay:BaseUrl"]}?{queryString}&vnp_SecureHash={vnp_SecureHash}";
        }

        [HttpGet]
        public async Task<IActionResult> PaymentCallback()
        {
            var queryDict = HttpContext.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString());
            string vnp_SecureHash = queryDict.GetValueOrDefault("vnp_SecureHash");
            queryDict.Remove("vnp_SecureHash"); queryDict.Remove("vnp_SecureHashType");

            var queryString = string.Join("&", new SortedDictionary<string, string>(queryDict).Select(kvp => $"{kvp.Key}={WebUtility.UrlEncode(kvp.Value)}"));
            if (HmacSHA512(_configuration["VnPay:HashSecret"], queryString) == vnp_SecureHash)
            {
                if (queryDict.GetValueOrDefault("vnp_ResponseCode") == "00")
                    await ConfirmPayment(queryDict.GetValueOrDefault("vnp_TxnRef"));
                else TempData["Error"] = "Thanh toán bị hủy.";
            }
            return RedirectToAction("History");
        }

        private async Task<string> GeneratePayOSUrl(decimal amount, string orderId, string desc)
        {
            string clientId = _configuration["PayOS:ClientId"];
            string apiKey = _configuration["PayOS:ApiKey"];
            string checksumKey = _configuration["PayOS:ChecksumKey"];

            int orderCodeInt = int.Parse(orderId);

            var reqData = new
            {
                orderCode = orderCodeInt,
                amount = (int)amount,
                description = "Thanh toan " + orderId,
                returnUrl = _configuration["PayOS:ReturnUrl"],
                cancelUrl = _configuration["PayOS:CancelUrl"]
            };

            string signatureData = $"amount={reqData.amount}&cancelUrl={reqData.cancelUrl}&description={reqData.description}&orderCode={reqData.orderCode}&returnUrl={reqData.returnUrl}";
            string signature = HmacSHA256(checksumKey, signatureData);

            var finalBody = new
            {
                reqData.orderCode,
                reqData.amount,
                reqData.description,
                reqData.returnUrl,
                reqData.cancelUrl,
                signature
            };

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("x-client-id", clientId);
                client.DefaultRequestHeaders.Add("x-api-key", apiKey);

                var content = new StringContent(JsonSerializer.Serialize(finalBody), Encoding.UTF8, "application/json");
                var response = await client.PostAsync("https://api-merchant.payos.vn/v2/payment-requests", content);

                if (response.IsSuccessStatusCode)
                {
                    var responseString = await response.Content.ReadAsStringAsync();
                    using (JsonDocument doc = JsonDocument.Parse(responseString))
                    {
                        var root = doc.RootElement;
                        if (root.GetProperty("code").GetString() == "00")
                        {
                            return root.GetProperty("data").GetProperty("checkoutUrl").GetString();
                        }
                    }
                }
            }
            return "/Payment/History";
        }

        [HttpGet]
        public async Task<IActionResult> PayOSCallback(int orderCode)
        {
            await ConfirmPayment(orderCode.ToString());
            TempData["Success"] = "Thanh toán qua PayOS thành công!";
            return RedirectToAction("History");
        }

        [HttpGet]
        public IActionResult PayOSCancel()
        {
            TempData["Error"] = "Bạn đã hủy thanh toán PayOS.";
            return RedirectToAction("History");
        }

        [HttpPost]
        [AllowAnonymous]
        public async Task<IActionResult> SePayWebhook([FromBody] JsonElement payload)
        {
            string transferContent = payload.GetProperty("transferContent").GetString() ?? "";
            var pendingTxns = await _context.Transactions
                .Where(t => t.Status == "Pending" && t.PaymentMethod == "SePay")
                .ToListAsync();

            foreach (var txn in pendingTxns)
            {
                string baseCode = txn.TransactionCode.Split('_')[0];
                if (transferContent.Contains(baseCode))
                {
                    await ConfirmPayment(baseCode);
                    return Ok(new { success = true, message = $"Auto confirmed {baseCode}" });
                }
            }
            return Ok(new { success = false, message = "Not found matching BaseCode" });
        }

        [HttpGet]
        public async Task<IActionResult> TransferInfo(string baseCode, decimal amount)
        {
            var bankAccount = await _context.BankAccounts.FirstOrDefaultAsync(b => b.IsActive);
            if (bankAccount == null) { TempData["Error"] = "Chưa cấu hình tài khoản ngân hàng."; return RedirectToAction("History"); }
            ViewBag.BaseCode = baseCode; ViewBag.Amount = amount; return View(bankAccount);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitBill(string baseCode, IFormFile billImage)
        {
            if (billImage != null && billImage.Length > 0)
            {
                var ext = Path.GetExtension(billImage.FileName).ToLowerInvariant();
                if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".webp")
                {
                    TempData["Error"] = "Chỉ chấp nhận file hình ảnh (.jpg, .png, .webp)!";
                    return RedirectToAction("History");
                }

                string uploadsFolder = Path.Combine(_env.WebRootPath, "uploads", "bills");
                if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

                string uniqueFileName = Guid.NewGuid().ToString() + "_" + Path.GetFileName(billImage.FileName);
                string filePath = Path.Combine(uploadsFolder, uniqueFileName);

                using (var fileStream = new FileStream(filePath, FileMode.Create))
                {
                    await billImage.CopyToAsync(fileStream);
                }

                string dbImageUrl = "/uploads/bills/" + uniqueFileName;
                var txns = await _context.Transactions
                    .Where(t => t.TransactionCode.StartsWith(baseCode) && t.Status == "Pending")
                    .ToListAsync();

                foreach (var t in txns) { t.BillImageUrl = dbImageUrl; }
                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã gửi biên lai thành công! Hệ thống đang chờ Admin xét duyệt.";
            }
            else
            {
                TempData["Error"] = "Vui lòng đính kèm hình ảnh biên lai trước khi xác nhận!";
            }
            return RedirectToAction("History");
        }

        private async Task ConfirmPayment(string baseCode)
        {
            var transactions = await _context.Transactions.Where(t => t.TransactionCode.StartsWith(baseCode) && t.Status == "Pending").ToListAsync();
            foreach (var t in transactions) { t.Status = "Success"; }
            await _context.SaveChangesAsync();
        }

        private string HmacSHA512(string key, string inputData)
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            using (var hmac = new HMACSHA512(keyBytes))
            {
                byte[] hashValue = hmac.ComputeHash(Encoding.UTF8.GetBytes(inputData));
                return string.Concat(hashValue.Select(b => b.ToString("x2")));
            }
        }

        private string HmacSHA256(string key, string inputData)
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            using (var hmac = new HMACSHA256(keyBytes))
            {
                byte[] hashValue = hmac.ComputeHash(Encoding.UTF8.GetBytes(inputData));
                return string.Concat(hashValue.Select(b => b.ToString("x2")));
            }
        }

        // ==================================================
        // TÍNH NĂNG MỚI: XUẤT HÓA ĐƠN (INVOICE) TỰ ĐỘNG
        // ==================================================
        [HttpGet]
        public async Task<IActionResult> Invoice(string baseCode)
        {
            if (string.IsNullOrEmpty(baseCode)) return NotFound();

            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId)) return Challenge();

            // Tìm toàn bộ các Item thuộc về mã Đơn hàng chung (BaseCode) của User này
            var transactions = await _context.Transactions
                .Include(t => t.User)
                .Include(t => t.Package)
                .Where(t => t.TransactionCode.StartsWith(baseCode) && t.UserID == userId)
                .ToListAsync();

            if (!transactions.Any())
            {
                TempData["Error"] = "Không tìm thấy thông tin đơn hàng.";
                return RedirectToAction("History");
            }

            // Chỉ cho phép xuất hóa đơn nếu trạng thái đã hoàn tất (Success)
            if (transactions.First().Status != "Success")
            {
                TempData["Error"] = "Giao dịch chưa hoàn thành, không thể xuất hóa đơn.";
                return RedirectToAction("History");
            }

            return View(transactions);
        }
    }
}