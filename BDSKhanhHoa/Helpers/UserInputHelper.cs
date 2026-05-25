using System.Text.RegularExpressions;

namespace BDSKhanhHoa.Helpers
{
    public static class UserInputHelper
    {
        public static string NormalizeText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            value = value.Trim();
            value = Regex.Replace(value, @"\s+", " ");

            return value;
        }

        public static string NormalizeEmail(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            return value.Trim().ToLower();
        }

        public static string? NormalizePhone(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            value = value.Trim();
            value = Regex.Replace(value, @"[^\d]", "");

            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        public static string? NormalizeFacebook(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            value = value.Trim();

            if (value.StartsWith("facebook.com", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("fb.com", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("www.facebook.com", StringComparison.OrdinalIgnoreCase))
            {
                value = "https://" + value;
            }

            return value;
        }

        public static bool IsValidPhone(string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return true;

            return Regex.IsMatch(phone, @"^0[35789][0-9]{8}$");
        }

        public static bool IsValidFacebook(string? facebook)
        {
            if (string.IsNullOrWhiteSpace(facebook)) return true;

            return Regex.IsMatch(
                facebook,
                @"^(https?:\/\/)?(www\.)?(facebook\.com|fb\.com)\/[A-Za-z0-9_.\-\/?=&]+$",
                RegexOptions.IgnoreCase);
        }

        public static bool HasDangerousHtml(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            string lower = value.ToLower();

            string[] dangerousWords =
            {
                "<script", "javascript:", "onclick=", "onerror=", "iframe", "<object", "<embed"
            };

            return dangerousWords.Any(lower.Contains);
        }

        public static string Cut(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            value = value.Trim();

            if (value.Length <= maxLength) return value;

            return value.Substring(0, maxLength - 3) + "...";
        }
    }
}