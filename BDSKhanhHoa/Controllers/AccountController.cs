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
        private const string SESSION_USER_ID = "PendingUserID";
        private const string SESSION_OTP_TIME = "LastOTPTime";

        public AccountController(ApplicationDbContext db, EmailSender emailSender, IWebHostEnvironment hostEnvironment)
        {
            _db = db;
            _emailSender = emailSender;
            _hostEnvironment = hostEnvironment;
        }

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
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        }

        private async Task GrantWelcomeFreeCredits(int userId)
        {
            var normalPackage = await _db.PostServicePackages
                .FirstOrDefaultAsync(p => p.PackageType == "Tin Thường" || p.Price == 0);

            if (normalPackage == null)
            {
                normalPackage = await _db.PostServicePackages.OrderBy(p => p.Price).FirstOrDefaultAsync();
            }

            if (normalPackage != null)
            {
                for (int i = 0; i < 5; i++)
                {
                    var freeTransaction = new Transaction
                    {
                        UserID = userId,
                        PackageID = normalPackage.PackageID,
                        PropertyID = null,
                        Amount = 0,
                        Type = "Tặng lượt đăng tin thường",
                        PaymentMethod = "System Gift",
                        TransactionCode = "WELCOME" + DateTime.Now.ToString("yyyyMMddHHmmss") + userId + i,
                        Status = "Success",
                        CreatedAt = DateTime.Now
                    };
                    _db.Transactions.Add(freeTransaction);
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

        #region ĐĂNG KÝ & ĐĂNG NHẬP (TIER 1 AUTH)
        [HttpGet]
        public IActionResult Register() => View(new RegisterViewModel());

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(RegisterViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

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

            var newUser = new User
            {
                FullName = model.FullName,
                Email = model.Email,
                Username = model.Email,
                Password = PasswordHasher.HashPassword(model.Password),
                Avatar = "/images/avatars/default-user.png",
                RoleID = 3,
                IsActive = true,
                IsEmailVerified = false,
                CreatedAt = DateTime.Now
            };

            _db.Users.Add(newUser);
            await _db.SaveChangesAsync();

            string otpCode = new Random().Next(100000, 999999).ToString();
            HttpContext.Session.SetString(SESSION_OTP, otpCode);
            HttpContext.Session.SetInt32(SESSION_USER_ID, newUser.UserID);

            Console.WriteLine($"[HỆ THỐNG DEV] MÃ OTP CHO {model.Email} LÀ: {otpCode}");

            TempData["EmailToVerify"] = model.Email;
            return RedirectToAction("VerifyOTP");
        }

        [HttpGet]
        public IActionResult VerifyOTP()
        {
            if (HttpContext.Session.GetInt32(SESSION_USER_ID) == null)
                return RedirectToAction("Register");
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VerifyOTP(string otp)
        {
            var serverOtp = HttpContext.Session.GetString(SESSION_OTP);
            var pendingUserId = HttpContext.Session.GetInt32(SESSION_USER_ID);

            if (pendingUserId == null || serverOtp == null)
                return RedirectToAction("Register");

            if (otp == serverOtp)
            {
                var user = await _db.Users.FindAsync(pendingUserId);
                if (user != null)
                {
                    user.IsEmailVerified = true;
                    await _db.SaveChangesAsync();

                    HttpContext.Session.Remove(SESSION_OTP);
                    HttpContext.Session.Remove(SESSION_USER_ID);

                    await GrantWelcomeFreeCredits(user.UserID);

                    TempData["Success"] = "Xác thực thành công! Bạn được tặng 5 lượt đăng tin miễn phí. Hãy đăng nhập để trải nghiệm.";
                    return RedirectToAction("Login");
                }
            }

            ViewBag.Error = "Mã OTP không chính xác. Vui lòng thử lại.";
            return View();
        }

        [HttpGet]
        public IActionResult Login() => View(new LoginViewModel());

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            var user = await _db.Users.FirstOrDefaultAsync(u => (u.Email == model.AccountName || u.Username == model.AccountName) && !u.IsDeleted);

            if (user != null && PasswordHasher.VerifyPassword(model.Password, user.Password))
            {
                if (user.IsActive == false)
                {
                    ModelState.AddModelError("", "Tài khoản của bạn đã bị khóa bởi Ban Quản Trị.");
                    return View(model);
                }

                if (user.IsEmailVerified == false || user.IsEmailVerified == null)
                {
                    string newOtp = new Random().Next(100000, 999999).ToString();
                    HttpContext.Session.SetString(SESSION_OTP, newOtp);
                    HttpContext.Session.SetInt32(SESSION_USER_ID, user.UserID);

                    Console.WriteLine($"[HỆ THỐNG DEV] RESEND OTP: {newOtp}");

                    TempData["Warning"] = "Bạn chưa hoàn tất xác thực Email. Chúng tôi vừa gửi lại mã OTP cho bạn.";
                    TempData["EmailToVerify"] = user.Email;
                    return RedirectToAction("VerifyOTP");
                }

                await UpdateUserClaims(user);

                if (user.RoleID == 1 || user.RoleID == 2) return RedirectToAction("Index", "Dashboard", new { area = "Admin" });
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
            if (!result.Succeeded) return RedirectToAction("Login");

            var claims = result.Principal.Identities.FirstOrDefault().Claims;
            var email = claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;
            var name = claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value;

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);

            if (user == null)
            {
                user = new User
                {
                    FullName = name,
                    Email = email,
                    Username = email,
                    Password = "GOOGLE_AUTH",
                    RoleID = 3,
                    IsEmailVerified = true,
                    CreatedAt = DateTime.Now,
                    Avatar = "/images/avatars/default-user.png",
                    IsActive = true
                };

                _db.Users.Add(user);
                await _db.SaveChangesAsync();
                await GrantWelcomeFreeCredits(user.UserID);
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
                return View(user);
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

            // MẤU CHỐT SỬA LỖI Ở ĐÂY: Gỡ bỏ việc kiểm tra các trường [Required] không có trong giao diện Profile
            ModelState.Remove("Username");
            ModelState.Remove("Password");
            ModelState.Remove("ConfirmPassword");
            ModelState.Remove("Email");
            ModelState.Remove("RoleID");

            if (!ModelState.IsValid)
            {
                TempData["Error"] = "Vui lòng kiểm tra lại định dạng số điện thoại hoặc các thông tin nhập vào.";
                return RedirectToAction("Profile");
            }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.UserID == model.UserID);
            if (user != null)
            {
                user.FullName = model.FullName;
                user.Phone = model.Phone;
                user.Address = model.Address;
                user.Zalo = model.Zalo;
                user.Facebook = model.Facebook;
                user.Bio = model.Bio;

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
                TempData["Success"] = "Đổi mật khẩu thành công!";
                return RedirectToAction("Profile");
            }

            TempData["Error"] = "Mật khẩu cũ không chính xác!";
            return View();
        }
        #endregion
    }
}