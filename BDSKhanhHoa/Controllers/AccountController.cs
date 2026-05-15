using BDSKhanhHoa.Data;
using BDSKhanhHoa.Helpers;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.Services;
using BDSKhanhHoa.ViewModels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System.Security.Claims;

namespace BDSKhanhHoa.Controllers
{
    public class AccountController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly IEmailService _emailSender;
        private readonly IWebHostEnvironment _hostEnvironment;
        private readonly IAuditLogService _auditLogService; // Thêm Service Log

        private const string SESSION_RESET_TOKEN = "ResetToken";
        private const string SESSION_RESET_EMAIL = "ResetEmail";
        private const string SESSION_OTP = "RegOTP";
        private const string SESSION_USER = "PendingUser";
        private const string SESSION_USER_ID = "PendingUserID";

        public AccountController(
            ApplicationDbContext db,
            IEmailService emailSender,
            IWebHostEnvironment hostEnvironment,
            IAuditLogService auditLogService)
        {
            _db = db;
            _emailSender = emailSender;
            _hostEnvironment = hostEnvironment;
            _auditLogService = auditLogService;
        }

        private async Task UpdateUserClaims(User user)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.UserID.ToString()),
                new Claim(ClaimTypes.Name, user.Username ?? ""),
                new Claim(ClaimTypes.Email, user.Email ?? ""),
                new Claim(ClaimTypes.Role, user.RoleID == 1 ? "Admin" : user.RoleID == 2 ? "Staff" : "Member"),
                new Claim("FullName", user.FullName ?? "Người dùng"),
                new Claim("Avatar", string.IsNullOrEmpty(user.Avatar) ? "/images/avatars/default-user.png" : user.Avatar)
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTime.UtcNow.AddDays(7) });
        }

        private async Task GrantWelcomeFreeCredits(int userId)
        {
            var normalPackage = await _db.PostServicePackages.FirstOrDefaultAsync(p => p.PackageType == "Tin Thường" || p.Price == 0)
                ?? await _db.PostServicePackages.OrderBy(p => p.Price).FirstOrDefaultAsync();

            if (normalPackage != null)
            {
                for (int i = 0; i < 5; i++)
                {
                    _db.Transactions.Add(new Transaction
                    {
                        UserID = userId,
                        PackageID = normalPackage.PackageID,
                        Amount = 0,
                        Type = "Tặng lượt đăng tin thường",
                        PaymentMethod = "System Gift",
                        TransactionCode = "WELCOME" + DateTime.Now.ToString("yyyyMMddHHmmss") + userId + i,
                        Status = "Success",
                        CreatedAt = DateTime.Now
                    });
                }
                await _db.SaveChangesAsync();
            }
        }

        [AllowAnonymous]
        [Route("Nguoi-Dang-Tin/{id}")]
        public async Task<IActionResult> UserProfile(int id)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.UserID == id && !u.IsDeleted);
            if (user == null)
            {
                TempData["Error"] = "Người dùng này không tồn tại hoặc đã bị khóa tài khoản.";
                return RedirectToAction("Index", "Home");
            }

            var activeProperties = await _db.Properties
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Include(p => p.PropertyType)
                .Include(p => p.PostServicePackage)
                .Where(p => p.UserID == id && p.Status == "Approved" && p.IsDeleted == false)
                .OrderByDescending(p => p.PackageID)
                .ThenByDescending(p => p.CreatedAt)
                .ToListAsync();

            ViewBag.ActiveProperties = activeProperties;
            ViewBag.TotalActive = activeProperties.Count;

            int completionRate = 20;
            if (!string.IsNullOrEmpty(user.FullName)) completionRate += 20;
            if (!string.IsNullOrEmpty(user.Phone)) completionRate += 20;
            if (user.Avatar != null && !user.Avatar.Contains("default")) completionRate += 10;
            if (!string.IsNullOrEmpty(user.Address)) completionRate += 10;
            if (!string.IsNullOrEmpty(user.Zalo)) completionRate += 10;
            if (!string.IsNullOrEmpty(user.Facebook)) completionRate += 10;

            ViewBag.TrustScore = completionRate;
            return View(user);
        }

        #region ĐĂNG KÝ & ĐĂNG NHẬP

        [HttpGet]
        public IActionResult Register()
        {
            return View(new RegisterViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(RegisterViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            model.Email = model.Email.Trim().ToLower();
            model.Username = model.Username.Trim();

            bool usernameExists = await _db.Users
                .AnyAsync(u => u.Username.ToLower() == model.Username.ToLower() && !u.IsDeleted);

            if (usernameExists)
            {
                ModelState.AddModelError("Username", "Tên đăng nhập này đã tồn tại. Vui lòng chọn tên khác.");
                return View(model);
            }

            var existUser = await _db.Users.FirstOrDefaultAsync(u => u.Email == model.Email);

            if (existUser != null)
            {
                if (existUser.IsEmailVerified == true)
                {
                    ModelState.AddModelError("Email", "Email này đã được đăng ký và xác thực. Vui lòng đăng nhập.");
                    return View(model);
                }

                _db.Users.Remove(existUser);
                await _db.SaveChangesAsync();
            }

            string avatarPath = "/images/avatars/default-user.png";

            if (model.AvatarFile != null && model.AvatarFile.Length > 0)
            {
                string[] allowedExtensions = { ".jpg", ".jpeg", ".png", ".webp" };
                string extension = Path.GetExtension(model.AvatarFile.FileName).ToLower();

                if (!allowedExtensions.Contains(extension))
                {
                    ModelState.AddModelError("AvatarFile", "Ảnh đại diện chỉ hỗ trợ JPG, JPEG, PNG hoặc WEBP.");
                    return View(model);
                }

                if (model.AvatarFile.Length > 2 * 1024 * 1024)
                {
                    ModelState.AddModelError("AvatarFile", "Ảnh đại diện không được vượt quá 2MB.");
                    return View(model);
                }

                string uploadDir = Path.Combine(_hostEnvironment.WebRootPath, "images", "avatars");

                if (!Directory.Exists(uploadDir))
                {
                    Directory.CreateDirectory(uploadDir);
                }

                string fileName = "avatar_" + Guid.NewGuid().ToString("N") + extension;
                string filePath = Path.Combine(uploadDir, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await model.AvatarFile.CopyToAsync(stream);
                }

                avatarPath = "/images/avatars/" + fileName;
            }

            var newUser = new User
            {
                FullName = model.FullName.Trim(),
                Email = model.Email,
                Username = model.Username,
                Phone = model.Phone.Trim(),
                Address = model.Address,
                Password = PasswordHasher.HashPassword(model.Password),
                Avatar = avatarPath,
                RoleID = 3,
                IsActive = false,
                IsDeleted = false,
                IsEmailVerified = false,
                CreatedAt = DateTime.Now
            };

            _db.Users.Add(newUser);
            await _db.SaveChangesAsync();

            await _auditLogService.LogAsync(
                newUser.UserID,
                "Đăng ký tài khoản mới - Chờ xác thực OTP",
                "Authentication",
                $"Email: {newUser.Email}, Username: {newUser.Username}",
                severity: "Info");

            string otpCode = new Random().Next(100000, 999999).ToString();

            HttpContext.Session.SetString(SESSION_OTP, otpCode);
            HttpContext.Session.SetInt32(SESSION_USER_ID, newUser.UserID);

            await SendOtpEmailAsync(newUser.Email, newUser.FullName ?? newUser.Username, otpCode);

            Console.WriteLine($"[HỆ THỐNG DEV] MÃ OTP CHO {newUser.Email} LÀ: {otpCode}");

            TempData["EmailToVerify"] = newUser.Email;
            TempData["Warning"] = "Tài khoản đã được tạo nhưng đang bị khóa tạm thời. Vui lòng nhập OTP để xác thực và mở khóa tài khoản.";

            return RedirectToAction("VerifyOTP");
        }

        [HttpGet]
        public async Task<IActionResult> VerifyOTP()
        {
            int? pendingUserId = HttpContext.Session.GetInt32(SESSION_USER_ID);

            if (pendingUserId == null)
            {
                return RedirectToAction("Register");
            }

            var user = await _db.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.UserID == pendingUserId.Value && !u.IsDeleted);

            if (user == null)
            {
                HttpContext.Session.Remove(SESSION_OTP);
                HttpContext.Session.Remove(SESSION_USER_ID);
                return RedirectToAction("Register");
            }

            if (user.IsEmailVerified == true && user.IsActive == true)
            {
                return RedirectToAction("Login");
            }

            ViewBag.EmailToVerify = user.Email;
            TempData["EmailToVerify"] = user.Email;

            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VerifyOTP(string otp)
        {
            string? serverOtp = HttpContext.Session.GetString(SESSION_OTP);
            int? pendingUserId = HttpContext.Session.GetInt32(SESSION_USER_ID);

            if (pendingUserId == null || string.IsNullOrWhiteSpace(serverOtp))
            {
                return RedirectToAction("Register");
            }

            var user = await _db.Users
                .FirstOrDefaultAsync(u => u.UserID == pendingUserId.Value && !u.IsDeleted);

            if (user == null)
            {
                HttpContext.Session.Remove(SESSION_OTP);
                HttpContext.Session.Remove(SESSION_USER_ID);
                return RedirectToAction("Register");
            }

            ViewBag.EmailToVerify = user.Email;
            TempData["EmailToVerify"] = user.Email;

            otp = (otp ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(otp) || otp.Length != 6)
            {
                ViewBag.Error = "Vui lòng nhập đầy đủ mã OTP gồm 6 số.";
                return View();
            }

            if (otp == serverOtp)
            {
                user.IsEmailVerified = true;
                user.IsActive = true;

                await _db.SaveChangesAsync();

                HttpContext.Session.Remove(SESSION_OTP);
                HttpContext.Session.Remove(SESSION_USER_ID);

                await GrantWelcomeFreeCredits(user.UserID);

                await _auditLogService.LogAsync(
                    user.UserID,
                    "Xác thực OTP Email thành công - Mở khóa tài khoản",
                    "Authentication",
                    $"Email: {user.Email}, Username: {user.Username}",
                    severity: "Info");

                TempData["Success"] = "Xác thực thành công! Tài khoản của bạn đã được mở khóa và được tặng 5 lượt đăng tin miễn phí. Hãy đăng nhập để trải nghiệm.";

                return RedirectToAction("Login");
            }

            await _auditLogService.LogAsync(
                user.UserID,
                "Xác thực OTP Email thất bại",
                "Authentication",
                $"Email: {user.Email}",
                severity: "Warning");

            ViewBag.Error = "Mã OTP không chính xác. Vui lòng thử lại.";
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResendOTP()
        {
            int? pendingUserId = HttpContext.Session.GetInt32(SESSION_USER_ID);

            if (pendingUserId == null)
            {
                TempData["Error"] = "Phiên xác thực đã hết hạn. Vui lòng đăng ký hoặc đăng nhập lại.";
                return RedirectToAction("Register");
            }

            var user = await _db.Users
                .FirstOrDefaultAsync(u => u.UserID == pendingUserId.Value && !u.IsDeleted);

            if (user == null)
            {
                HttpContext.Session.Remove(SESSION_OTP);
                HttpContext.Session.Remove(SESSION_USER_ID);
                TempData["Error"] = "Không tìm thấy tài khoản cần xác thực.";
                return RedirectToAction("Register");
            }

            if (user.IsEmailVerified == true && user.IsActive == true)
            {
                TempData["Success"] = "Tài khoản đã được xác thực. Vui lòng đăng nhập.";
                return RedirectToAction("Login");
            }

            string newOtp = new Random().Next(100000, 999999).ToString();

            HttpContext.Session.SetString(SESSION_OTP, newOtp);
            HttpContext.Session.SetInt32(SESSION_USER_ID, user.UserID);

            await SendOtpEmailAsync(user.Email, user.FullName ?? user.Username, newOtp);

            Console.WriteLine($"[HỆ THỐNG DEV] MÃ OTP GỬI LẠI CHO {user.Email} LÀ: {newOtp}");

            await _auditLogService.LogAsync(
                user.UserID,
                "Gửi lại mã OTP Email",
                "Authentication",
                $"Email: {user.Email}",
                severity: "Info");

            TempData["EmailToVerify"] = user.Email;
            TempData["Warning"] = "Mã OTP mới đã được gửi lại. Vui lòng kiểm tra Email.";

            return RedirectToAction("VerifyOTP");
        }

        [HttpGet]
        public IActionResult Login()
        {
            return View(new LoginViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            string accountName = model.AccountName.Trim();

            var user = await _db.Users
                .FirstOrDefaultAsync(u =>
                    (u.Email == accountName || u.Username == accountName)
                    && !u.IsDeleted);

            if (user != null && PasswordHasher.VerifyPassword(model.Password, user.Password))
            {
                if (user.IsEmailVerified == false || user.IsEmailVerified == null)
                {
                    string newOtp = new Random().Next(100000, 999999).ToString();

                    HttpContext.Session.SetString(SESSION_OTP, newOtp);
                    HttpContext.Session.SetInt32(SESSION_USER_ID, user.UserID);

                    await SendOtpEmailAsync(user.Email, user.FullName ?? user.Username, newOtp);

                    await _auditLogService.LogAsync(
                        user.UserID,
                        "Đăng nhập bị chặn do chưa xác thực Email",
                        "Authentication",
                        $"Email: {user.Email}",
                        severity: "Warning");

                    TempData["Warning"] = "Tài khoản của bạn chưa xác thực Email. Hệ thống vừa gửi lại mã OTP. Vui lòng xác thực để mở khóa tài khoản.";
                    TempData["EmailToVerify"] = user.Email;

                    return RedirectToAction("VerifyOTP");
                }

                if (user.IsActive == false)
                {
                    await _auditLogService.LogAsync(
                        user.UserID,
                        "Đăng nhập thất bại - Tài khoản bị khóa",
                        "Authentication",
                        $"Email/Username: {model.AccountName}",
                        severity: "Warning");

                    ModelState.AddModelError("", "Tài khoản của bạn đang bị khóa bởi hệ thống hoặc Ban Quản Trị.");
                    return View(model);
                }

                await UpdateUserClaims(user);

                await _auditLogService.LogAsync(
                    user.UserID,
                    "Đăng nhập hệ thống",
                    "Authentication",
                    $"RoleID: {user.RoleID}",
                    severity: "Info");

                if (user.RoleID == 1)
                {
                    return RedirectToAction("Index", "Dashboard", new { area = "Admin" });
                }

                if (user.RoleID == 2)
                {
                    return RedirectToAction("Index", "StaffDashboard", new { area = "Admin" });
                }

                return RedirectToAction("Index", "Home");
            }

            ModelState.AddModelError("", "Tài khoản, mật khẩu không đúng hoặc tài khoản không tồn tại.");
            return View(model);
        }

        [AllowAnonymous]
        [HttpGet("Account/LoginGoogle")]
        public IActionResult LoginGoogle()
        {
            var properties = new AuthenticationProperties { RedirectUri = Url.Action("GoogleResponse") };
            return Challenge(properties, GoogleDefaults.AuthenticationScheme);
        }

        [AllowAnonymous]
        [HttpGet("Account/GoogleResponse")]
        public async Task<IActionResult> GoogleResponse()
        {
            var result = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            if (!result.Succeeded)
            {
                return RedirectToAction("Login");
            }

            var claims = result.Principal.Identities.FirstOrDefault()?.Claims;
            string? email = claims?.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;
            string? name = claims?.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value;

            if (string.IsNullOrWhiteSpace(email))
            {
                TempData["Error"] = "Không lấy được Email từ Google. Vui lòng thử lại.";
                return RedirectToAction("Login");
            }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);

            if (user == null)
            {
                string googleUsername = email.Contains("@")
                    ? email.Split('@')[0]
                    : "google_user";

                string baseUsername = googleUsername;
                int counter = 1;

                while (await _db.Users.AnyAsync(u => u.Username == googleUsername))
                {
                    googleUsername = baseUsername + counter;
                    counter++;
                }

                user = new User
                {
                    FullName = name,
                    Email = email,
                    Username = googleUsername,
                    Password = "GOOGLE_AUTH",
                    RoleID = 3,
                    IsEmailVerified = true,
                    CreatedAt = DateTime.Now,
                    Avatar = "/images/avatars/default-user.png",
                    IsActive = true,
                    IsDeleted = false
                };

                _db.Users.Add(user);
                await _db.SaveChangesAsync();

                await GrantWelcomeFreeCredits(user.UserID);

                await _auditLogService.LogAsync(
                    user.UserID,
                    "Đăng ký bằng Google",
                    "Authentication",
                    $"Email: {email}",
                    severity: "Info");
            }

            if (user.IsEmailVerified == false || user.IsEmailVerified == null)
            {
                user.IsEmailVerified = true;
                user.IsActive = true;
                await _db.SaveChangesAsync();
            }

            if (user.IsActive == false)
            {
                await _auditLogService.LogAsync(
                    user.UserID,
                    "Đăng nhập thất bại - Google - Tài khoản bị khóa",
                    "Authentication",
                    $"Email: {email}",
                    severity: "Warning");

                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

                TempData["Error"] = "Tài khoản của bạn đã bị khóa bởi hệ thống hoặc Ban Quản Trị.";
                return RedirectToAction("Login");
            }

            await UpdateUserClaims(user);

            await _auditLogService.LogAsync(
                user.UserID,
                "Đăng nhập bằng Google",
                "Authentication",
                $"Email: {email}",
                severity: "Info");

            return RedirectToAction("Index", "Home");
        }

        [HttpPost]
        public async Task<IActionResult> Logout()
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (int.TryParse(userIdStr, out int userId))
            {
                await _auditLogService.LogAsync(
                    userId,
                    "Đăng xuất hệ thống",
                    "Authentication",
                    "User Session",
                    severity: "Info");
            }

            await HttpContext.SignOutAsync();
            return RedirectToAction("Index", "Home");
        }

        #endregion

        #region HỒ SƠ CÁ NHÂN & ĐỔI MẬT KHẨU
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> Profile()
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId)) return RedirectToAction("Login");

            var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserID == userId && !u.IsDeleted);
            if (user == null) return NotFound();

            ViewBag.TotalProps = await _db.Properties.CountAsync(p => p.UserID == userId && p.IsDeleted == false);
            ViewBag.TotalProjects = await _db.Projects.CountAsync(p => p.OwnerUserID == userId && p.IsDeleted == false);
            ViewBag.BusinessProfile = await _db.BusinessProfiles.AsNoTracking().FirstOrDefaultAsync(b => b.UserID == userId);

            if (user.RoleID == 1 || user.RoleID == 2)
            {
                ViewBag.PendingAds = await _db.Properties.CountAsync(p => p.Status == "Pending" && p.IsDeleted == false);
                ViewBag.TotalUsers = await _db.Users.CountAsync(u => !u.IsDeleted);
                ViewBag.NewReports = await _db.PropertyReports.CountAsync(r => r.Status == "Pending" && r.IsDeleted == false);
            }

            return View(user);
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateProfile(User model)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (model.UserID.ToString() != userIdStr) return Forbid();

            ModelState.Remove("Username");
            ModelState.Remove("Password");
            ModelState.Remove("ConfirmPassword");
            ModelState.Remove("Email");
            ModelState.Remove("RoleID");

            if (!ModelState.IsValid)
            {
                TempData["Error"] = "Vui lòng kiểm tra lại thông tin nhập vào.";
                return RedirectToAction("Profile");
            }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.UserID == model.UserID);
            if (user != null)
            {
                var oldData = JsonConvert.SerializeObject(new { user.FullName, user.Phone, user.Address });

                user.FullName = model.FullName;
                user.Phone = model.Phone;
                user.Address = model.Address;
                user.Zalo = model.Zalo;
                user.Facebook = model.Facebook;
                user.Bio = model.Bio;

                await _db.SaveChangesAsync();
                await UpdateUserClaims(user);

                var newData = JsonConvert.SerializeObject(new { user.FullName, user.Phone, user.Address });

                await _auditLogService.LogAsync(user.UserID, "Cập nhật hồ sơ cá nhân", "Account", $"User: {user.Email}", oldData, newData, "Info");

                TempData["Success"] = "Cập nhật hồ sơ thành công!";
            }
            return RedirectToAction("Profile");
        }

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> UpdateAvatar(IFormFile AvatarFile)
        {
            if (AvatarFile != null && AvatarFile.Length > 0)
            {
                var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
                var user = await _db.Users.FirstOrDefaultAsync(u => u.UserID.ToString() == userIdStr);

                if (user != null)
                {
                    string uploadDir = Path.Combine(_hostEnvironment.WebRootPath, "images/avatars");
                    if (!Directory.Exists(uploadDir)) Directory.CreateDirectory(uploadDir);
                    string fileName = Guid.NewGuid().ToString() + Path.GetExtension(AvatarFile.FileName);

                    using (var stream = new FileStream(Path.Combine(uploadDir, fileName), FileMode.Create)) { await AvatarFile.CopyToAsync(stream); }

                    user.Avatar = "/images/avatars/" + fileName;
                    await _db.SaveChangesAsync();
                    await UpdateUserClaims(user);

                    await _auditLogService.LogAsync(user.UserID, "Cập nhật ảnh đại diện", "Account", "Avatar Upload");

                    TempData["Success"] = "Cập nhật ảnh đại diện thành công!";
                }
            }
            return RedirectToAction("Profile");
        }

        [Authorize]
        [HttpGet]
        public IActionResult ChangePassword() => View();

        [AllowAnonymous]
        [HttpGet]
        public IActionResult AccessDenied(string? ReturnUrl = null)
        {
            ViewBag.ReturnUrl = ReturnUrl;
            return View();
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangePassword(string OldPassword, string NewPassword, string ConfirmPassword)
        {
            if (NewPassword != ConfirmPassword)
            {
                TempData["Error"] = "Mật khẩu xác nhận không trùng khớp!";
                return View();
            }

            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var user = await _db.Users.FindAsync(userId);

            if (user != null && PasswordHasher.VerifyPassword(OldPassword, user.Password))
            {
                user.Password = PasswordHasher.HashPassword(NewPassword);
                await _db.SaveChangesAsync();

                await _auditLogService.LogAsync(user.UserID, "Đổi mật khẩu", "Account", "Change Password", severity: "Warning");

                TempData["Success"] = "Đổi mật khẩu thành công!";
                return RedirectToAction("Profile");
            }

            TempData["Error"] = "Mật khẩu cũ không chính xác!";
            return View();
        }
        #endregion

        #region QUÊN MẬT KHẨU
        [AllowAnonymous]
        [HttpGet]
        public IActionResult ForgotPassword() => View(new ForgotPasswordViewModel());

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == model.Email && !u.IsDeleted);

            if (user == null)
            {
                ModelState.AddModelError("Email", "Email này không tồn tại trong hệ thống.");
                return View(model);
            }

            string resetToken = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper();
            HttpContext.Session.SetString(SESSION_RESET_TOKEN, resetToken);
            HttpContext.Session.SetString(SESSION_RESET_EMAIL, model.Email);

            Console.WriteLine($"[HỆ THỐNG DEV] MÃ RESET PASSWORD CHO {model.Email} LÀ: {resetToken}");

            try
            {
                string subject = "Yêu cầu khôi phục mật khẩu - Bất Động Sản Khánh Hòa";
                string message = $"Chào {user.FullName},<br><br>Mã xác nhận để đặt lại mật khẩu của bạn là: <strong style='color:red; font-size:18px;'>{resetToken}</strong>.<br>Mã này chỉ có hiệu lực trong phiên làm việc hiện tại.";
                await _emailSender.SendEmailAsync(model.Email, subject, message);
            }
            catch (Exception ex) { Console.WriteLine("Lỗi gửi mail: " + ex.Message); }

            await _auditLogService.LogAsync(user.UserID, "Yêu cầu khôi phục mật khẩu", "Authentication", $"Email: {model.Email}");

            TempData["Success"] = "Mã xác nhận đã được gửi đến Email của bạn.";
            return RedirectToAction("ResetPassword");
        }

        [AllowAnonymous]
        [HttpGet]
        public IActionResult ResetPassword()
        {
            if (string.IsNullOrEmpty(HttpContext.Session.GetString(SESSION_RESET_EMAIL))) return RedirectToAction("ForgotPassword");
            return View();
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(string token, string newPassword)
        {
            var sessionToken = HttpContext.Session.GetString(SESSION_RESET_TOKEN);
            var sessionEmail = HttpContext.Session.GetString(SESSION_RESET_EMAIL);

            if (string.IsNullOrEmpty(sessionToken) || string.IsNullOrEmpty(sessionEmail))
            {
                ViewBag.Error = "Phiên làm việc đã hết hạn.";
                return View();
            }

            if (token.Trim().ToUpper() != sessionToken)
            {
                ViewBag.Error = "Mã xác nhận không chính xác.";
                return View();
            }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == sessionEmail);
            if (user != null)
            {
                user.Password = PasswordHasher.HashPassword(newPassword);
                await _db.SaveChangesAsync();

                HttpContext.Session.Remove(SESSION_RESET_TOKEN);
                HttpContext.Session.Remove(SESSION_RESET_EMAIL);

                await _auditLogService.LogAsync(user.UserID, "Khôi phục mật khẩu thành công", "Authentication", $"Email: {user.Email}", severity: "Warning");

                TempData["Success"] = "Đặt lại mật khẩu thành công! Vui lòng đăng nhập với mật khẩu mới.";
                return RedirectToAction("Login");
            }

            ViewBag.Error = "Đã có lỗi xảy ra.";
            return View();
        }
        #endregion
        private async Task SendOtpEmailAsync(string email, string fullName, string otpCode)
        {
            try
            {
                string subject = "Mã xác thực tài khoản - BĐS Khánh Hòa";

                string htmlContent = $@"
<div style='font-family:Arial,sans-serif;max-width:620px;margin:auto;background:#ffffff;border:1px solid #e5e7eb;border-radius:18px;overflow:hidden;'>
    <div style='background:linear-gradient(135deg,#1d4ed8,#7c3aed);padding:26px;text-align:center;color:#ffffff;'>
        <h2 style='margin:0;font-size:24px;'>BĐS Khánh Hòa</h2>
        <p style='margin:8px 0 0;color:#dbeafe;'>Xác thực tài khoản thành viên</p>
    </div>

    <div style='padding:28px;'>
        <p style='font-size:15px;color:#334155;'>Xin chào <b>{fullName}</b>,</p>
        <p style='font-size:15px;color:#334155;line-height:1.7;'>
            Cảm ơn bạn đã đăng ký tài khoản tại hệ thống BĐS Khánh Hòa.
            Vui lòng nhập mã OTP dưới đây để hoàn tất xác thực Email và mở khóa tài khoản.
        </p>

        <div style='margin:26px 0;text-align:center;'>
            <div style='display:inline-block;background:#eff6ff;border:1px dashed #2563eb;border-radius:16px;padding:18px 28px;'>
                <div style='font-size:13px;color:#64748b;font-weight:bold;margin-bottom:8px;'>MÃ XÁC THỰC OTP</div>
                <div style='font-size:34px;letter-spacing:8px;color:#1d4ed8;font-weight:900;'>{otpCode}</div>
            </div>
        </div>

        <p style='font-size:14px;color:#64748b;line-height:1.7;'>
            Nếu bạn không thực hiện đăng ký, vui lòng bỏ qua email này.
        </p>
    </div>
</div>";

                await _emailSender.SendEmailAsync(email, subject, htmlContent);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Lỗi gửi OTP Email: " + ex.Message);
            }
        }
    }
}