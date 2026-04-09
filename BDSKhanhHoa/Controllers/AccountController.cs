using BDSKhanhHoa.Data;
using BDSKhanhHoa.Helpers;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System.Security.Claims;
using BDSKhanhHoa.ViewModels;

namespace BDSKhanhHoa.Controllers
{
    public class AccountController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly EmailSender _emailSender;
        private readonly IWebHostEnvironment _hostEnvironment;

        private const string SESSION_OTP = "RegOTP";
        private const string SESSION_USER = "PendingUser";
        private const string SESSION_OTP_TIME = "LastOTPTime";

        public AccountController(ApplicationDbContext db, EmailSender emailSender, IWebHostEnvironment hostEnvironment)
        {
            _db = db;
            _emailSender = emailSender;
            _hostEnvironment = hostEnvironment;
        }

        // Cập nhật lại Identity Cookie để hiển thị đúng thông tin trên Header
        private async Task UpdateUserClaims(User user)
        {
            var claims = new List<Claim> {
                new Claim(ClaimTypes.NameIdentifier, user.UserID.ToString()),
                new Claim(ClaimTypes.Name, user.FullName ?? "Người dùng"),
                new Claim(ClaimTypes.Email, user.Email ?? ""),
                new Claim(ClaimTypes.Role, user.RoleID == 1 ? "Admin" : (user.RoleID == 2 ? "Staff" : "Member")),
                new Claim("Avatar", user.Avatar ?? "/images/avatars/default-user.png")
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        }

        #region ĐĂNG KÝ & ĐĂNG NHẬP
        [HttpGet]
        public IActionResult Register() => View(new RegisterViewModel());

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(RegisterViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            var exist = await _db.Users.FirstOrDefaultAsync(u => u.Email == model.Email);
            if (exist != null)
            {
                ModelState.AddModelError("Email", "Email này đã được đăng ký trên hệ thống.");
                return View(model);
            }

            string avatarPath = "/images/avatars/default-user.png";
            if (model.AvatarFile != null)
            {
                string uploadDir = Path.Combine(_hostEnvironment.WebRootPath, "images/avatars");
                if (!Directory.Exists(uploadDir)) Directory.CreateDirectory(uploadDir);
                string fileName = Guid.NewGuid().ToString() + Path.GetExtension(model.AvatarFile.FileName);
                using (var stream = new FileStream(Path.Combine(uploadDir, fileName), FileMode.Create)) { await model.AvatarFile.CopyToAsync(stream); }
                avatarPath = "/images/avatars/" + fileName;
            }

            string otpCode = new Random().Next(100000, 999999).ToString();
            var pendingUser = new User
            {
                FullName = model.FullName,
                Email = model.Email,
                Phone = model.Phone,
                Password = PasswordHasher.HashPassword(model.Password),
                Avatar = avatarPath,
                Username = model.Email,
                Address = model.Address ?? "",
                RoleID = 3, // Member
                IsActive = true,
                IsEmailVerified = true,
                CreatedAt = DateTime.Now
            };

            HttpContext.Session.SetString(SESSION_OTP, otpCode);
            HttpContext.Session.SetString(SESSION_USER, JsonConvert.SerializeObject(pendingUser));

            // Code gửi Email OTP (Mở comment nếu đã cấu hình SMTP)
            // await _emailSender.SendEmailAsync(model.Email, "MÃ XÁC THỰC OTP", $"Mã xác thực BDS Khánh Hòa của bạn là: {otpCode}");

            Console.WriteLine($"OTP CODE CHO {model.Email} LA: {otpCode}");

            return RedirectToAction("VerifyOTP");
        }

        [HttpGet]
        public IActionResult VerifyOTP() => View();

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VerifyOTP(string otp)
        {
            var serverOtp = HttpContext.Session.GetString(SESSION_OTP);
            if (otp == serverOtp) return await FinalizeRegistration();
            ViewBag.Error = "Mã OTP không chính xác. Vui lòng thử lại.";
            return View();
        }

        private async Task<IActionResult> FinalizeRegistration()
        {
            var userJson = HttpContext.Session.GetString(SESSION_USER);
            if (string.IsNullOrEmpty(userJson)) return RedirectToAction("Register");

            var user = JsonConvert.DeserializeObject<User>(userJson);
            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            HttpContext.Session.Remove(SESSION_OTP);
            HttpContext.Session.Remove(SESSION_USER);
            TempData["Success"] = "Đăng ký thành công! Hãy đăng nhập để bắt đầu.";
            return RedirectToAction("Login");
        }

        [HttpGet]
        public IActionResult Login() => View(new LoginViewModel());

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            var user = await _db.Users.FirstOrDefaultAsync(u => (u.Email == model.AccountName || u.Username == model.AccountName) && !u.IsDeleted);

            if (user != null && user.IsActive && PasswordHasher.VerifyPassword(model.Password, user.Password))
            {
                await UpdateUserClaims(user);

                if (user.RoleID == 1 || user.RoleID == 2) return RedirectToAction("Index", "Dashboard", new { area = "Admin" });
                return RedirectToAction("MyAds", "Property");
            }

            ModelState.AddModelError("", "Tài khoản, mật khẩu không đúng hoặc tài khoản bị khóa.");
            return View(model);
        }

        // ===============================================
        // ĐÃ KHẮC PHỤC LỖI 404 CHO ĐĂNG NHẬP GOOGLE TẠI ĐÂY
        // ===============================================
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
            if (!result.Succeeded) return RedirectToAction("Login");

            var claims = result.Principal.Identities.FirstOrDefault().Claims;
            var email = claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;
            var name = claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value;

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);

            if (user == null)
            {
                // Nếu chưa có tài khoản -> Tự động tạo mới
                user = new User
                {
                    FullName = name,
                    Email = email,
                    Username = email,
                    Password = "GOOGLE_AUTH", // Mật khẩu đánh dấu sinh từ Google
                    RoleID = 3, // Member
                    IsEmailVerified = true,
                    CreatedAt = DateTime.Now,
                    Avatar = "/images/avatars/default-user.png",
                    IsActive = true
                };
                _db.Users.Add(user);
                await _db.SaveChangesAsync();
            }

            await UpdateUserClaims(user);
            return RedirectToAction("Index", "Home");
        }

        [HttpPost]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync();
            return RedirectToAction("Index", "Home");
        }
        #endregion

        #region HỒ SƠ CÁ NHÂN (PROFILE) & ĐỔI MẬT KHẨU
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> Profile()
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId)) return RedirectToAction("Login");

            var user = await _db.Users.FirstOrDefaultAsync(u => u.UserID == userId);
            if (user == null) return NotFound();

            ViewBag.TotalProps = await _db.Properties.CountAsync(p => p.UserID == userId && !p.IsDeleted.Value);
            ViewBag.TotalProjects = await _db.Projects.CountAsync(p => p.UserID == userId && !p.IsDeleted.Value);

            return View(user);
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateProfile(User model)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (model.UserID.ToString() != userIdStr) return Forbid();

            var user = await _db.Users.FirstOrDefaultAsync(u => u.UserID == model.UserID);
            if (user != null)
            {
                user.FullName = model.FullName;
                user.Phone = model.Phone;
                user.Address = model.Address;
                // Zalo & FB
                user.Zalo = model.Zalo;
                user.Facebook = model.Facebook;

                await _db.SaveChangesAsync();
                await UpdateUserClaims(user);
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

                    TempData["Success"] = "Cập nhật ảnh đại diện thành công!";
                }
            }
            return RedirectToAction("Profile");
        }

        [Authorize]
        [HttpGet]
        public IActionResult ChangePassword()
        {
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
                TempData["Success"] = "Đổi mật khẩu thành công!";
                return RedirectToAction("Profile");
            }

            TempData["Error"] = "Mật khẩu cũ không chính xác!";
            return View();
        }
        #endregion
    }
}