using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    public class UsersController : Controller
    {
        private readonly ApplicationDbContext _context;

        public UsersController(ApplicationDbContext context)
        {
            _context = context;
        }

        // HÀM QUAN TRỌNG: Cập nhật lại Cookie để Header Admin thay đổi ngay lập tức
        private async Task UpdateAdminClaims(User user)
        {
            var claims = new List<Claim> {
                new Claim(ClaimTypes.NameIdentifier, user.UserID.ToString()),
                new Claim(ClaimTypes.Name, user.FullName ?? "Admin"),
                new Claim(ClaimTypes.Email, user.Email ?? ""),
                new Claim(ClaimTypes.Role, "Admin"),
                new Claim("Avatar", user.Avatar ?? "/images/avatars/default-user.png"),
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        }

        [HttpGet]
        public async Task<IActionResult> Index(string searchString)
        {
            var query = _context.Users.Where(u => !u.IsDeleted);
            if (!string.IsNullOrEmpty(searchString))
            {
                query = query.Where(u => u.FullName.Contains(searchString) || u.Email.Contains(searchString));
            }
            return View(await query.OrderByDescending(u => u.CreatedAt).ToListAsync());
        }

        // 2. THÙNG RÁC (Danh sách người dùng đã xóa tạm thời)
        [HttpGet]
        public async Task<IActionResult> Trash()
        {
            var deletedUsers = await _context.Users.Where(u => u.IsDeleted).ToListAsync();
            return View(deletedUsers);
        }

        // 3. XÓA TẠM THỜI (Soft Delete)
        [HttpPost]
        public async Task<IActionResult> SoftDelete(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return Json(new { success = false });

            user.IsDeleted = true; // Đưa vào thùng rác
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // 4. KHÔI PHỤC TÀI KHOẢN
        [HttpPost]
        public async Task<IActionResult> Restore(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return Json(new { success = false });

            user.IsDeleted = false; // Đưa ra khỏi thùng rác
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // 5. XÓA VĨNH VIỄN
        [HttpPost]
        public async Task<IActionResult> DeleteForever(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return Json(new { success = false });

            _context.Users.Remove(user); // Xóa khỏi DB
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // 6. CẬP NHẬT QUYỀN VÀ THÔNG TIN (GET)
        [HttpGet]
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound(); // Hoặc chuyển hướng về trang Index
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound();
            return View(user);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, User user)
        {
            if (id != user.UserID) return NotFound();

            var existingUser = await _context.Users.FindAsync(id);
            if (existingUser == null) return NotFound();

            if (ModelState.IsValid)
            {
                existingUser.FullName = user.FullName;
                existingUser.Phone = user.Phone;
                existingUser.Address = user.Address;
                existingUser.RoleID = user.RoleID;
                existingUser.IsActive = user.IsActive;

                _context.Update(existingUser);
                await _context.SaveChangesAsync();

                // Nếu Admin đang sửa chính mình, cập nhật Cookie
                var currentAdminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (id.ToString() == currentAdminId)
                {
                    await UpdateAdminClaims(existingUser);
                }

                TempData["Success"] = "Cập nhật thành công!";
                return RedirectToAction(nameof(Index));
            }
            return View(user);
        }

        // 8. KHÓA/MỞ KHÓA NHANH (AJAX)
        [HttpPost]
        public async Task<IActionResult> ToggleStatus(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return Json(new { success = false });

            user.IsActive = !user.IsActive;
            await _context.SaveChangesAsync();
            return Json(new { success = true, isActive = user.IsActive });
        }
        // 9. XEM CHI TIẾT HỒ SƠ (Profile, Giao dịch, Vi phạm)
        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            var user = await _context.Users.FirstOrDefaultAsync(m => m.UserID == id);
            if (user == null)
            {
                return NotFound();
            }

            // Lấy lịch sử giao dịch nạp tiền/thanh toán
            ViewBag.Transactions = await _context.Transactions
                .Where(t => t.UserID == id)
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync();

            // Lấy nhật ký hoạt động (Audit Logs)
            ViewBag.Logs = await _context.AuditLogs
                .Where(l => l.UserID == id)
                .OrderByDescending(l => l.CreatedAt)
                .ToListAsync();

            // Lấy danh sách báo cáo vi phạm
            ViewBag.Violations = await _context.UserViolations
                .Where(v => v.UserID == id)
                .OrderByDescending(v => v.CreatedAt)
                .ToListAsync();

            return View(user);
        }
        [HttpGet]
        public async Task<IActionResult> Profile()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null) return RedirectToAction("Login", "Account", new { area = "" });

            int userId = int.Parse(userIdClaim.Value);
            var user = await _context.Users.FindAsync(userId);
            if (user == null) return NotFound();
            return View(user);
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateAvatar(int UserID, IFormFile AvatarFile)
        {
            var user = await _context.Users.FindAsync(UserID);
            if (user == null) return NotFound();

            if (AvatarFile != null && AvatarFile.Length > 0)
            {
                string folderPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "images", "avatars");
                if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);

                string fileName = Guid.NewGuid().ToString() + Path.GetExtension(AvatarFile.FileName);
                string filePath = Path.Combine(folderPath, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await AvatarFile.CopyToAsync(stream);
                }

                // Xóa ảnh cũ
                if (!string.IsNullOrEmpty(user.Avatar) && !user.Avatar.Contains("default-user.png"))
                {
                    string oldPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", user.Avatar.TrimStart('/'));
                    if (System.IO.File.Exists(oldPath)) System.IO.File.Delete(oldPath);
                }

                user.Avatar = "/images/avatars/" + fileName;
                _context.Update(user);
                await _context.SaveChangesAsync();

                // Cập nhật Cookie ngay lập tức để Header đổi ảnh
                await UpdateAdminClaims(user);

                TempData["Success"] = "Cập nhật ảnh đại diện thành công!";
            }
            return RedirectToAction(nameof(Profile));
        }
    }
}