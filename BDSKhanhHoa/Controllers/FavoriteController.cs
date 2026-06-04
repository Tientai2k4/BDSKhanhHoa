using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json.Serialization;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class FavoriteController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<FavoriteController> _logger;

        public FavoriteController(ApplicationDbContext context, ILogger<FavoriteController> logger)
        {
            _context = context;
            _logger = logger;
        }

        // ==========================================
        // 1. TRANG DANH SÁCH BẤT ĐỘNG SẢN ĐÃ LƯU
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            int? userId = GetCurrentUserId();
            if (userId == null)
            {
                return RedirectToAction("Login", "Account", new { returnUrl = Url.Action(nameof(Index), "Favorite") });
            }

            var favorites = await _context.Favorites
                .AsNoTracking()
                .Include(f => f.Property)
                    .ThenInclude(p => p.PropertyType)
                .Include(f => f.Property)
                    .ThenInclude(p => p.Ward)
                        .ThenInclude(w => w.Area)
                .Where(f =>
                    f.UserID == userId.Value &&
                    f.Property != null &&
                    f.Property.IsDeleted == false)
                .OrderByDescending(f => f.CreatedAt)
                .ToListAsync();

            return View(favorites);
        }

        // ==========================================
        // 2. BỎ LƯU TIN YÊU THÍCH Ở TRANG /Favorite/Index
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Remove(int id)
        {
            int? userId = GetCurrentUserId();
            if (userId == null)
            {
                return RedirectToAction("Login", "Account", new { returnUrl = Url.Action(nameof(Index), "Favorite") });
            }

            var favorite = await _context.Favorites
                .FirstOrDefaultAsync(f => f.FavoriteID == id && f.UserID == userId.Value);

            if (favorite == null)
            {
                TempData["Error"] = "Không tìm thấy tin yêu thích hoặc bạn không có quyền thao tác.";
                return RedirectToAction(nameof(Index));
            }

            _context.Favorites.Remove(favorite);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Đã bỏ lưu bất động sản khỏi danh sách yêu thích.";
            return RedirectToAction(nameof(Index));
        }

        // ==========================================
        // 3. API LƯU / BỎ LƯU TIN BẰNG AJAX
        // Dùng cho nút trái tim ở trang chi tiết tin.
        // Lưu ý: View phải gửi JSON dạng { propertyId: 100 }
        // và gửi kèm RequestVerificationToken.
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleFavorite([FromBody] ToggleFavoriteRequest? request)
        {
            int? userId = GetCurrentUserId();
            if (userId == null)
            {
                return Json(new FavoriteJsonResult
                {
                    Success = false,
                    RequireLogin = true,
                    IsSaved = false,
                    Message = "Vui lòng đăng nhập để lưu tin yêu thích."
                });
            }

            if (request == null || request.PropertyId <= 0)
            {
                return Json(new FavoriteJsonResult
                {
                    Success = false,
                    RequireLogin = false,
                    IsSaved = false,
                    Message = "Tin bất động sản không hợp lệ."
                });
            }

            try
            {
                var property = await _context.Properties
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p =>
                        p.PropertyID == request.PropertyId &&
                        p.IsDeleted == false);

                if (property == null)
                {
                    return Json(new FavoriteJsonResult
                    {
                        Success = false,
                        RequireLogin = false,
                        IsSaved = false,
                        Message = "Tin bất động sản không tồn tại hoặc đã bị xóa."
                    });
                }

                if (property.Status == "Sold" || property.Status == "Rented" || property.Status == "Expired")
                {
                    return Json(new FavoriteJsonResult
                    {
                        Success = false,
                        RequireLogin = false,
                        IsSaved = false,
                        Message = "Tin này đã bán, đã cho thuê hoặc đã hết hạn nên không thể lưu."
                    });
                }

                var existingFavorite = await _context.Favorites
                    .FirstOrDefaultAsync(f =>
                        f.UserID == userId.Value &&
                        f.PropertyID == request.PropertyId);

                if (existingFavorite != null)
                {
                    _context.Favorites.Remove(existingFavorite);
                    await _context.SaveChangesAsync();

                    return Json(new FavoriteJsonResult
                    {
                        Success = true,
                        RequireLogin = false,
                        IsSaved = false,
                        Message = "Đã bỏ lưu tin."
                    });
                }

                var newFavorite = new Favorite
                {
                    UserID = userId.Value,
                    PropertyID = request.PropertyId,
                    CreatedAt = DateTime.Now
                };

                _context.Favorites.Add(newFavorite);
                await _context.SaveChangesAsync();

                return Json(new FavoriteJsonResult
                {
                    Success = true,
                    RequireLogin = false,
                    IsSaved = true,
                    Message = "Đã lưu tin vào danh sách yêu thích."
                });
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex,
                    "Lỗi lưu tin yêu thích. UserID={UserID}, PropertyID={PropertyID}",
                    userId.Value,
                    request.PropertyId);

                return Json(new FavoriteJsonResult
                {
                    Success = false,
                    RequireLogin = false,
                    IsSaved = false,
                    Message = "Không thể cập nhật lưu tin. Vui lòng kiểm tra dữ liệu người dùng/tin đăng và thử lại."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Lỗi không xác định khi lưu tin yêu thích. UserID={UserID}, PropertyID={PropertyID}",
                    userId.Value,
                    request.PropertyId);

                return Json(new FavoriteJsonResult
                {
                    Success = false,
                    RequireLogin = false,
                    IsSaved = false,
                    Message = "Không thể cập nhật lưu tin. Vui lòng thử lại."
                });
            }
        }

        private int? GetCurrentUserId()
        {
            string? userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(userIdStr, out int userId) && userId > 0 ? userId : null;
        }
    }

    public class ToggleFavoriteRequest
    {
        [JsonPropertyName("propertyId")]
        public int PropertyId { get; set; }
    }

    public class FavoriteJsonResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("requireLogin")]
        public bool RequireLogin { get; set; }

        [JsonPropertyName("isSaved")]
        public bool IsSaved { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";
    }
}
