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
        public string Code { get; set; } = "";
        public string CartData { get; set; } = "";
    }

    [Authorize]
    public class PaymentController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _env;

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
            /*
                Chỉ đưa gói đang dùng sang trang thanh toán.
                Nếu URL truyền packageId của gói đã ngừng dùng thì không cho thanh toán.
            */

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

            long unixTimestamp = DateTimeOffset.Now.ToUnixTimeSeconds();
            string txCodeBase = unixTimestamp.ToString();

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
                    CreatedAt = DateTime.Now
                };

                _context.Transactions.Add(transaction);
            }

            await _context.SaveChangesAsync();

            switch (paymentMethod)
            {
                case "BankTransfer":
                    TempData["Success"] = "Đã tạo đơn thanh toán chuyển khoản. Vui lòng chuyển khoản và chờ xác nhận.";
                    return RedirectToAction("History");

                case "Cash":
                    TempData["Success"] = "Đã ghi nhận đơn thanh toán tiền mặt. Vui lòng liên hệ quản trị viên để xác nhận.";
                    return RedirectToAction("History");

                case "Momo":
                    TempData["Info"] = "Chức năng thanh toán MoMo đang được cấu hình. Đơn hàng đã được ghi nhận.";
                    return RedirectToAction("History");

                case "VNPay":
                    TempData["Info"] = "Chức năng thanh toán VNPay đang được cấu hình. Đơn hàng đã được ghi nhận.";
                    return RedirectToAction("History");

                case "PayOS":
                    TempData["Info"] = "Chức năng thanh toán PayOS đang được cấu hình. Đơn hàng đã được ghi nhận.";
                    return RedirectToAction("History");

                default:
                    TempData["Success"] = "Đơn hàng đã được ghi nhận.";
                    return RedirectToAction("History");
            }
        }

        [HttpGet]
        public async Task<IActionResult> History(string status = "All", string? fromDate = null, string? toDate = null)
        {
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

            return View(transactions);
        }
    }
}