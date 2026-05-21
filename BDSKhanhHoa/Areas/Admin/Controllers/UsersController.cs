using BDSKhanhHoa.Data;
using BDSKhanhHoa.Helpers;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class UsersController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmailService _emailService;
        private readonly IAuditLogService _auditLogService; // Thêm Service Log

        public UsersController(ApplicationDbContext context, IEmailService emailService, IAuditLogService auditLogService)
        {
            _context = context;
            _emailService = emailService;
            _auditLogService = auditLogService;
        }

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

        [HttpGet]
        public async Task<IActionResult> Index(string searchString, int? roleId, string verifyStatus, int page = 1)
        {
            int pageSize = 15;

            var query = _context.Users.Where(u => !u.IsDeleted);

            if (!string.IsNullOrEmpty(searchString))
            {
                string searchLower = searchString.ToLower();

                query = query.Where(u =>
                    (u.FullName != null && u.FullName.ToLower().Contains(searchLower)) ||
                    (u.Email != null && u.Email.ToLower().Contains(searchLower)) ||
                    (u.Username != null && u.Username.ToLower().Contains(searchLower)) ||
                    (u.Phone != null && u.Phone.Contains(searchLower)));
            }

            if (roleId.HasValue && roleId.Value > 0)
            {
                query = query.Where(u => u.RoleID == roleId.Value);
            }

            if (!string.IsNullOrWhiteSpace(verifyStatus))
            {
                verifyStatus = verifyStatus.Trim().ToLower();

                if (verifyStatus == "verified")
                {
                    query = query.Where(u => u.IsEmailVerified == true);
                }
                else if (verifyStatus == "unverified")
                {
                    query = query.Where(u => u.IsEmailVerified == false || u.IsEmailVerified == null);
                }
                else if (verifyStatus == "active")
                {
                    query = query.Where(u => u.IsActive == true && u.IsEmailVerified == true);
                }
                else if (verifyStatus == "locked")
                {
                    query = query.Where(u => u.IsActive == false && u.IsEmailVerified == true);
                }
            }

            ViewBag.TotalUsers = await _context.Users.CountAsync(u => !u.IsDeleted);
            ViewBag.ActiveUsers = await _context.Users.CountAsync(u => !u.IsDeleted && u.IsActive && u.IsEmailVerified == true);
            ViewBag.LockedUsers = await _context.Users.CountAsync(u => !u.IsDeleted && !u.IsActive && u.IsEmailVerified == true);
            ViewBag.ViolationLockedUsers = await _context.Users.CountAsync(u =>
                !u.IsDeleted &&
                !u.IsActive &&
                u.AdminNote != null &&
                u.AdminNote.Contains("AUTO-KHÓA DO VI PHẠM"));
            ViewBag.UnverifiedUsers = await _context.Users.CountAsync(u => !u.IsDeleted && (u.IsEmailVerified == false || u.IsEmailVerified == null));
            ViewBag.TrashCount = await _context.Users.CountAsync(u => u.IsDeleted);

            var roles = await _context.Roles.ToListAsync();

            ViewBag.Roles = roles;
            ViewBag.RoleDictionary = roles.ToDictionary(r => r.RoleID, r => r.RoleName);

            int totalItems = await query.CountAsync();
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

            if (totalPages <= 0)
            {
                totalPages = 1;
            }

            if (page < 1)
            {
                page = 1;
            }

            if (page > totalPages)
            {
                page = totalPages;
            }

            var users = await query
                .OrderByDescending(u => u.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var pageUserIds = users.Select(u => u.UserID).ToList();

            ViewBag.ActiveViolationCounts = await _context.UserViolations
                .Where(v => pageUserIds.Contains(v.UserID) && v.Status == "Active")
                .GroupBy(v => v.UserID)
                .Select(g => new { UserID = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.UserID, x => x.Count);

            ViewBag.SearchString = searchString;
            ViewBag.RoleId = roleId;
            ViewBag.VerifyStatus = verifyStatus;
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;

            ViewData["Title"] = "Hệ thống Quản lý Người dùng";

            return View(users);
        }
        [HttpGet]
        public async Task<IActionResult> ExportCsv(string searchString, int? roleId)
        {
            var query = _context.Users.Where(u => !u.IsDeleted);

            if (!string.IsNullOrEmpty(searchString))
            {
                string searchLower = searchString.ToLower();
                query = query.Where(u => u.Username.ToLower().Contains(searchLower) || u.Email.ToLower().Contains(searchLower));
            }
            if (roleId.HasValue && roleId.Value > 0)
                query = query.Where(u => u.RoleID == roleId.Value);

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

            int adminId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
            await _auditLogService.LogAsync(adminId, "Xuất file danh sách người dùng (CSV)", "Users", "Export CSV", severity: "Info");

            return File(bytes, "text/csv", $"DanhSachNguoiDung_{DateTime.Now:yyyyMMddHHmmss}.csv");
        }

        [HttpGet]
        public async Task<IActionResult> Create()
        {
            ViewBag.RolesList = new SelectList(await _context.Roles.ToListAsync(), "RoleID", "RoleName");
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(User user, IFormFile AvatarFile)
        {
            ViewBag.RolesList = new SelectList(await _context.Roles.ToListAsync(), "RoleID", "RoleName", user.RoleID);

            try
            {
                if (!ModelState.IsValid)
                {
                    var errors = string.Join(" | ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
                    TempData["Error"] = "Dữ liệu chưa hợp lệ: " + errors;
                    return View(user);
                }

                if (await _context.Users.AnyAsync(u => u.Username == user.Username))
                {
                    ModelState.AddModelError("Username", "Tên đăng nhập đã tồn tại.");
                    TempData["Error"] = "Tên đăng nhập đã tồn tại.";
                    return View(user);
                }

                if (await _context.Users.AnyAsync(u => u.Email == user.Email))
                {
                    ModelState.AddModelError("Email", "Email này đã được sử dụng trong hệ thống.");
                    TempData["Error"] = "Email này đã được sử dụng trong hệ thống.";
                    return View(user);
                }

                if (AvatarFile != null && AvatarFile.Length > 0)
                {
                    string uploadDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "avatars");
                    if (!Directory.Exists(uploadDir)) Directory.CreateDirectory(uploadDir);

                    string fileName = Guid.NewGuid().ToString() + Path.GetExtension(AvatarFile.FileName);
                    string filePath = Path.Combine(uploadDir, fileName);

                    using (var stream = new FileStream(filePath, FileMode.Create)) { await AvatarFile.CopyToAsync(stream); }
                    user.Avatar = "/uploads/avatars/" + fileName;
                }
                else
                {
                    user.Avatar = "/images/avatars/default-user.png";
                }

                string rawPassword = user.Password;
                user.Password = PasswordHasher.HashPassword(user.Password);
                user.CreatedAt = DateTime.Now;
                user.IsDeleted = false;
                user.IsEmailVerified = true;

                _context.Users.Add(user);
                await _context.SaveChangesAsync();

                // GHI LOG
                int adminId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
                await _auditLogService.LogAsync(adminId, "Tạo tài khoản người dùng mới", "Users", $"UserID: {user.UserID} - {user.Email}", severity: "Info");

                bool isBusiness = Request.Form["IsBusiness"] == "on";
                if (isBusiness)
                {
                    var bizProfile = new BusinessProfile
                    {
                        UserID = user.UserID,
                        BusinessName = Request.Form["BusinessName"],
                        TaxCode = Request.Form["TaxCode"],
                        BusinessEmail = Request.Form["BusinessEmail"],
                        VerificationStatus = "Approved",
                        CreatedAt = DateTime.Now,
                        RepresentativeName = user.FullName ?? user.Username,
                        RepresentativePhone = user.Phone ?? "N/A",
                        BusinessAddress = user.Address ?? "N/A"
                    };

                    _context.BusinessProfiles.Add(bizProfile);
                    await _context.SaveChangesAsync();
                }

                try
                {
                    var role = await _context.Roles.FindAsync(user.RoleID);
                    string roleName = role?.RoleName ?? "Thành viên";

                    string subject = "Cấp tài khoản truy cập hệ thống BĐS Khánh Hòa";
                    string htmlContent = $@"
<div style='font-family:Arial,sans-serif;max-width:600px;margin:auto;padding:20px;border:1px solid #e2e8f0;border-radius:10px;'>
    <h2 style='color:#2563eb;text-align:center;'>CHÀO MỪNG ĐẾN VỚI BĐS KHÁNH HÒA</h2>
    <p>Xin chào <b>{user.FullName ?? user.Username}</b>,</p>
    <p>Ban quản trị đã tạo cho bạn một tài khoản với vai trò: <b>{roleName}</b>.</p>
    <div style='background:#f8fafc;padding:15px;border-radius:8px;margin:20px 0;'>
        <p style='margin:5px 0;'><b>Tên đăng nhập:</b> <span style='color:#dc2626;font-weight:bold;'>{user.Username}</span></p>
        <p style='margin:5px 0;'><b>Mật khẩu:</b> <span style='color:#dc2626;font-weight:bold;'>{rawPassword}</span></p>
        <p style='margin:5px 0;'><b>Email hệ thống:</b> {user.Email}</p>
    </div>
    <p style='color:#ef4444;font-size:0.9em;'><i>*Vui lòng đổi mật khẩu sau lần đăng nhập đầu tiên.</i></p>
</div>";

                    await _emailService.SendEmailAsync(user.Email, subject, htmlContent);
                    TempData["Success"] = $"Tạo tài khoản thành công! Đã gửi thông tin đăng nhập đến email: {user.Email}";
                }
                catch (Exception exEmail)
                {
                    TempData["Warning"] = "Tài khoản đã tạo thành công, nhưng gửi email bị lỗi: " + exEmail.Message;
                }

                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                TempData["Error"] = "Lỗi hệ thống khi tạo tài khoản: " + ex.Message;
                ModelState.AddModelError("", ex.Message);
                return View(user);
            }
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound();

            ViewBag.RolesList = new SelectList(await _context.Roles.ToListAsync(), "RoleID", "RoleName", user.RoleID);
            ViewBag.BusinessProfile = await _context.BusinessProfiles.FirstOrDefaultAsync(b => b.UserID == id);

            return View(user);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, User user)
        {
            if (id != user.UserID) return NotFound();

            var existingUser = await _context.Users.FirstOrDefaultAsync(u => u.UserID == id);
            if (existingUser == null) return NotFound();

            ModelState.Remove("Password");
            ModelState.Remove("ConfirmPassword");
            ModelState.Remove("Username");
            ModelState.Remove("Email");

            if (ModelState.IsValid)
            {
                try
                {
                    var oldData = JsonSerializer.Serialize(new { existingUser.FullName, existingUser.Phone, existingUser.RoleID });

                    existingUser.FullName = user.FullName;
                    existingUser.Phone = user.Phone;
                    existingUser.Address = user.Address;
                    existingUser.Zalo = user.Zalo;
                    existingUser.Position = user.Position;
                    existingUser.AdminNote = user.AdminNote;
                    existingUser.RoleID = user.RoleID;
                    existingUser.IsActive = user.IsActive;

                    bool isBusinessChecked = Request.Form["IsBusiness"] == "on";
                    var bizProfile = await _context.BusinessProfiles.FirstOrDefaultAsync(b => b.UserID == id);

                    if (isBusinessChecked)
                    {
                        if (bizProfile == null)
                        {
                            bizProfile = new BusinessProfile
                            {
                                UserID = existingUser.UserID,
                                BusinessName = Request.Form["BusinessName"],
                                TaxCode = Request.Form["TaxCode"],
                                BusinessEmail = Request.Form["BusinessEmail"],
                                VerificationStatus = "Approved",
                                CreatedAt = DateTime.Now,
                                RepresentativeName = existingUser.FullName ?? existingUser.Username,
                                RepresentativePhone = existingUser.Phone ?? "N/A",
                                BusinessAddress = existingUser.Address ?? "N/A"
                            };
                            _context.BusinessProfiles.Add(bizProfile);
                        }
                        else
                        {
                            bizProfile.BusinessName = Request.Form["BusinessName"];
                            bizProfile.TaxCode = Request.Form["TaxCode"];
                            bizProfile.BusinessEmail = Request.Form["BusinessEmail"];
                            _context.BusinessProfiles.Update(bizProfile);
                        }
                    }

                    await _context.SaveChangesAsync();

                    var newData = JsonSerializer.Serialize(new { existingUser.FullName, existingUser.Phone, existingUser.RoleID });

                    // GHI LOG
                    int adminId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
                    await _auditLogService.LogAsync(adminId, "Cập nhật hồ sơ người dùng", "Users", $"UserID: {existingUser.UserID}", oldData, newData, "Info");

                    TempData["Success"] = "Cập nhật hồ sơ " + existingUser.Username + " thành công!";
                    return RedirectToAction(nameof(Index));
                }
                catch (Exception ex)
                {
                    TempData["Error"] = "Lỗi Database: " + ex.Message;
                }
            }
            else
            {
                var errors = string.Join(" | ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
                TempData["Error"] = "Dữ liệu chưa chuẩn: " + errors;
            }

            ViewBag.RolesList = new SelectList(await _context.Roles.ToListAsync(), "RoleID", "RoleName", user.RoleID);
            ViewBag.BusinessProfile = await _context.BusinessProfiles.FirstOrDefaultAsync(b => b.UserID == id);
            return View(user);
        }

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
                await _context.SaveChangesAsync();

                // DÙNG IAUDITLOGSERVICE THAY CHO MANUAL
                int adminId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
                await _auditLogService.LogAsync(adminId, $"Cấp lại mật khẩu cho tài khoản: {user.Username}", "Users", $"UserID: {user.UserID}", severity: "Warning");

                TempData["Success"] = "Đã đặt lại mật khẩu thành công cho tài khoản " + user.Username;
            }
            else
            {
                TempData["Error"] = "Mật khẩu mới phải có ít nhất 6 ký tự.";
            }

            return RedirectToAction(nameof(Edit), new { id = user.UserID });
        }


        [HttpPost]
        public async Task<IActionResult> ToggleStatus(int id)
        {
            var user = await _context.Users.FindAsync(id);

            if (user == null)
            {
                return Json(new
                {
                    success = false,
                    message = "Không tìm thấy người dùng."
                });
            }

            string? currentAdminId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (id.ToString() == currentAdminId)
            {
                return Json(new
                {
                    success = false,
                    message = "Lỗi bảo mật: Bạn không thể tự khóa tài khoản của chính mình."
                });
            }

            if ((user.IsEmailVerified == false || user.IsEmailVerified == null) && user.IsActive == false)
            {
                return Json(new
                {
                    success = false,
                    needVerify = true,
                    message = "Tài khoản này chưa xác thực Email OTP nên Admin không được phép mở khóa. Người dùng phải nhập đúng mã OTP trước."
                });
            }

            int activeViolationCount = await _context.UserViolations
                .CountAsync(v => v.UserID == id && v.Status == "Active");

            bool isOpening = !user.IsActive;

            // Tài khoản bị khóa do đủ 3 lỗi chỉ nên mở lại bằng chức năng ân xá/xóa lỗi,
            // để tránh mở nhầm khi hồ sơ vi phạm vẫn còn hiệu lực.
            if (isOpening && activeViolationCount >= 3)
            {
                return Json(new
                {
                    success = false,
                    lockedByViolation = true,
                    activeViolationCount,
                    message = $"Tài khoản đang có {activeViolationCount}/3 lỗi vi phạm đang hiệu lực nên không thể mở khóa thủ công. Vui lòng vào hồ sơ người dùng hoặc hồ sơ báo cáo để ân xá/xử lý lỗi trước."
                });
            }

            user.IsActive = !user.IsActive;

            string adminNoteLine = user.IsActive
                ? $"[MỞ KHÓA THỦ CÔNG - {DateTime.Now:dd/MM/yyyy HH:mm}] Admin mở khóa tài khoản. Số lỗi đang hiệu lực: {activeViolationCount}/3."
                : $"[KHÓA THỦ CÔNG - {DateTime.Now:dd/MM/yyyy HH:mm}] Admin khóa tài khoản thủ công. Số lỗi đang hiệu lực: {activeViolationCount}/3.";

            user.AdminNote = string.IsNullOrWhiteSpace(user.AdminNote)
                ? adminNoteLine
                : user.AdminNote + Environment.NewLine + adminNoteLine;

            await _context.SaveChangesAsync();

            await _auditLogService.LogAsync(
                int.Parse(currentAdminId ?? "0"),
                user.IsActive ? "Mở khóa tài khoản" : "Khóa tài khoản",
                "Users",
                $"UserID: {id}, IsEmailVerified: {user.IsEmailVerified}, ActiveViolations: {activeViolationCount}",
                severity: user.IsActive ? "Info" : "Warning");

            return Json(new
            {
                success = true,
                isActive = user.IsActive,
                isEmailVerified = user.IsEmailVerified == true,
                activeViolationCount,
                message = user.IsActive ? "Đã mở khóa tài khoản." : "Đã khóa tài khoản."
            });
        }

        [HttpPost]
        public async Task<IActionResult> SoftDelete(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return Json(new { success = false, message = "Không tìm thấy người dùng" });

            var currentAdminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (id.ToString() == currentAdminId)
                return Json(new { success = false, message = "Không thể tự xóa chính mình!" });

            if (user.RoleID == 1)
            {
                int adminCount = await _context.Users.CountAsync(u => u.RoleID == 1 && !u.IsDeleted);
                if (adminCount <= 1)
                    return Json(new { success = false, message = "Hệ thống phải có ít nhất 1 Quản trị viên!" });
            }

            user.IsDeleted = true;
            user.IsActive = false;
            await _context.SaveChangesAsync();

            // DÙNG IAUDITLOGSERVICE THAY CHO MANUAL
            await _auditLogService.LogAsync(int.Parse(currentAdminId ?? "0"), $"Đưa tài khoản {user.Username} vào thùng rác", "Users", $"UserID: {id}", severity: "Danger");

            return Json(new { success = true });
        }

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

            // GHI LOG
            int adminId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
            await _auditLogService.LogAsync(adminId, $"Khôi phục tài khoản {user.Username} từ thùng rác", "Users", $"UserID: {id}", severity: "Info");

            return Json(new { success = true });
        }

        [HttpGet]
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null) return NotFound();

            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound();

            ViewBag.Transactions = await _context.Transactions
                .Where(t => t.UserID == id)
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync();

            ViewBag.Logs = await _context.AuditLogs
                .Where(l => l.UserID == id)
                .OrderByDescending(l => l.CreatedAt)
                .Take(20)
                .ToListAsync();

            ViewBag.Violations = await _context.UserViolations
                .Where(v => v.UserID == id)
                .OrderByDescending(v => v.CreatedAt)
                .ToListAsync();

            ViewBag.ActiveViolationCount = await _context.UserViolations
                .CountAsync(v => v.UserID == id && v.Status == "Active");

            ViewBag.IsLockedByViolation = user.AdminNote != null &&
                user.AdminNote.Contains("AUTO-KHÓA DO VI PHẠM");

            return View(user);
        }

        [HttpPost]
        public async Task<IActionResult> DeleteForever(int id)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var user = await _context.Users.FindAsync(id);
                if (user == null)
                    return Json(new { success = false, message = "Không tìm thấy người dùng trong hệ thống." });

                bool hasTransactions = await _context.Transactions.AnyAsync(t => t.UserID == id);
                if (hasTransactions)
                {
                    return Json(new
                    {
                        success = false,
                        message = "LỖI NGHIỆP VỤ: Không thể xóa vĩnh viễn người dùng đã phát sinh giao dịch tài chính."
                    });
                }

                var bizProfile = await _context.BusinessProfiles.FirstOrDefaultAsync(b => b.UserID == id);
                if (bizProfile != null) _context.BusinessProfiles.Remove(bizProfile);

                var favorites = await _context.Favorites.Where(f => f.UserID == id).ToListAsync();
                if (favorites.Any()) _context.Favorites.RemoveRange(favorites);

                var notifications = await _context.Notifications.Where(n => n.UserID == id).ToListAsync();
                if (notifications.Any()) _context.Notifications.RemoveRange(notifications);

                var violations = await _context.UserViolations.Where(v => v.UserID == id).ToListAsync();
                if (violations.Any()) _context.UserViolations.RemoveRange(violations);

                var comments = await _context.Comments.Where(c => c.UserID == id).ToListAsync();
                if (comments.Any()) _context.Comments.RemoveRange(comments);

                var properties = await _context.Properties.Where(p => p.UserID == id).ToListAsync();
                if (properties.Any())
                {
                    var propIds = properties.Select(p => p.PropertyID).ToList();

                    var propImages = await _context.PropertyImages.Where(pi => propIds.Contains(pi.PropertyID)).ToListAsync();
                    if (propImages.Any()) _context.PropertyImages.RemoveRange(propImages);

                    var propFeatures = await _context.PropertyFeatures.Where(pf => pf.PropertyID.HasValue && propIds.Contains(pf.PropertyID.Value)).ToListAsync();
                    if (propFeatures.Any()) _context.PropertyFeatures.RemoveRange(propFeatures);

                    _context.Properties.RemoveRange(properties);
                }

                _context.Users.Remove(user);

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                // GHI LOG
                int adminId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
                await _auditLogService.LogAsync(adminId, $"Xóa vĩnh viễn tài khoản {user.Username}", "Users", $"UserID: {id}", severity: "Critical");

                return Json(new { success = true, message = "Đã xóa vĩnh viễn người dùng cùng dữ liệu liên quan." });
            }
            catch
            {
                await transaction.RollbackAsync();
                return Json(new { success = false, message = "Không thể xóa do ràng buộc dữ liệu liên kết." });
            }
        }
    }
}