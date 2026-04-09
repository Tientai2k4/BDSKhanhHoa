using BCrypt.Net;

namespace BDSKhanhHoa.Helpers
{
    public static class PasswordHasher
    {
        // Hàm băm mật khẩu
        public static string HashPassword(string password)
        {
            return BCrypt.Net.BCrypt.HashPassword(password);
        }

        // Hàm kiểm tra mật khẩu
        public static bool VerifyPassword(string password, string hashedPassword)
        {
            try
            {
                return BCrypt.Net.BCrypt.Verify(password, hashedPassword);
            }
            catch
            {
                return false;
            }
        }
    }
}