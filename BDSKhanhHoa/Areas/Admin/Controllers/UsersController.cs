using BDSKhanhHoa.Data;
using BDSKhanhHoa.Helpers;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")] // Chỉ Admin tối cao mới được vào đây
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
            var role = await _context.Roles.FindAsync(user.RoleID);
            string roleName = role != null ? role.RoleName : "Member";

            var claims = new List<Claim> {
                new Claim(ClaimTypes.NameIdentifier, user.UserID.ToString()),
                new Claim(ClaimTypes.Name, user.FullName ?? user.Username),
                new Claim(ClaimTypes.Email, user.Email ?? ""),
                new Claim(ClaimTypes.Role, roleName),
                new Claim("Avatar", user.Avatar ?? "/images/avatars/default-user.png"),
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        }

        // 1. DANH SÁCH NGƯỜI DÙNG (Tích hợp Phân trang & Join Bảng Roles)
        [HttpGet]
        public async Task<IActionResult> Index(string searchString, int? roleId, int page = 1)
        {
            int pageSize = 15; // Phân trang: 15 user / trang

            var query = _context.Users.Where(u => !u.IsDeleted);

            // Tìm kiếm
            if (!string.IsNullOrEmpty(searchString))
            {
                string searchLower = searchString.ToLower();
                query = query.Where(u =>
                    (u.FullName != null && u.FullName.ToLower().Contains(searchLower)) ||
                    (u.Email != null && u.Email.ToLower().Contains(searchLower)) ||
                    (u.Username != null && u.Username.ToLower().Contains(searchLower)) ||
                    (u.Phone != null && u.Phone.Contains(searchLower)));
            }

            // Lọc theo phân quyền
            if (roleId.HasValue && roleId.Value > 0)
            {
                query = query.Where(u => u.RoleID == roleId.Value);
            }

            // Thống kê KPI
            ViewBag.TotalUsers = await _context.Users.CountAsync(u => !u.IsDeleted);
            ViewBag.ActiveUsers = await _context.Users.CountAsync(u => !u.IsDeleted && u.IsActive);
            ViewBag.LockedUsers = await _context.Users.CountAsync(u => !u.IsDeleted && !u.IsActive);
            ViewBag.TrashCount = await _context.Users.CountAsync(u => u.IsDeleted);

            // Truyền danh sách Roles ra View để làm Dropdown lọc và hiển thị tên Role
            var roles = await _context.Roles.ToListAsync();
            ViewBag.Roles = roles;
            ViewBag.RoleDictionary = roles.ToDictionary(r => r.RoleID, r => r.RoleName);

            // Xử lý phân trang
            int totalItems = await query.CountAsync();
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
            page = page < 1 ? 1 : (page > totalPages && totalPages > 0 ? totalPages : page);

            var users = await query
                .OrderByDescending(u => u.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.SearchString = searchString;
            ViewBag.RoleId = roleId;
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;

            ViewData["Title"] = "Hệ thống Quản lý Người dùng";
            return View(users);
        }

        // 2. XUẤT DỮ LIỆU NGƯỜI DÙNG RA FILE CSV/EXCEL
        [HttpGet]
        public async Task<IActionResult> ExportCsv(string searchString, int? roleId)
        {
            var query = _context.Users.Where(u => !u.IsDeleted);

            if (!string.IsNullOrEmpty(searchString))
            {
                string searchLower = searchString.ToLower();
                query = query.Where(u => u.Username.ToLower().Contains(searchLower) || u.Email.ToLower().Contains(searchLower));
            }
            if (roleId.HasValue && roleId.Value > 0) query = query.Where(u => u.RoleID == roleId.Value);

            var users = await query.OrderByDescending(u => u.CreatedAt).ToListAsync();
            var rolesDict = await _context.Roles.ToDictionaryAsync(r => r.RoleID, r => r.RoleName);

            var builder = new StringBuilder();
            builder.AppendLine("ID,Username,Họ Tên,Email,Số điện thoại,Vai trò,Trạng thái,Ngày tạo");

            foreach (var u in users)
            {
                string roleName = rolesDict.ContainsKey(u.RoleID) ? rolesDict[u.RoleID] : "Unknown";
                string status = u.IsActive ? "Đang hoạt động" : "Bị khóa";
                string date = u.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";

                builder.AppendLine($"{u.UserID},{u.Username},\"{u.FullName}\",{u.Email},{u.Phone},{roleName},{status},{date}");
            }

            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray();
            return File(bytes, "text/csv", $"DanhSachNguoiDung_{DateTime.Now:yyyyMMddHHmmss}.csv");
        }

        // 3. THÊM MỚI NGƯỜI DÙNG TỪ ADMIN
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            ViewBag.RolesList = new SelectList(await _context.Roles.ToListAsync(), "RoleID", "RoleName");
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(User user, IFormFile AvatarFile) // Thêm IFormFile
        {
            if (ModelState.IsValid)
            {
                // 1. Kiểm tra trùng lặp
                if (await _context.Users.AnyAsync(u => u.Username == user.Username))
                {
                    ModelState.AddModelError("Username", "Tên đăng nhập đã tồn tại.");
                    ViewBag.RolesList = new SelectList(await _context.Roles.ToListAsync(), "RoleID", "RoleName", user.RoleID);
                    return View(user);
                }

                // 2. Xử lý Upload Ảnh đại diện (Nếu Admin chọn ảnh)
                if (AvatarFile != null && AvatarFile.Length > 0)
                {
                    string uploadDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot/uploads/avatars");
                    if (!Directory.Exists(uploadDir)) Directory.CreateDirectory(uploadDir);

                    string fileName = Guid.NewGuid().ToString() + Path.GetExtension(AvatarFile.FileName);
                    string filePath = Path.Combine(uploadDir, fileName);

                    using (var stream = new FileStream(filePath, FileMode.Create))
                    {
                        await AvatarFile.CopyToAsync(stream);
                    }
                    user.Avatar = "/uploads/avatars/" + fileName;
                }
                else
                {
                    user.Avatar = "/images/avatars/default-user.png"; // Ảnh mặc định nếu không chọn
                }

                // 3. Khởi tạo các giá trị khác
                user.Password = PasswordHasher.HashPassword(user.Password);
                user.CreatedAt = DateTime.Now;
                user.IsDeleted = false;
                user.IsEmailVerified = true;

                _context.Add(user);
                await _context.SaveChangesAsync();

                // 4. Logic tạo BusinessProfile nếu là Chủ đầu tư (Giữ nguyên phần cũ của bạn)
                bool isBusiness = Request.Form["IsBusiness"] == "on";
                if (isBusiness)
                {
                    var bizProfile = new BusinessProfile
                    {
                        UserID = user.UserID,
                        BusinessName = Request.Form["BusinessName"],
                        TaxCode = Request.Form["TaxCode"],
                        VerificationStatus = "Approved",
                        CreatedAt = DateTime.Now,
                        RepresentativeName = user.FullName ?? user.Username,
                        RepresentativePhone = user.Phone ?? "N/A",
                        BusinessAddress = user.Address ?? "N/A"
                    };
                    _context.BusinessProfiles.Add(bizProfile);
                    await _context.SaveChangesAsync();
                }

                TempData["Success"] = "Tạo tài khoản thành công!";
                return RedirectToAction(nameof(Index));
            }
            ViewBag.RolesList = new SelectList(await _context.Roles.ToListAsync(), "RoleID", "RoleName", user.RoleID);
            return View(user);
        }


        [HttpGet]
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound();

            ViewBag.RolesList = new SelectList(await _context.Roles.ToListAsync(), "RoleID", "RoleName", user.RoleID);
            return View(user);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, User user)
        {
            if (id != user.UserID) return NotFound();

            var existingUser = await _context.Users.FindAsync(id);
            if (existingUser == null) return NotFound();

            ModelState.Remove("Password");
            ModelState.Remove("Username"); // Username không được đổi

            if (ModelState.IsValid)
            {
                existingUser.FullName = user.FullName;
                existingUser.Phone = user.Phone;
                existingUser.Address = user.Address;
                existingUser.RoleID = user.RoleID; // Cập nhật Role động
                existingUser.IsActive = user.IsActive;
                existingUser.Bio = user.Bio;
                existingUser.Zalo = user.Zalo;
                existingUser.Facebook = user.Facebook;

                _context.Update(existingUser);

                // Ghi Log sửa đổi
                var currentAdminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                _context.AuditLogs.Add(new AuditLog
                {
                    UserID = int.Parse(currentAdminId),
                    Action = $"Cập nhật thông tin tài khoản: {existingUser.Username}",
                    Target = $"Users (ID: {existingUser.UserID})",
                    CreatedAt = DateTime.Now
                });

                await _context.SaveChangesAsync();

                if (id.ToString() == currentAdminId)
                {
                    await UpdateAdminClaims(existingUser);
                }

                TempData["Success"] = "Cập nhật thông tin người dùng thành công!";
                return RedirectToAction(nameof(Index));
            }
            ViewBag.RolesList = new SelectList(await _context.Roles.ToListAsync(), "RoleID", "RoleName", user.RoleID);
            return View(user);
        }

        // 5. ĐẶT LẠI MẬT KHẨU (TỪ ADMIN)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(int id, string newPassword)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound();

            if (!string.IsNullOrEmpty(newPassword) && newPassword.Length >= 6)
            {
                user.Password = PasswordHasher.HashPassword(newPassword);
                _context.Update(user);

                var currentAdminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                _context.AuditLogs.Add(new AuditLog
                {
                    UserID = int.Parse(currentAdminId),
                    Action = $"Cấp lại mật khẩu cho tài khoản: {user.Username}",
                    Target = $"Users (ID: {user.UserID})",
                    CreatedAt = DateTime.Now
                });

                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã đặt lại mật khẩu thành công cho tài khoản " + user.Username;
            }
            else
            {
                TempData["Error"] = "Mật khẩu mới phải có ít nhất 6 ký tự.";
            }

            return RedirectToAction(nameof(Edit), new { id = user.UserID });
        }

        // 6. KHÓA / MỞ KHÓA TÀI KHOẢN (AJAX)
        [HttpPost]
        public async Task<IActionResult> ToggleStatus(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return Json(new { success = false });

            var currentAdminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (id.ToString() == currentAdminId) return Json(new { success = false, message = "Lỗi bảo mật: Bạn không thể tự khóa tài khoản của chính mình!" });

            user.IsActive = !user.IsActive;
            await _context.SaveChangesAsync();
            return Json(new { success = true, isActive = user.IsActive });
        }

        // 7. XÓA TẠM THỜI (VÀO THÙNG RÁC)
        [HttpPost]
        public async Task<IActionResult> SoftDelete(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return Json(new { success = false, message = "Không tìm thấy người dùng" });

            var currentAdminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (id.ToString() == currentAdminId) return Json(new { success = false, message = "Không thể tự xóa chính mình!" });

            // Kiểm tra: Không cho xóa User đang có RoleID là Admin nếu đó là Admin duy nhất
            if (user.RoleID == 1)
            {
                int adminCount = await _context.Users.CountAsync(u => u.RoleID == 1 && !u.IsDeleted);
                if (adminCount <= 1) return Json(new { success = false, message = "Hệ thống phải có ít nhất 1 Quản trị viên!" });
            }

            user.IsDeleted = true;
            user.IsActive = false;

            _context.AuditLogs.Add(new AuditLog
            {
                UserID = int.Parse(currentAdminId),
                Action = $"Đưa tài khoản {user.Username} vào thùng rác",
                Target = $"Users (ID: {user.UserID})",
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // 8. CÁC HÀM THÙNG RÁC (TRASH, RESTORE, DELETE FOREVER) - Tương tự code của bạn đã có
        [HttpGet]
        public async Task<IActionResult> Trash()
        {
            var deletedUsers = await _context.Users.Where(u => u.IsDeleted).ToListAsync();
            var roles = await _context.Roles.ToDictionaryAsync(r => r.RoleID, r => r.RoleName);
            ViewBag.RoleDictionary = roles;
            return View(deletedUsers);
        }

        [HttpPost]
        public async Task<IActionResult> Restore(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return Json(new { success = false });

            user.IsDeleted = false;
            user.IsActive = true;
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }
        // 9. XEM CHI TIẾT NGƯỜI DÙNG (Fix lỗi 404)
        [HttpGet]
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null) return NotFound();

            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound();

            // Lấy dữ liệu bổ trợ cho các Tab trong View Details
            ViewBag.Transactions = await _context.Transactions
                .Where(t => t.UserID == id)
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync();

            ViewBag.Logs = await _context.AuditLogs
                .Where(l => l.UserID == id)
                .OrderByDescending(l => l.CreatedAt)
                .Take(20) // Lấy 20 hoạt động gần nhất
                .ToListAsync();

            ViewBag.Violations = await _context.UserViolations
                .Where(v => v.UserID == id)
                .OrderByDescending(v => v.CreatedAt)
                .ToListAsync();

            return View(user);
        }
        [HttpPost]
        public async Task<IActionResult> DeleteForever(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return Json(new { success = false });

            _context.Users.Remove(user);
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }
    }
}