using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BDSKhanhHoa.Services
{
    public class ChatbotService
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _config;
        private readonly HttpClient _httpClient;

        public ChatbotService(ApplicationDbContext context, IConfiguration config)
        {
            _context = context;
            _config = config;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(45)
            };
        }

        public async Task<ChatResponse> ProcessChatAsync(ChatRequest req)
        {
            string originalMessage = req.Message?.Trim() ?? "";
            string normalizedMessage = NormalizeText(originalMessage);

            ChatIntent intent = DetectIntent(normalizedMessage, req.PageContext);

            List<Property> suggestedProperties = new();

            if (intent.ShouldSearchProperties)
            {
                suggestedProperties = await SearchPropertiesAsync(normalizedMessage, intent);
            }

            string realEstateContext = BuildSuggestedPropertiesContext(suggestedProperties, intent.ShouldSearchProperties);
            string currentPageContext = BuildCurrentPageContext(req.PageContext);
            string ragData = await GetRagDataAsync();

            string prompt = BuildPrompt(
                originalMessage,
                ragData,
                currentPageContext,
                realEstateContext,
                intent);

            string botMessage = await CallGeminiAsync(prompt);

            if (string.IsNullOrWhiteSpace(botMessage))
            {
                botMessage = BuildFallbackMessage(intent);
            }

            await SaveChatLogAsync(req, botMessage);

            bool shouldShowSuggestions = intent.ShouldSearchProperties && suggestedProperties.Any();

            return new ChatResponse
            {
                Message = botMessage,
                ShouldShowSuggestions = shouldShowSuggestions,
                Intent = intent.Name,
                SuggestedProperties = shouldShowSuggestions
                    ? suggestedProperties.Select(p => (object)new
                    {
                        id = p.PropertyID,
                        title = p.Title,
                        price = FormatPrice(p.Price),
                        areaSize = p.AreaSize.HasValue ? $"{p.AreaSize.Value:0.##} m²" : "",
                        location = BuildLocationText(p),
                        image = string.IsNullOrWhiteSpace(p.MainImage) ? "/images/no-image.png" : p.MainImage,
                        link = $"/Property/Details/{p.PropertyID}"
                    }).ToList()
                    : new List<object>()
            };
        }

        // =====================================================
        // 1. NHẬN DIỆN Ý ĐỊNH NGƯỜI DÙNG
        // =====================================================
        private static ChatIntent DetectIntent(string message, string? pageContext)
        {
            bool isViewingProperty = !string.IsNullOrWhiteSpace(pageContext);

            bool asksCurrentProperty =
                isViewingProperty &&
                ContainsAny(message,
                    "tin nay",
                    "can nay",
                    "nha nay",
                    "dat nay",
                    "bat dong san nay",
                    "cho nay",
                    "o day",
                    "can tren",
                    "tin dang nay",
                    "gia nay",
                    "phap ly",
                    "co nen mua",
                    "co nen thue",
                    "nen mua khong",
                    "nen thue khong",
                    "xem thuc te",
                    "dat lich",
                    "lien he chu nha");

            bool asksSearchExplicitly =
                ContainsAny(message,
                    "tim",
                    "tim kiem",
                    "kiem giup",
                    "loc giup",
                    "goi y",
                    "de xuat",
                    "cho toi xem",
                    "cho minh xem",
                    "co can nao",
                    "co nha nao",
                    "co dat nao",
                    "co bds nao",
                    "bat dong san nao",
                    "danh sach",
                    "can mua",
                    "can thue",
                    "muon mua",
                    "muon thue",
                    "toi muon mua",
                    "toi muon thue",
                    "minh muon mua",
                    "minh muon thue",
                    "anh muon mua",
                    "chi muon thue",
                    "tim can khac",
                    "goi y can khac",
                    "co tin nao khac",
                    "bat dong san khac");

            bool hasTransactionIntent =
                ContainsAny(message, "mua", "ban", "thue", "muon", "can");

            bool hasPropertyType =
                ContainsAny(message,
                    "nha",
                    "dat",
                    "can ho",
                    "chung cu",
                    "phong tro",
                    "mat bang",
                    "biet thu",
                    "shophouse",
                    "kho",
                    "xuong",
                    "van phong",
                    "toa nha",
                    "nha pho");

            bool hasAreaOrBudget =
                ContainsAny(message,
                    "nha trang",
                    "cam ranh",
                    "ninh hoa",
                    "van ninh",
                    "dien khanh",
                    "cam lam",
                    "khanh vinh",
                    "khanh son",
                    "truong sa",
                    "phan rang",
                    "ninh thuan",
                    "duoi",
                    "tam",
                    "khoang",
                    "tu",
                    "den",
                    "ty",
                    "trieu",
                    "m2");

            bool broadSearchByMeaning =
                hasTransactionIntent &&
                hasPropertyType &&
                hasAreaOrBudget &&
                !asksCurrentProperty;

            bool shouldSearchProperties =
                asksSearchExplicitly ||
                broadSearchByMeaning;

            if (asksCurrentProperty && !ContainsAny(message, "khac", "can khac", "tin khac", "goi y them", "tim them"))
            {
                shouldSearchProperties = false;
            }

            string name = "General";

            if (asksCurrentProperty)
            {
                name = "CurrentPropertyAdvice";
            }
            else if (shouldSearchProperties)
            {
                name = "PropertySearch";
            }
            else if (ContainsAny(message, "gia goi", "goi vip", "bao gia", "dang tin", "nap tien", "kim cuong", "vip"))
            {
                name = "PackagePolicy";
            }
            else if (ContainsAny(message, "phap ly", "so do", "quy hoach", "hop dong", "cong chung"))
            {
                name = "LegalAdvice";
            }

            return new ChatIntent
            {
                Name = name,
                IsViewingProperty = isViewingProperty,
                IsAskingAboutCurrentProperty = asksCurrentProperty,
                ShouldSearchProperties = shouldSearchProperties,
                WantsBuy = ContainsAny(message, "mua", "ban", "can mua", "muon mua"),
                WantsRent = ContainsAny(message, "thue", "mướn", "muon thue", "can thue"),
                WantsOtherOptions = ContainsAny(message, "khac", "can khac", "tin khac", "goi y them", "tim them")
            };
        }

        // =====================================================
        // 2. TÌM BẤT ĐỘNG SẢN CHỈ KHI KHÁCH MUỐN TÌM
        // =====================================================
        private async Task<List<Property>> SearchPropertiesAsync(string message, ChatIntent intent)
        {
            IQueryable<Property> query = _context.Properties
                .AsNoTracking()
                .Include(p => p.Ward)
                    .ThenInclude(w => w.Area)
                .Include(p => p.PropertyType)
                .Include(p => p.PostServicePackage)
                .Where(p =>
                    p.IsDeleted == false &&
                    p.Status == "Approved");

            if (intent.WantsRent)
            {
                query = query.Where(p =>
                    p.PropertyType != null &&
                    (
                        p.PropertyType.TypeName.ToLower().Contains("thuê") ||
                        p.PropertyType.TypeName.ToLower().Contains("cho thuê") ||
                        p.PropertyType.ParentID == 2
                    ));
            }
            else if (intent.WantsBuy)
            {
                query = query.Where(p =>
                    p.PropertyType != null &&
                    (
                        p.PropertyType.TypeName.ToLower().Contains("bán") ||
                        p.PropertyType.ParentID == 1
                    ));
            }

            query = ApplyPropertyTypeFilter(query, message);
            query = ApplyLocationFilter(query, message);
            query = ApplyAreaSizeFilter(query, message);

            decimal? maxPrice = ExtractMaxPrice(message);
            decimal? minPrice = ExtractMinPrice(message);

            if (maxPrice.HasValue)
            {
                query = query.Where(p => p.Price.HasValue && p.Price.Value <= maxPrice.Value);
            }

            if (minPrice.HasValue)
            {
                query = query.Where(p => p.Price.HasValue && p.Price.Value >= minPrice.Value);
            }

            DateTime now = DateTime.Now;

            List<Property> result = await query
                .OrderByDescending(p => p.VipExpiryDate.HasValue && p.VipExpiryDate.Value > now)
                .ThenBy(p => p.PostServicePackage != null ? p.PostServicePackage.PriorityLevel : 999)
                .ThenByDescending(p => p.CreatedAt)
                .Take(4)
                .ToListAsync();

            return result;
        }

        private static IQueryable<Property> ApplyPropertyTypeFilter(IQueryable<Property> query, string message)
        {
            if (ContainsAny(message, "can ho", "chung cu", "apartment"))
            {
                query = query.Where(p =>
                    p.PropertyType != null &&
                    (
                        p.PropertyType.TypeName.ToLower().Contains("căn hộ") ||
                        p.PropertyType.TypeName.ToLower().Contains("chung cư")
                    ));
            }
            else if (ContainsAny(message, "phong tro", "tro", "mini"))
            {
                query = query.Where(p =>
                    p.PropertyType != null &&
                    (
                        p.PropertyType.TypeName.ToLower().Contains("phòng trọ") ||
                        p.PropertyType.TypeName.ToLower().Contains("mini")
                    ));
            }
            else if (ContainsAny(message, "dat", "dat nen", "lo dat"))
            {
                query = query.Where(p =>
                    p.PropertyType != null &&
                    p.PropertyType.TypeName.ToLower().Contains("đất"));
            }
            else if (ContainsAny(message, "biet thu", "villa"))
            {
                query = query.Where(p =>
                    p.PropertyType != null &&
                    p.PropertyType.TypeName.ToLower().Contains("biệt thự"));
            }
            else if (ContainsAny(message, "mat bang", "kinh doanh", "cua hang", "shop"))
            {
                query = query.Where(p =>
                    p.PropertyType != null &&
                    (
                        p.PropertyType.TypeName.ToLower().Contains("mặt bằng") ||
                        p.PropertyType.TypeName.ToLower().Contains("kinh doanh") ||
                        p.Title.ToLower().Contains("mặt bằng") ||
                        p.Title.ToLower().Contains("kinh doanh")
                    ));
            }
            else if (ContainsAny(message, "van phong", "toa nha", "tru so", "cong ty"))
            {
                query = query.Where(p =>
                    p.PropertyType != null &&
                    (
                        p.PropertyType.TypeName.ToLower().Contains("văn phòng") ||
                        p.PropertyType.TypeName.ToLower().Contains("tòa nhà") ||
                        p.Title.ToLower().Contains("văn phòng") ||
                        p.Title.ToLower().Contains("tòa nhà") ||
                        p.Title.ToLower().Contains("trụ sở")
                    ));
            }
            else if (ContainsAny(message, "nha", "nha pho", "nha rieng", "nha nguyen can"))
            {
                query = query.Where(p =>
                    p.PropertyType != null &&
                    (
                        p.PropertyType.TypeName.ToLower().Contains("nhà") ||
                        p.Title.ToLower().Contains("nhà")
                    ));
            }

            return query;
        }

        private static IQueryable<Property> ApplyLocationFilter(IQueryable<Property> query, string message)
        {
            if (ContainsAny(message, "nha trang"))
            {
                query = query.Where(p =>
                    (p.Ward != null && p.Ward.Area != null && p.Ward.Area.AreaName.ToLower().Contains("nha trang")) ||
                    (p.AddressDetail != null && p.AddressDetail.ToLower().Contains("nha trang")));
            }
            else if (ContainsAny(message, "cam ranh"))
            {
                query = query.Where(p =>
                    (p.Ward != null && p.Ward.Area != null && p.Ward.Area.AreaName.ToLower().Contains("cam ranh")) ||
                    (p.AddressDetail != null && p.AddressDetail.ToLower().Contains("cam ranh")));
            }
            else if (ContainsAny(message, "ninh hoa", "ninh hòa"))
            {
                query = query.Where(p =>
                    (p.Ward != null && p.Ward.Area != null && p.Ward.Area.AreaName.ToLower().Contains("ninh hòa")) ||
                    (p.AddressDetail != null && p.AddressDetail.ToLower().Contains("ninh hòa")));
            }
            else if (ContainsAny(message, "van ninh", "vạn ninh"))
            {
                query = query.Where(p =>
                    (p.Ward != null && p.Ward.Area != null && p.Ward.Area.AreaName.ToLower().Contains("vạn ninh")) ||
                    (p.AddressDetail != null && p.AddressDetail.ToLower().Contains("vạn ninh")));
            }
            else if (ContainsAny(message, "dien khanh", "diên khánh"))
            {
                query = query.Where(p =>
                    (p.Ward != null && p.Ward.Area != null && p.Ward.Area.AreaName.ToLower().Contains("diên khánh")) ||
                    (p.AddressDetail != null && p.AddressDetail.ToLower().Contains("diên khánh")));
            }
            else if (ContainsAny(message, "cam lam", "cam lâm"))
            {
                query = query.Where(p =>
                    (p.Ward != null && p.Ward.Area != null && p.Ward.Area.AreaName.ToLower().Contains("cam lâm")) ||
                    (p.AddressDetail != null && p.AddressDetail.ToLower().Contains("cam lâm")));
            }
            else if (ContainsAny(message, "phan rang", "ninh thuan", "ninh thuận"))
            {
                query = query.Where(p =>
                    (p.Ward != null && p.Ward.Area != null &&
                     (
                         p.Ward.Area.AreaName.ToLower().Contains("phan rang") ||
                         p.Ward.Area.AreaName.ToLower().Contains("ninh thuận")
                     )) ||
                    (p.AddressDetail != null &&
                     (
                         p.AddressDetail.ToLower().Contains("phan rang") ||
                         p.AddressDetail.ToLower().Contains("ninh thuận")
                     )));
            }

            return query;
        }

        private static IQueryable<Property> ApplyAreaSizeFilter(IQueryable<Property> query, string message)
        {
            Match maxAreaMatch = Regex.Match(message, @"(duoi|nho hon|toi da)\s*(\d+)\s*m2");
            Match minAreaMatch = Regex.Match(message, @"(tren|tu|lon hon|toi thieu)\s*(\d+)\s*m2");

            if (maxAreaMatch.Success && decimal.TryParse(maxAreaMatch.Groups[2].Value, out decimal maxArea))
            {
                query = query.Where(p => p.AreaSize.HasValue && p.AreaSize.Value <= maxArea);
            }

            if (minAreaMatch.Success && decimal.TryParse(minAreaMatch.Groups[2].Value, out decimal minArea))
            {
                query = query.Where(p => p.AreaSize.HasValue && p.AreaSize.Value >= minArea);
            }

            return query;
        }

        // =====================================================
        // 3. GIÁ / NGÂN SÁCH
        // =====================================================
        private static decimal? ExtractMaxPrice(string message)
        {
            if (!ContainsAny(message, "duoi", "toi da", "khoang", "tam", "ngan sach", "budget"))
            {
                return null;
            }

            decimal? price = ExtractFirstPrice(message);
            return price;
        }

        private static decimal? ExtractMinPrice(string message)
        {
            if (!ContainsAny(message, "tren", "tu "))
            {
                return null;
            }

            decimal? price = ExtractFirstPrice(message);
            return price;
        }

        private static decimal? ExtractFirstPrice(string message)
        {
            Match billionMatch = Regex.Match(message, @"(\d+([.,]\d+)?)\s*(ty|tỷ)");
            if (billionMatch.Success)
            {
                decimal value = ParseDecimal(billionMatch.Groups[1].Value);
                return value * 1_000_000_000M;
            }

            Match millionMatch = Regex.Match(message, @"(\d+([.,]\d+)?)\s*(trieu|triệu)");
            if (millionMatch.Success)
            {
                decimal value = ParseDecimal(millionMatch.Groups[1].Value);
                return value * 1_000_000M;
            }

            return null;
        }

        private static decimal ParseDecimal(string value)
        {
            value = value.Replace(",", ".");
            decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal result);
            return result;
        }

        // =====================================================
        // 4. RAG / PROMPT
        // =====================================================
        private async Task<string> GetRagDataAsync()
        {
            string? content = await _context.StaticPages
                .AsNoTracking()
                .Where(s => s.PageKey == "ai_knowledge_base")
                .Select(s => s.Content)
                .FirstOrDefaultAsync();

            if (string.IsNullOrWhiteSpace(content))
            {
                return "Chưa có dữ liệu đào tạo nội bộ. Nếu câu hỏi liên quan chính sách, hãy trả lời thận trọng và khuyên khách liên hệ hỗ trợ.";
            }

            content = StripHtml(content);

            if (content.Length > 12000)
            {
                content = content.Substring(0, 12000);
            }

            return content;
        }

        private static string BuildCurrentPageContext(string? pageContext)
        {
            if (string.IsNullOrWhiteSpace(pageContext))
            {
                return "";
            }

            string clean = StripHtml(pageContext);

            if (clean.Length > 5000)
            {
                clean = clean.Substring(0, 5000);
            }

            return $"""
            [NGỮ CẢNH TRANG KHÁCH ĐANG XEM]
            {clean}
            """;
        }

        private static string BuildSuggestedPropertiesContext(List<Property> properties, bool shouldSearch)
        {
            if (!shouldSearch)
            {
                return """
                [GỢI Ý BẤT ĐỘNG SẢN]
                Người dùng chưa yêu cầu tìm bất động sản. Tuyệt đối không tự giới thiệu danh sách tin đăng.
                """;
            }

            if (!properties.Any())
            {
                return """
                [GỢI Ý BẤT ĐỘNG SẢN]
                Người dùng có ý định tìm bất động sản nhưng hệ thống chưa tìm thấy tin phù hợp theo bộ lọc.
                Hãy xin thêm khu vực, ngân sách, diện tích, loại hình hoặc mời khách để lại thông tin liên hệ.
                """;
            }

            StringBuilder builder = new();

            builder.AppendLine("[DANH SÁCH BẤT ĐỘNG SẢN PHÙ HỢP - CHỈ DÙNG KHI KHÁCH ĐANG MUỐN TÌM]");
            builder.AppendLine("Hãy giới thiệu ngắn gọn, không đọc ID thô. Có thể nhắc khách bấm thẻ bên dưới để xem chi tiết.");

            foreach (Property p in properties)
            {
                builder.AppendLine(
                    $"- {p.Title} | Giá: {FormatPrice(p.Price)} | Diện tích: {(p.AreaSize.HasValue ? $"{p.AreaSize.Value:0.##} m²" : "Chưa rõ")} | Vị trí: {BuildLocationText(p)} | Link: /Property/Details/{p.PropertyID}");
            }

            return builder.ToString();
        }

        private static string BuildPrompt(
            string userMessage,
            string ragData,
            string currentPageContext,
            string realEstateContext,
            ChatIntent intent)
        {
            return $"""
            Bạn là trợ lý AI tư vấn bất động sản của BĐS Khánh Hòa.

            [VAI TRÒ]
            - Tư vấn tự nhiên, chuyên nghiệp, thân thiện.
            - Ưu tiên hỗ trợ khách hiểu thông tin, chính sách, pháp lý cơ bản, cách đăng tin, gói VIP, hoặc tin BĐS đang xem.
            - Không nói lan man, không tự bịa dữ liệu.

            [Ý ĐỊNH HỆ THỐNG ĐÃ NHẬN DIỆN]
            - Intent: {intent.Name}
            - Đang xem trang chi tiết BĐS: {(intent.IsViewingProperty ? "Có" : "Không")}
            - Đang hỏi về tin hiện tại: {(intent.IsAskingAboutCurrentProperty ? "Có" : "Không")}
            - Có được phép gợi ý danh sách BĐS: {(intent.ShouldSearchProperties ? "Có" : "Không")}

            [QUY TẮC QUAN TRỌNG]
            1. Nếu "Có được phép gợi ý danh sách BĐS" là "Không":
               - Tuyệt đối KHÔNG tự giới thiệu 3-4 tin BĐS.
               - Tuyệt đối KHÔNG nói "mình gợi ý các căn sau" nếu khách chưa yêu cầu tìm.
               - Chỉ trả lời đúng câu hỏi của khách.
               - Nếu thiếu thông tin, hỏi lại ngắn gọn 1-3 câu.

            2. Nếu khách đang xem một tin cụ thể:
               - Tập trung phân tích tin đang xem dựa trên ngữ cảnh trang.
               - Có thể khuyên khách bấm đặt lịch, liên hệ người đăng, hoặc xem pháp lý nếu phù hợp.
               - Không kéo danh sách tin khác vào, trừ khi khách nói rõ muốn xem căn khác.

            3. Nếu khách thật sự muốn tìm BĐS:
               - Dựa vào danh sách BĐS phù hợp nếu có.
               - Giới thiệu tối đa 3 lựa chọn nổi bật.
               - Nói rõ nếu cần thêm ngân sách, khu vực, diện tích, loại hình.

            4. Định dạng câu trả lời:
               - Dễ đọc, có xuống dòng.
               - Dùng gạch đầu dòng khi liệt kê.
               - In đậm các điểm quan trọng bằng Markdown.
               - Không trả lời quá dài nếu câu hỏi đơn giản.

            5. Văn phong:
               - Xưng "mình" hoặc "em".
               - Gọi khách là "bạn" hoặc "anh/chị".
               - Không đọc mã ID thô nếu không cần.

            [DỮ LIỆU ĐÀO TẠO NỘI BỘ]
            {ragData}

            {currentPageContext}

            {realEstateContext}

            [CÂU HỎI CỦA KHÁCH]
            {userMessage}
            """;
        }

        // =====================================================
        // 5. GỌI GEMINI
        // =====================================================
        private async Task<string> CallGeminiAsync(string prompt)
        {
            string? apiKey = _config["GeminiApiSettings:ApiKey"];
            string? baseUrl = _config["GeminiApiSettings:BaseUrl"];

            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(baseUrl))
            {
                return "Hệ thống AI chưa được cấu hình API Key. Bạn vui lòng liên hệ quản trị viên để kiểm tra phần cấu hình Gemini.";
            }

            string url = $"{baseUrl.TrimEnd('/')}/models/gemini-2.5-flash:generateContent?key={apiKey}";

            var requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new[]
                        {
                            new { text = prompt }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.55,
                    topP = 0.9,
                    topK = 40,
                    maxOutputTokens = 1200
                }
            };

            try
            {
                using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(url, requestBody);
                string json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine("Gemini API Error: " + json);
                    return "Hiện tại trợ lý AI đang bận hoặc quá tải. Bạn thử lại sau ít phút nhé.";
                }

                using JsonDocument doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("candidates", out JsonElement candidates) &&
                    candidates.GetArrayLength() > 0 &&
                    candidates[0].TryGetProperty("content", out JsonElement content) &&
                    content.TryGetProperty("parts", out JsonElement parts) &&
                    parts.GetArrayLength() > 0 &&
                    parts[0].TryGetProperty("text", out JsonElement textElement))
                {
                    return textElement.GetString() ?? "";
                }

                return "Mình chưa nhận được phản hồi rõ ràng từ AI. Bạn vui lòng hỏi lại ngắn gọn hơn nhé.";
            }
            catch (TaskCanceledException)
            {
                return "Kết nối AI phản hồi hơi chậm. Bạn vui lòng thử lại sau vài giây nhé.";
            }
            catch (Exception ex)
            {
                Console.WriteLine("Chatbot System Error: " + ex.Message);
                return "Đã xảy ra sự cố khi kết nối trợ lý AI. Nếu cần hỗ trợ gấp, bạn vui lòng liên hệ bộ phận chăm sóc khách hàng.";
            }
        }

        // =====================================================
        // 6. LƯU LOG
        // =====================================================
        private async Task SaveChatLogAsync(ChatRequest req, string botMessage)
        {
            try
            {
                ChatLogs log = new()
                {
                    UserID = req.UserId > 0 ? req.UserId : null,
                    UserMessage = req.Message,
                    BotResponse = botMessage,
                    CreatedAt = DateTime.Now
                };

                _context.ChatLogs.Add(log);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Save Chat Log Error: " + ex.Message);
            }
        }

        // =====================================================
        // 7. TIỆN ÍCH
        // =====================================================
        private static string BuildFallbackMessage(ChatIntent intent)
        {
            if (intent.ShouldSearchProperties)
            {
                return "Mình đã tiếp nhận nhu cầu tìm bất động sản của bạn. Bạn cho mình thêm khu vực, ngân sách và loại hình mong muốn để lọc chính xác hơn nhé.";
            }

            return "Mình đã hiểu câu hỏi của bạn. Bạn có thể nói rõ hơn một chút để mình tư vấn chính xác hơn nhé.";
        }

        private static string FormatPrice(decimal? price)
        {
            if (!price.HasValue || price.Value <= 0)
            {
                return "Thỏa thuận";
            }

            decimal value = price.Value;

            if (value >= 1_000_000_000M)
            {
                return $"{value / 1_000_000_000M:0.##} tỷ";
            }

            if (value >= 1_000_000M)
            {
                return $"{value / 1_000_000M:0.##} triệu";
            }

            return $"{value:N0} đ";
        }

        private static string BuildLocationText(Property p)
        {
            List<string> parts = new();

            if (!string.IsNullOrWhiteSpace(p.Ward?.WardName))
            {
                parts.Add(p.Ward.WardName);
            }

            if (!string.IsNullOrWhiteSpace(p.Ward?.Area?.AreaName))
            {
                parts.Add(p.Ward.Area.AreaName);
            }

            if (!parts.Any() && !string.IsNullOrWhiteSpace(p.AddressDetail))
            {
                parts.Add(p.AddressDetail);
            }

            return parts.Any() ? string.Join(", ", parts) : "Đang cập nhật";
        }

        private static string StripHtml(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            string noHtml = Regex.Replace(value, "<.*?>", " ");
            noHtml = System.Net.WebUtility.HtmlDecode(noHtml);
            noHtml = Regex.Replace(noHtml, @"\s+", " ");

            return noHtml.Trim();
        }

        private static bool ContainsAny(string source, params string[] keywords)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            foreach (string keyword in keywords)
            {
                if (source.Contains(keyword))
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            string text = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            StringBuilder builder = new();

            foreach (char ch in text)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);

                if (category != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(ch);
                }
            }

            string normalized = builder
                .ToString()
                .Normalize(NormalizationForm.FormC)
                .Replace("đ", "d");

            normalized = Regex.Replace(normalized, @"\s+", " ");
            return normalized.Trim();
        }

        private sealed class ChatIntent
        {
            public string Name { get; set; } = "General";
            public bool IsViewingProperty { get; set; }
            public bool IsAskingAboutCurrentProperty { get; set; }
            public bool ShouldSearchProperties { get; set; }
            public bool WantsBuy { get; set; }
            public bool WantsRent { get; set; }
            public bool WantsOtherOptions { get; set; }
        }
    }
}