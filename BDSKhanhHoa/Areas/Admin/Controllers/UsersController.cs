using BDSKhanhHoa.Data;
using BDSKhanhHoa.Helpers; // Thêm namespace này để dùng PasswordHasher
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
                new Claim(ClaimTypes.Role, user.RoleID == 1 ? "Admin" : (user.RoleID == 2 ? "Staff" : "Member")),
                new Claim("Avatar", user.Avatar ?? "/images/avatars/default-user.png"),
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        }

        // 1. DANH SÁCH NGƯỜI DÙNG (Có tìm kiếm và lọc)
        [HttpGet]
        public async Task<IActionResult> Index(string searchString, int? roleId)
        {
            var query = _context.Users.Where(u => !u.IsDeleted);

            if (!string.IsNullOrEmpty(searchString))
            {
                searchString = searchString.ToLower();
                query = query.Where(u =>
                    (u.FullName != null && u.FullName.ToLower().Contains(searchString)) ||
                    (u.Email != null && u.Email.ToLower().Contains(searchString)) ||
                    (u.Username != null && u.Username.ToLower().Contains(searchString)));
            }

            if (roleId.HasValue)
            {
                query = query.Where(u => u.RoleID == roleId.Value);
            }

            ViewBag.SearchString = searchString;
            ViewBag.RoleId = roleId;
            ViewBag.TrashCount = await _context.Users.CountAsync(u => u.IsDeleted);

            return View(await query.OrderByDescending(u => u.CreatedAt).ToListAsync());
        }

        // 2. THÊM MỚI NGƯỜI DÙNG TỪ ADMIN
        [HttpGet]
        public IActionResult Create()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(User user)
        {
            if (ModelState.IsValid)
            {
                // Kiểm tra trùng Username hoặc Email
                if (await _context.Users.AnyAsync(u => u.Username == user.Username))
                {
                    ModelState.AddModelError("Username", "Tên đăng nhập này đã tồn tại trong hệ thống.");
                    return View(user);
                }
                if (await _context.Users.AnyAsync(u => u.Email == user.Email))
                {
                    ModelState.AddModelError("Email", "Email này đã được sử dụng.");
                    return View(user);
                }

                user.CreatedAt = DateTime.Now;
                user.IsDeleted = false;
                user.IsEmailVerified = true; // Admin tạo thì xác nhận luôn

                // Gán avatar mặc định nếu không có
                if (string.IsNullOrEmpty(user.Avatar))
                {
                    user.Avatar = "/images/avatars/default-user.png";
                }

                // QUAN TRỌNG: Mã hóa mật khẩu giống AccountController để người dùng có thể đăng nhập được
                user.Password = PasswordHasher.HashPassword(user.Password);

                _context.Add(user);
                await _context.SaveChangesAsync();

                TempData["Success"] = "Đã thêm thành viên mới thành công!";
                return RedirectToAction(nameof(Index));
            }
            return View(user);
        }

        // 3. THÙNG RÁC (Danh sách người dùng đã xóa tạm thời)
        [HttpGet]
        public async Task<IActionResult> Trash()
        {
            var deletedUsers = await _context.Users.Where(u => u.IsDeleted).ToListAsync();
            return View(deletedUsers);
        }

        // 4. XÓA TẠM THỜI (Soft Delete)
        [HttpPost]
        public async Task<IActionResult> SoftDelete(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return Json(new { success = false, message = "Không tìm thấy người dùng" });

            // Không cho phép Admin xóa chính mình
            var currentAdminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (id.ToString() == currentAdminId) return Json(new { success = false, message = "Không thể xóa chính mình" });

            user.IsDeleted = true;
            user.IsActive = false; // Tự động khóa khi đưa vào thùng rác
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // 5. KHÔI PHỤC TÀI KHOẢN
        [HttpPost]
        public async Task<IActionResult> Restore(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return Json(new { success = false });

            user.IsDeleted = false;
            user.IsActive = true; // Khôi phục trạng thái hoạt động
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // 6. XÓA VĨNH VIỄN
        [HttpPost]
        public async Task<IActionResult> DeleteForever(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return Json(new { success = false });

            _context.Users.Remove(user);
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // 7. CẬP NHẬT QUYỀN VÀ THÔNG TIN (GET)
        [HttpGet]
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();
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

            // Loại bỏ validate Password khi edit thông tin cơ bản
            ModelState.Remove("Password");

            if (ModelState.IsValid)
            {
                existingUser.FullName = user.FullName;
                existingUser.Phone = user.Phone;
                existingUser.Address = user.Address;
                existingUser.RoleID = user.RoleID;
                existingUser.IsActive = user.IsActive;
                existingUser.Bio = user.Bio;
                existingUser.Zalo = user.Zalo;
                existingUser.Facebook = user.Facebook;

                _context.Update(existingUser);
                await _context.SaveChangesAsync();

                // Nếu Admin đang sửa chính mình, cập nhật Cookie
                var currentAdminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (id.ToString() == currentAdminId)
                {
                    await UpdateAdminClaims(existingUser);
                }

                TempData["Success"] = "Cập nhật thông tin thành công!";
                return RedirectToAction(nameof(Index));
            }
            return View(user);
        }

        // 8. ĐẶT LẠI MẬT KHẨU (TỪ ADMIN)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(int id, string newPassword)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound();

            if (!string.IsNullOrEmpty(newPassword) && newPassword.Length >= 6)
            {
                // QUAN TRỌNG: Mã hóa mật khẩu mới trước khi lưu
                user.Password = PasswordHasher.HashPassword(newPassword);
                _context.Update(user);
                await _context.SaveChangesAsync();

                TempData["Success"] = "Đã đặt lại mật khẩu thành công cho tài khoản " + user.Username;
            }
            else
            {
                TempData["Error"] = "Mật khẩu mới phải có ít nhất 6 ký tự.";
            }

            return RedirectToAction(nameof(Edit), new { id = user.UserID });
        }

        // 9. KHÓA/MỞ KHÓA NHANH (AJAX)
        [HttpPost]
        public async Task<IActionResult> ToggleStatus(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return Json(new { success = false });

            var currentAdminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (id.ToString() == currentAdminId) return Json(new { success = false, message = "Bạn không thể tự khóa tài khoản của mình!" });

            user.IsActive = !user.IsActive;
            await _context.SaveChangesAsync();
            return Json(new { success = true, isActive = user.IsActive });
        }

        // 10. XEM CHI TIẾT HỒ SƠ 
        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            var user = await _context.Users.FirstOrDefaultAsync(m => m.UserID == id);
            if (user == null) return NotFound();

            // Lấy giao dịch, nhật ký, vi phạm tối đa 50 bản ghi mới nhất
            ViewBag.Transactions = await _context.Transactions?
                .Where(t => t.UserID == id)
                .OrderByDescending(t => t.CreatedAt)
                .Take(50)
                .ToListAsync() ?? new List<Transaction>();

            ViewBag.Logs = await _context.AuditLogs?
                .Where(l => l.UserID == id)
                .OrderByDescending(l => l.CreatedAt)
                .Take(50)
                .ToListAsync() ?? new List<AuditLog>();

            ViewBag.Violations = await _context.UserViolations?
                .Where(v => v.UserID == id)
                .OrderByDescending(v => v.CreatedAt)
                .Take(50)
                .ToListAsync() ?? new List<UserViolation>();

            return View(user);
        }

        // 11. HỒ SƠ ADMIN ĐANG ĐĂNG NHẬP
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

                if (!string.IsNullOrEmpty(user.Avatar) && !user.Avatar.Contains("default-user.png"))
                {
                    string oldPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", user.Avatar.TrimStart('/'));
                    if (System.IO.File.Exists(oldPath)) System.IO.File.Delete(oldPath);
                }

                user.Avatar = "/images/avatars/" + fileName;
                _context.Update(user);
                await _context.SaveChangesAsync();

                await UpdateAdminClaims(user);

                TempData["Success"] = "Cập nhật ảnh đại diện thành công!";
            }
            return RedirectToAction(nameof(Profile));
        }
    }
}