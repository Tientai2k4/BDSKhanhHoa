using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.Services.AI;
using BDSKhanhHoa.ViewModels;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BDSKhanhHoa.Services
{
    /// <summary>
    /// ChatbotService bản strict/clarify-first.
    /// Mục tiêu: không tự đề xuất tin BĐS khi câu hỏi còn mơ hồ; muốn gợi ý tin phải đủ
    /// mua/thuê + loại BĐS + khu vực + ngân sách. Câu hỏi kiến thức/pháp lý/vay/quy trình
    /// đi theo route tư vấn, không chạy SQL. Có fallback để AI không bịa khi không biết.
    /// </summary>
    public class ChatbotService
    {
        private readonly ApplicationDbContext _context;
        private readonly IAIModelClient _aiClient;
        private readonly ILogger<ChatbotService> _logger;
        private static readonly CultureInfo Vi = new("vi-VN");
        private const int MaxCards = 5;
        private const int ThinkingDelayMilliseconds = 1200; // Không nên để 10000ms vì dễ làm request chậm/crash khi test nhiều câu. Nên tạo hiệu ứng typing ở frontend nếu muốn quay lâu hơn.

        public ChatbotService(ApplicationDbContext context, IAIModelClient aiClient, ILogger<ChatbotService> logger)
        {
            _context = context;
            _aiClient = aiClient;
            _logger = logger;
        }

        public async Task<ChatResponse> ProcessChatAsync(ChatRequest req)
        {
            string original = (req.Message ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(original))
                return BuildResponse(Welcome(), "Empty", "General", "Start", req.SessionId ?? "", new(), false, new(), Replies("General"));

            PageInfo page = BuildPageInfo(req);
            AIChatSession session = await GetOrCreateSessionAsync(req, page);
            List<AIChatMessage> history = await LoadRecentMessagesAsync(session.SessionID);
            Dictionary<string, string> slots = await LoadSlotsAsync(session.SessionID);

            string normalized = ResolveShortChoice(Normalize(original), history);
            await ApplyThinkingDelayAsync(normalized);
            Scenario scenario = DetectScenario(normalized, page, session, slots);
            ResetSlotsForNewScenario(slots, scenario, session.Scenario ?? "General", normalized);
            ExtractSlots(normalized, original, slots, scenario, page, history);
            SlotPlan plan = BuildSlotPlan(scenario, slots, normalized);
            Route route = DecideRoute(scenario, slots, plan, normalized, page);

            List<Property> properties = new();
            string answer;
            string trace;

            switch (route)
            {
                case Route.Refuse:
                    answer = UnsafeAnswer();
                    trace = "Guardrail:Unsafe";
                    break;
                case Route.OffTopic:
                    answer = OffTopicAnswer(normalized);
                    trace = "Guardrail:OffTopic";
                    break;
                case Route.Clarify:
                    answer = ClarifyAnswer(scenario, slots, plan);
                    trace = "ClarificationPolicy:RequiredSlotsMissing";
                    break;
                case Route.Search:
                    properties = await SearchPropertiesAsync(slots, scenario);
                    answer = properties.Any() ? SearchResultAnswer(scenario, slots, properties) : NoResultAnswer(scenario, slots);
                    trace = properties.Any() ? BuildPropertyContext(properties, slots, scenario) : "SQLSearch:NoMatchedProperty";
                    break;
                case Route.PageAnalysis:
                    answer = page.IsProjectDetail
                        ? ProjectPageAnalysisAnswer(page, normalized)
                        : PageAnalysisAnswer(page, normalized);
                    trace = page.IsProjectDetail ? "RuleBased:ProjectPageAnalysis" : "RuleBased:PropertyPageAnalysis";
                    break;
                case Route.Direct:
                    answer = DirectAnswer(original, normalized, scenario, slots, page);
                    trace = "RuleBased:DirectKnowledge";
                    break;
                default:
                    answer = await AiOrFallbackAsync(original, normalized, scenario, slots, plan, page, history);
                    trace = "AIOrFallback";
                    break;
            }

            answer = FinalGuard(answer, scenario, properties, page);
            string stage = route == Route.Clarify ? "CollectingNeed" : route == Route.Search ? "SearchResult" : "Answering";

            session.Scenario = scenario.Name;
            session.LastIntent = scenario.Intent;
            session.PageType = page.PageType;
            session.PageUrl = page.PageUrl;
            session.PageTitle = page.PageTitle;
            session.Stage = stage;
            session.CollectedDataJson = JsonSerializer.Serialize(CleanSlots(slots));
            session.UpdatedAt = DateTime.Now;

            _context.Set<AIChatMessage>().Add(new AIChatMessage { SessionID = session.SessionID, Role = "user", Content = original, Intent = scenario.Intent, CreatedAt = DateTime.Now });
            _context.Set<AIChatMessage>().Add(new AIChatMessage { SessionID = session.SessionID, Role = "assistant", Content = answer, Intent = scenario.Intent, ToolTrace = Trim(trace, 8000), CreatedAt = DateTime.Now });
            await _context.SaveChangesAsync();

            return BuildResponse(answer, scenario.Intent, scenario.Name, stage, session.SessionKey, slots, scenario.NeedHuman, Cards(properties, slots, scenario), RepliesForRoute(route, scenario.Name));
        }


        private static async Task ApplyThinkingDelayAsync(string normalized)
        {
            // Tạo cảm giác chatbot đang "suy nghĩ" thay vì trả lời quá vội.
            // Không đặt mặc định 10 giây vì dễ làm request bị xem là chậm; nếu cần demo quay lâu hơn,
            // chỉ cần đổi ThinkingDelayMilliseconds ở đầu class thành 5000 hoặc 10000.
            if (ThinkingDelayMilliseconds <= 0) return;
            if (ContainsAny(normalized, "hack", "gia so", "tron thue", "ca do")) return;
            await Task.Delay(ThinkingDelayMilliseconds);
        }

        private static ChatResponse BuildResponse(string message, string intent, string scenario, string stage, string sessionKey, Dictionary<string, string> slots, bool needHuman, List<object> cards, List<string> replies)
        {
            return new ChatResponse
            {
                Message = message,
                Intent = intent,
                Scenario = scenario,
                Stage = stage,
                SessionId = sessionKey,
                ShouldShowSuggestions = cards.Any(),
                SuggestedProperties = cards,
                SuggestedReplies = replies,
                NeedHumanSupport = needHuman,
                CollectedSlots = CleanSlots(slots)
            };
        }

        private async Task<AIChatSession> GetOrCreateSessionAsync(ChatRequest req, PageInfo page)
        {
            string key = string.IsNullOrWhiteSpace(req.SessionId) ? Guid.NewGuid().ToString("N") : req.SessionId.Trim();
            AIChatSession? session = await _context.Set<AIChatSession>().FirstOrDefaultAsync(x => x.SessionKey == key);
            if (session != null) return session;
            session = new AIChatSession { SessionKey = key, UserID = req.UserId > 0 ? req.UserId : null, Scenario = "General", Stage = "Start", PageType = page.PageType, PageUrl = page.PageUrl, PageTitle = page.PageTitle, LastIntent = "Start", CollectedDataJson = "{}", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
            _context.Set<AIChatSession>().Add(session);
            await _context.SaveChangesAsync();
            return session;
        }

        private async Task<List<AIChatMessage>> LoadRecentMessagesAsync(int sessionId)
        {
            return await _context.Set<AIChatMessage>().AsNoTracking().Where(x => x.SessionID == sessionId).OrderByDescending(x => x.CreatedAt).Take(14).OrderBy(x => x.CreatedAt).ToListAsync();
        }

        private async Task<Dictionary<string, string>> LoadSlotsAsync(int sessionId)
        {
            string? json = await _context.Set<AIChatSession>().AsNoTracking().Where(x => x.SessionID == sessionId).Select(x => x.CollectedDataJson).FirstOrDefaultAsync();
            if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.OrdinalIgnoreCase);
            try
            {
                Dictionary<string, string>? data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                return data == null ? new(StringComparer.OrdinalIgnoreCase) : new(data, StringComparer.OrdinalIgnoreCase);
            }
            catch { return new(StringComparer.OrdinalIgnoreCase); }
        }

        private static string ResolveShortChoice(string normalized, List<AIChatMessage> history)
        {
            if (!Regex.IsMatch(normalized ?? string.Empty, @"^[1-5]$")) return normalized;
            string last = Normalize(history.LastOrDefault(x => x.Role == "assistant")?.Content ?? string.Empty);
            return normalized switch
            {
                "1" when ContainsAny(last, "quy trinh ban lai") => "quy trinh ban lai bat dong san",
                "2" when ContainsAny(last, "yeu to anh huong den gia ban", "thanh khoan") => "cac yeu to anh huong den gia ban va thanh khoan khi ban lai",
                "3" when ContainsAny(last, "chi phi va thue", "thue khi ban lai") => "chi phi va thue khi ban lai bat dong san",
                _ => normalized
            };
        }

        private static void ResetSlotsForNewScenario(Dictionary<string, string> slots, Scenario sc, string lastScenario, string normalized)
        {
            if (slots.Count == 0) return;
            if (sc.Name == lastScenario || sc.Name == "PropertyDetail") return;
            if (Refinement(normalized) || Another(normalized)) return;

            bool hardSwitch = sc.Name is "Legal" or "Transaction" or "Loan" or "Posting" or "Project" or "Market" or "Care" or "Unsafe" or "OffTopic";
            bool buyRentSwitch = (sc.Name == "Buy" && lastScenario == "Rent") || (sc.Name == "Rent" && lastScenario == "Buy");
            if (!hardSwitch && !buyRentSwitch) return;

            string[] keys = { "deal_type", "property_type", "area_name", "ward_name", "budget_min", "budget_max", "rent_max", "area_min", "area_max", "purpose", "legal_requirement", "road_requirement", "amenities", "loan_need", "sort_price" };
            foreach (string key in keys) slots.Remove(key);
        }

        private static bool PostingNoExaggerationQuestion(string n)
        {
            return ContainsAny(n, "bao loi", "bao lai", "chac chan tang gia", "cam ket loi nhuan", "co nen ghi");
        }

        private static bool SellerPresentationQuestion(string n)
        {
            return ContainsAny(n, "ban nhanh", "khong muon bi ep gia", "bi ep gia", "trinh bay tin", "de co khach", "dang tin the nao");
        }

        private static bool HasPropertyNeedIntent(string n)
        {
            return Buy(n) || Rent(n) || Search(n) ||
                   (PropertyType(n) != null && (AreaName(n) != null || HasBudgetWords(n) || ContainsAny(n, "mua", "thue", "tim", "co nha", "co dat", "can mua", "muon mua")));
        }

        private static bool HasBudgetWords(string n)
        {
            return MoneyValues(n).Any() || MoneyRange(n).HasValue || ContainsAny(n, "duoi", "tren", "tam", "khoang", "ngan sach", "gia");
        }

        private static bool MortgageEligibilityQuestion(string n)
        {
            return ContainsAny(n,
                "mua dat co so do thi ngan hang co cho vay",
                "mua dat co so do ngan hang co cho vay",
                "co so do thi ngan hang co cho vay",
                "co so hong thi ngan hang co cho vay",
                "ngan hang co cho vay khong",
                "ngan hang co nhan the chap khong",
                "mua dat co so do",
                "mua nha co so hong");
        }

        private static bool BuildPermissionQuestion(string n)
        {
            return ContainsAny(n, "co duoc xay nha", "dat co duoc xay", "kiem tra dat co duoc xay", "duoc phep xay", "cap phep xay dung", "muc dich su dung dat", "dat o hay dat nong nghiep");
        }

        private static bool FeasibilityAdviceQuestion(string n)
        {
            return ContainsAny(n,
                       "co kha thi khong", "kha thi khong", "co on khong",
                       "nen chon", "chon dat mat tien hay nha xay san",
                       "vi tri dien tich hay phap ly", "so sanh", "nen mua",
                       "ngan sach gioi han", "giam yeu cau dien tich hay vi tri",
                       "giam dien tich hay vi tri", "dat mat tien gia cao", "mat tien gia cao",
                       "gia re co so duong nho", "duong nho co nen mua", "hem nho co nen mua")
                   && !ContainsAny(n, "co tin nao", "tim tin", "loc tin", "cho toi xem");
        }

        private static bool ExplicitSearchRequest(string n)
        {
            return ExplicitListing(n) || ContainsAny(n,
                "co tin nao", "co nha nao", "co dat nao", "co can nao", "co lo nao", "co phong nao",
                "tim tin", "loc tin", "xem tin", "cho toi xem", "goi y tin", "de xuat tin", "show tin");
        }

        private static bool BuyAdvisoryQuestion(string n)
        {
            // Nếu câu có vay vốn hoặc pháp lý rõ ràng nhiều ý, để Loan/MultiIntent xử lý riêng.
            // Ngoại lệ: các câu kinh nghiệm kiểu “sợ quy hoạch, hỏi người bán gì” vẫn trả lời tư vấn mua.
            if (Loan(n)) return false;
            if (Legal(n) && !ContainsAny(n, "so dinh quy hoach", "so vuong quy hoach", "gia re", "hoi nguoi ban", "co rui ro", "rui ro")) return false;

            bool buyContext = Buy(n) || ContainsAny(n, "mua dat", "mua nha", "mua bds", "mua bat dong san", "xay nha", "giu tai san", "dau tu");
            bool advisory = ContainsAny(n,
                "nen chon", "chua biet nen", "khong biet nen", "chon xa nao", "chon khu nao", "co nen", "nen mua",
                "co kha thi khong", "kha thi khong", "co on khong", "on khong", "rui ro", "so dinh quy hoach", "so vuong quy hoach",
                "hoi nguoi ban gi", "can hoi gi", "can kiem tra gi", "kiem tra gi", "gan bien de dau tu", "von khong nhieu",
                "vi tri dien tich hay phap ly", "uu tien phap ly hay vi tri", "phap ly truoc", "vi tri truoc",
                "dat mat tien hay nha xay san", "vua o vua kinh doanh", "de o lau dai", "o on dinh", "ban lai de");

            return buyContext && advisory && !ExplicitSearchRequest(n);
        }

        private static bool RentAdvisoryQuestion(string n)
        {
            bool rentContext = Rent(n) || ContainsAny(n,
                "hop dong thue", "chu nha", "chu phong", "tien coc", "dat coc thue", "coc 6 thang", "coc sau thang",
                "nha thue", "phong tro", "mat bang", "van phong", "noi that", "thu cung", "nuoi thu cung");
            bool advisory = ContainsAny(n,
                "can hoi gi", "hoi chu nha", "can kiem tra gi", "kiem tra gi", "rui ro", "co rui ro khong",
                "hop dong", "dieu khoan", "dat coc", "tien coc", "coc 6 thang", "coc sau thang", "cong chung",
                "kinh doanh", "mo quan", "quan ca phe", "van phong nho", "dien nuoc", "thue", "giay phep", "pccc",
                "o lau dai", "nuoi thu cung", "thu cung", "noi that", "ban giao", "an toan");

            return rentContext && advisory && !ExplicitSearchRequest(n);
        }

        private static Scenario DetectScenario(string normalized, PageInfo page, AIChatSession session, Dictionary<string, string> slots)
        {
            if (Unsafe(normalized)) return new("Unsafe", "UnsafeIntent", false, false, false);
            if (OffTopic(normalized)) return new("OffTopic", "OffTopicIntent", false, false, false);

            // Ưu tiên đăng tin trước khi bắt lỗi/khiếu nại.
            // Ví dụ: "Có nên ghi bao lời..." là hỏi cách đăng tin an toàn, không phải "báo lỗi".
            if (PostingNoExaggerationQuestion(normalized) || SellerPresentationQuestion(normalized))
                return new("Posting", "SellerPostingIntent", false, false, false);

            if (Complaint(normalized)) return new("Care", "ComplaintReportIntent", false, false, true);
            if (Appointment(normalized)) return new("Care", "AppointmentGuideIntent", false, false, true);
            if (Support(normalized)) return new("Care", "CustomerSupportIntent", false, false, true);
            if (WebsiteGuide(normalized)) return new("Care", "WebsiteGuideIntent", false, false, false);

            // Khi đang ở trang chi tiết dự án, ưu tiên phân tích dự án đang xem trước nhóm kiến thức chung.
            if (page.IsProjectDetail && (Project(normalized) || CurrentProperty(normalized) || CurrentProjectQuestion(normalized)))
                return new("ProjectDetail", "ProjectDetailIntent", false, false, false);

            // Khi đang ở trang chi tiết, ưu tiên câu hỏi về tin hiện tại trước nhóm vay/market.
            // Ví dụ: "Giá này có hợp lý không?" không được nhảy sang tư vấn vay vốn.
            if (page.IsPropertyDetail && CurrentProperty(normalized))
                return new("PropertyDetail", "PropertyDetailIntent", false, false, false);

            // Các câu tư vấn thuê/mua theo kinh nghiệm phải trả lời trực tiếp, không ép hỏi khu vực/ngân sách.
            // Ví dụ: "Thuê mặt bằng cọc 6 tháng có rủi ro không?", "Mua Cam Lâm hay Diên Khánh để ở ổn định?".
            if (RentAdvisoryQuestion(normalized)) return new("RentAdvice", "RentAdviceIntent", false, false, false);
            if (BuyAdvisoryQuestion(normalized)) return new("BuyAdvice", "BuyAdviceIntent", false, false, false);

            // Các câu vay vốn/pháp lý độc lập phải ưu tiên trước MultiIntent, tránh câu
            // "Mua đất có sổ đỏ thì ngân hàng có cho vay không?" bị tách sai thành nhiều ý.
            if (MortgageEligibilityQuestion(normalized)) return new("Loan", "LoanAdviceIntent", false, false, false);
            if (BuildPermissionQuestion(normalized)) return new("Legal", "BuildPermissionLegalIntent", false, false, false);

            // Câu hỏi nhiều ý định: vừa tìm mua + pháp lý + vay vốn/dự án/giao dịch.
            // Không ép thành một intent đơn làm mất ý người dùng.
            if (IsMultiIntentQuestion(normalized))
                return new("MultiIntent", "MultiIntentAdvice", false, false, false);

            if (StandaloneLoan(normalized)) return new("Loan", "LoanAdviceIntent", false, false, false);
            if (StandaloneProject(normalized)) return new("Project", "ProjectConsultIntent", false, false, false);
            if (StandaloneTransaction(normalized)) return new("Transaction", "TransactionProcedureIntent", false, false, false);
            if (StandalonePosting(normalized)) return new("Posting", "SellerPostingIntent", false, false, false);
            if (StandaloneLegal(normalized)) return new("Legal", "LegalAdviceIntent", false, false, false);
            if (StandaloneMarket(normalized) || FeasibilityAdviceQuestion(normalized)) return new("Market", "MarketExperienceIntent", false, false, false);
            if (BuySafety(normalized)) return new("Buy", "BuySafetyAdviceIntent", true, false, false);

            bool buy = Buy(normalized);
            bool rent = Rent(normalized);
            bool explicitSearch = ExplicitListing(normalized);
            bool refine = Refinement(normalized) && (session.Scenario == "Buy" || session.Scenario == "Rent" || slots.ContainsKey("deal_type"));
            bool needContinuation = IsSearchNeedContinuation(normalized, session, slots);

            // Câu trả lời ngắn sau khi chatbot vừa hỏi thêm thông tin, ví dụ:
            // User: "tôi muốn mua 1 lô đất tại nha trang" -> bot hỏi ngân sách/mục đích/pháp lý
            // User: "giá dưới 20 tỷ, để ở và kinh doanh, có sổ đầy đủ"
            // Không được rơi về General/AI, mà phải giữ kịch bản Buy/Rent và lọc SQL nếu đã đủ slot.
            if (needContinuation)
                return SlotIs(slots, "deal_type", "Thuê") || session.Scenario == "Rent"
                    ? new("Rent", "SearchPropertyIntent", true, true, false)
                    : new("Buy", "SearchPropertyIntent", true, true, false);

            if (refine)
                return SlotIs(slots, "deal_type", "Thuê") || session.Scenario == "Rent"
                    ? new("Rent", "SearchRefinementIntent", true, true, false)
                    : new("Buy", "SearchRefinementIntent", true, true, false);

            if (rent) return new("Rent", explicitSearch ? "RentPropertyIntent" : "RentAdviceIntent", true, explicitSearch, false);
            if (buy) return new("Buy", explicitSearch ? "BuyPropertyIntent" : "BuyAdviceIntent", true, explicitSearch, false);

            if (Search(normalized) || Another(normalized))
                return SlotIs(slots, "deal_type", "Thuê")
                    ? new("Rent", "SearchPropertyIntent", true, true, false)
                    : new("Buy", "SearchPropertyIntent", true, true, false);

            if (Project(normalized) || page.IsProjectDetail) return new("Project", "ProjectConsultIntent", false, false, false);
            if (Legal(normalized)) return new("Legal", "LegalAdviceIntent", false, false, false);
            if (Transaction(normalized)) return new("Transaction", "TransactionProcedureIntent", false, false, false);
            if (Loan(normalized)) return new("Loan", "LoanAdviceIntent", false, false, false);
            if (Posting(normalized)) return new("Posting", "SellerPostingIntent", false, false, false);
            if (Market(normalized)) return new("Market", "MarketExperienceIntent", false, false, false);

            return new("General", "UnknownIntent", false, false, false);
        }

        private static Route DecideRoute(Scenario sc, Dictionary<string, string> slots, SlotPlan plan, string normalized, PageInfo page)
        {
            if (sc.Name == "Unsafe") return Route.Refuse;
            if (sc.Name == "OffTopic") return Route.OffTopic;

            // Chăm sóc khách hàng và phân tích tin đang xem vẫn xử lý theo dữ liệu nội bộ/trang hiện tại.
            if (sc.Name == "Care") return Route.Direct;
            if (sc.Name == "PropertyDetail" || sc.Name == "ProjectDetail") return Route.PageAnalysis;
            if (sc.Name is "BuyAdvice" or "RentAdvice") return Route.Direct;
            if (sc.Intent == "BuySafetyAdviceIntent") return Route.Direct;

            // Các nhóm kiến thức phải được trả lời như chatbot AI hiện đại:
            // pháp lý cơ bản, giao dịch, vay vốn, đăng tin, dự án, kinh nghiệm thị trường, câu nhiều ý định.
            // Không ép hỏi lại "thiếu dữ liệu" cho các câu kiến thức chung như "Đất nông nghiệp có xây nhà được không?".
            // Nếu cần thông tin ngoài website, AiOrFallbackAsync sẽ gọi Gemini và bật Google Search Grounding khi cấu hình cho phép.
            if (sc.Name is "Legal" or "Transaction" or "Loan" or "Posting" or "Project" or "Market" or "MultiIntent")
                return Route.AI;

            if (sc.Name is "Buy" or "Rent")
            {
                if (!HasSearchCriteria(slots, sc.Name)) return Route.Clarify;

                // Khi đã đủ loại BĐS + khu vực cụ thể + ngân sách thì lọc SQL nội bộ.
                // Các câu tư vấn "có nên/kiểm tra gì/rủi ro" đã được tách sang BuyAdvice/RentAdvice phía trên.
                return Route.Search;
            }

            // General vẫn cho AI xử lý để trả lời các câu BĐS cơ bản mà bộ rule chưa bắt được.
            // Nếu AI/quota lỗi thì fallback về câu hướng dẫn an toàn, không văng lỗi.
            return Route.AI;
        }

        private static void ExtractSlots(string normalized, string original, Dictionary<string, string> slots, Scenario sc, PageInfo page, List<AIChatMessage> history)
        {
            if (sc.Name == "Buy" || sc.Intent.Contains("Buy")) slots["deal_type"] = "Mua";
            if (sc.Name == "Rent" || sc.Intent.Contains("Rent")) slots["deal_type"] = "Thuê";

            ApplyShortAnswer(normalized, slots, history);

            // CỰC KỲ QUAN TRỌNG:
            // Câu follow-up như "giá dưới 20 tỷ, để ở và kinh doanh, có sổ đầy đủ"
            // là bổ sung mục đích/pháp lý/ngân sách, KHÔNG phải đổi từ "Đất" sang "Mặt bằng".
            // Chỉ ghi đè property_type khi người dùng nói rõ loại hình mới: đất/nhà/căn hộ/mặt bằng...
            string? type = PropertyType(normalized);
            if (!string.IsNullOrWhiteSpace(type) && ShouldOverridePropertyType(normalized, slots))
                slots["property_type"] = type;

            string? area = AreaName(normalized); if (!string.IsNullOrWhiteSpace(area)) slots["area_name"] = area;
            ExtractMoney(normalized, slots, sc);
            ExtractAreaSize(normalized, slots);
            string? purpose = Purpose(normalized); if (!string.IsNullOrWhiteSpace(purpose)) slots["purpose"] = purpose;
            string? legal = LegalNeed(normalized); if (!string.IsNullOrWhiteSpace(legal)) slots["legal_requirement"] = legal;
            string? road = RoadNeed(normalized); if (!string.IsNullOrWhiteSpace(road)) slots["road_requirement"] = road;
            string? amenity = Amenities(normalized); if (!string.IsNullOrWhiteSpace(amenity)) slots["amenities"] = amenity;
            if (ContainsAny(normalized, "vay", "ngan hang", "tra gop")) slots["loan_need"] = "Có quan tâm vay ngân hàng";
            Match propertyId = Regex.Match(page.PageUrl ?? "", @"/Property/Details/(?<id>\d+)", RegexOptions.IgnoreCase);
            if (propertyId.Success) slots["page_property_id"] = propertyId.Groups["id"].Value;
        }

        private static bool ShouldOverridePropertyType(string normalized, Dictionary<string, string> slots)
        {
            if (!slots.ContainsKey("property_type")) return true;

            // Chỉ đổi loại hình khi người dùng nêu trực tiếp loại BĐS mới.
            // Không xem các từ "kinh doanh", "để ở", "đầu tư" là loại hình.
            return ContainsAny(normalized,
                "lo dat", "dat nen", "dat tho cu", "mua dat", "tim dat",
                "nha rieng", "nha pho", "nha nguyen can", "mua nha", "tim nha",
                "can ho", "chung cu",
                "mat bang", "van phong", "cua hang", "shop", "shophouse", "showroom", "toa nha",
                "phong tro", "nha tro",
                "biet thu", "villa");
        }

        private static SlotPlan BuildSlotPlan(Scenario sc, Dictionary<string, string> slots, string normalized)
        {
            List<string> missing = new();
            if (sc.Name == "Buy")
            {
                if (!slots.ContainsKey("property_type")) missing.Add("property_type");
                if (!slots.ContainsKey("area_name")) missing.Add("area_name");
                if (!HasBudget(slots)) missing.Add("budget_any");
            }
            if (sc.Name == "Rent")
            {
                if (!slots.ContainsKey("property_type")) missing.Add("property_type");
                if (!slots.ContainsKey("area_name")) missing.Add("area_name");
                if (!slots.ContainsKey("rent_max") && !HasBudget(slots)) missing.Add("rent_or_budget");
            }
            if (sc.Intent == "BuySafetyAdviceIntent") missing.Clear();
            return new SlotPlan { Missing = missing };
        }

        private static bool HasSearchCriteria(Dictionary<string, string> slots, string scName)
        {
            bool deal = slots.ContainsKey("deal_type") || scName is "Buy" or "Rent";
            bool type = slots.ContainsKey("property_type");
            bool area = slots.ContainsKey("area_name");
            bool budget = HasBudget(slots);
            if (area && slots.TryGetValue("area_name", out string? areaName) && IsProvinceWideArea(Normalize(areaName)))
                return false;
            return deal && type && area && budget;
        }

        private async Task<List<Property>> SearchPropertiesAsync(Dictionary<string, string> slots, Scenario sc)
        {
            // Cổng chặn cuối: chỉ tìm khi đã đủ loại BĐS + khu vực + ngân sách.
            // Sửa phần nhận CSDL: mở rộng khu vực Khánh Hòa mới, gồm các khu vực Ninh Thuận cũ;
            // đồng thời khớp loại "mặt bằng kinh doanh" rộng hơn nhưng vẫn không bịa tin.
            if (!HasSearchCriteria(slots, sc.Name)) return new();

            IQueryable<Property> query = _context.Properties.AsNoTracking()
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Include(p => p.PropertyType)
                .Include(p => p.PostServicePackage)
                .Where(p => p.IsDeleted != true &&
                    (p.Status == "Approved" || p.Status == "Active" || p.Status == "Published" ||
                     p.Status == "Đã duyệt" || p.Status == "Da duyet"));

            string deal = slots.TryGetValue("deal_type", out string? d) ? d : sc.Name == "Rent" ? "Thuê" : "Mua";

            if (deal.Equals("Thuê", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(p => p.PropertyType != null &&
                    (p.PropertyType.ParentID == 2 ||
                     p.PropertyType.TypeName.ToLower().Contains("thuê") || p.PropertyType.TypeName.ToLower().Contains("thue") ||
                     p.Title.ToLower().Contains("cho thuê") || p.Title.ToLower().Contains("cho thue") ||
                     p.Title.ToLower().Contains("thuê") || p.Title.ToLower().Contains("thue")));
            }
            else
            {
                query = query.Where(p => p.PropertyType != null &&
                    !(p.PropertyType.ParentID == 2 ||
                      p.PropertyType.TypeName.ToLower().Contains("thuê") || p.PropertyType.TypeName.ToLower().Contains("thue") ||
                      p.Title.ToLower().Contains("cho thuê") || p.Title.ToLower().Contains("cho thue") ||
                      p.Title.ToLower().Contains("thuê") || p.Title.ToLower().Contains("thue")));
            }

            decimal? max = DecimalSlot(slots, deal == "Thuê" && slots.ContainsKey("rent_max") ? "rent_max" : "budget_max");
            decimal? min = DecimalSlot(slots, "budget_min");
            decimal? amin = DecimalSlot(slots, "area_min");
            decimal? amax = DecimalSlot(slots, "area_max");

            if (min.HasValue) query = query.Where(p => p.Price.HasValue && p.Price.Value >= min.Value);
            if (max.HasValue) query = query.Where(p => p.Price.HasValue && p.Price.Value <= max.Value);
            if (amin.HasValue) query = query.Where(p => p.AreaSize.HasValue && p.AreaSize.Value >= amin.Value);
            if (amax.HasValue) query = query.Where(p => p.AreaSize.HasValue && p.AreaSize.Value <= amax.Value);

            DateTime now = DateTime.Now;
            List<Property> list = await query
                .OrderByDescending(p => p.VipExpiryDate.HasValue && p.VipExpiryDate.Value > now)
                .ThenBy(p => p.PostServicePackage != null ? p.PostServicePackage.PriorityLevel : 999)
                .ThenByDescending(p => p.UpdatedAt ?? p.CreatedAt)
                .Take(1500)
                .ToListAsync();

            string typeNeed = Normalize(slots.GetValueOrDefault("property_type", ""));
            string areaNeed = Normalize(slots.GetValueOrDefault("area_name", ""));
            bool provinceWide = IsProvinceWideArea(areaNeed);

            IEnumerable<Property> strict = list;
            if (!string.IsNullOrWhiteSpace(areaNeed) && !provinceWide) strict = strict.Where(p => MatchArea(p, areaNeed));
            if (!string.IsNullOrWhiteSpace(typeNeed)) strict = strict.Where(p => MatchType(p, typeNeed));

            List<Property> result = strict
                .OrderByDescending(p => Score(p, slots, typeNeed, areaNeed, now))
                .GroupBy(p => p.PropertyID)
                .Select(g => g.First())
                .Take(MaxCards)
                .ToList();

            if (result.Any()) return result;

            // Fallback an toàn cho "mặt bằng kinh doanh": trong CSDL thực tế có thể lưu dưới dạng
            // Nhà phố, shophouse, văn phòng, cửa hàng, đất mặt tiền... Nếu không có TypeName đúng "Mặt bằng",
            // thử nới loại hình nhưng vẫn giữ khu vực + ngân sách + trạng thái tin thật.
            if (typeNeed.Contains("mat bang") || typeNeed.Contains("kinh doanh") || typeNeed.Contains("van phong") || typeNeed.Contains("shop"))
            {
                IEnumerable<Property> business = list;
                if (!string.IsNullOrWhiteSpace(areaNeed) && !provinceWide) business = business.Where(p => MatchArea(p, areaNeed));
                business = business.Where(IsBusinessSuitableProperty);

                result = business
                    .OrderByDescending(p => Score(p, slots, typeNeed, areaNeed, now))
                    .GroupBy(p => p.PropertyID)
                    .Select(g => g.First())
                    .Take(MaxCards)
                    .ToList();

                if (result.Any()) return result;
            }

            return new();
        }

        private static int Score(Property p, Dictionary<string, string> slots, string typeNeed, string areaNeed, DateTime now)
        {
            int s = 0;
            if (MatchType(p, typeNeed)) s += 35;
            if (MatchArea(p, areaNeed)) s += 35;
            decimal? max = DecimalSlot(slots, slots.ContainsKey("rent_max") ? "rent_max" : "budget_max");
            if (max.HasValue && p.Price.HasValue && p.Price.Value <= max.Value) s += 25;
            if (!string.IsNullOrWhiteSpace(p.MainImage)) s += 4;
            if (!string.IsNullOrWhiteSpace(p.Description) && p.Description.Length > 80) s += 4;
            if (p.VipExpiryDate.HasValue && p.VipExpiryDate.Value > now) s += 5;
            return s;
        }

        private static bool MatchType(Property p, string typeNeed)
        {
            if (string.IsNullOrWhiteSpace(typeNeed)) return true;

            string title = Normalize(p.Title ?? string.Empty);
            string typeName = Normalize(p.PropertyType?.TypeName ?? string.Empty);
            string description = Normalize(p.Description ?? string.Empty);
            string address = Normalize(p.AddressDetail ?? string.Empty);
            string text = $"{title} {typeName} {description} {address}";

            // CSDL đồ án có thể đặt tên loại khác nhau: "Mặt bằng", "BĐS kinh doanh",
            // "Nhà phố", "Shophouse", "Văn phòng", "Cửa hàng". Khớp rộng để nhận dữ liệu thật,
            // nhưng vẫn tránh lấy đất thường khi người dùng cần mặt bằng kinh doanh.
            if (typeNeed.Contains("mat bang") || typeNeed.Contains("kinh doanh") || typeNeed.Contains("van phong") || typeNeed.Contains("shop"))
                return IsBusinessSuitableProperty(p);

            if (typeNeed.Contains("can ho") || typeNeed.Contains("chung cu"))
                return ContainsAny(text, "can ho", "chung cu", "apartment");

            if (typeNeed.Contains("phong tro") || typeNeed.Contains("nha tro"))
                return ContainsAny(text, "phong tro", "nha tro", "can ho mini");

            if (typeNeed.Contains("dat"))
                return ContainsAny(text, "dat", "dat nen", "lo dat", "dat tho cu", "dat nong nghiep", "dat vuon")
                    && !ContainsAny(text, "nha pho", "nha rieng", "can ho", "chung cu", "mat bang");

            if (typeNeed.Contains("nha"))
                return ContainsAny(text, "nha", "nha pho", "nha rieng", "nha nguyen can", "biet thu", "villa", "shophouse")
                    && !ContainsAny(text, "dat nen", "lo dat", "dat tho cu", "can ho", "chung cu");

            return ContainsCompact(text, typeNeed);
        }

        private static bool IsBusinessSuitableProperty(Property p)
        {
            string text = Normalize($"{p.Title} {p.PropertyType?.TypeName} {p.Description} {p.AddressDetail}");

            bool directBusiness = ContainsAny(text,
                "mat bang", "mat bang kinh doanh", "kinh doanh", "van phong", "cua hang", "shop",
                "shophouse", "nha pho thuong mai", "nha mat tien", "mat tien", "toa nha", "mua ban",
                "mo quan", "spa", "cafe", "ca phe", "showroom", "khach san", "nha nghi");

            if (directBusiness) return true;

            // Nhà phố/nhà mặt tiền thường có thể phù hợp kinh doanh.
            bool houseBusinessCandidate = ContainsAny(text, "nha pho", "nha rieng", "biet thu", "villa") &&
                                          ContainsAny(text, "duong lon", "mat tien", "trung tam", "kinh doanh");

            return houseBusinessCandidate;
        }

        private static bool MatchArea(Property p, string areaNeed)
        {
            if (string.IsNullOrWhiteSpace(areaNeed)) return true;

            string title = Normalize(p.Title ?? string.Empty);
            string address = Normalize(p.AddressDetail ?? string.Empty);
            string ward = Normalize(p.Ward?.WardName ?? string.Empty);
            string area = Normalize(p.Ward?.Area?.AreaName ?? string.Empty);
            string text = $"{title} {address} {ward} {area}";

            List<string> aliases = AreaAliases(areaNeed);

            // Ưu tiên kiểm tra AreaName/WardName trước. Đây là dữ liệu địa giới chuẩn trong CSDL.
            foreach (string alias in aliases)
            {
                if (AreaPhraseEquals(area, alias) || AreaPhraseEquals(ward, alias)) return true;
            }

            // Sau đó mới kiểm tra tiêu đề/địa chỉ, nhưng vẫn phải khớp theo cụm địa danh thật, không khớp mơ hồ.
            foreach (string alias in aliases)
            {
                if (ContainsAreaPhrase(text, alias)) return true;
            }

            return false;
        }

        private static bool AreaPhraseEquals(string text, string alias)
        {
            text = Normalize(text);
            alias = Normalize(alias);
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(alias)) return false;
            return text == alias || ContainsAreaPhrase(text, alias);
        }

        private static bool ContainsAreaPhrase(string text, string alias)
        {
            string n = Normalize(text);
            string a = Normalize(alias);
            if (a.Length < 4) return Regex.IsMatch(n, $@"(^|\s){Regex.Escape(a)}($|\s)");

            // Khớp theo cụm từ có ranh giới khoảng trắng để tránh kiểu alias ngắn/một phần chữ kéo sai khu vực.
            return Regex.IsMatch(n, $@"(^|\s){Regex.Escape(a)}($|\s|,|\.|-)");
        }

        private static string ClarifyAnswer(Scenario sc, Dictionary<string, string> slots, SlotPlan plan)
        {
            if (sc.Name == "Buy")
            {
                List<string> q = new();
                if (!slots.ContainsKey("property_type")) q.Add("Anh/Chị muốn mua loại nào: đất, nhà, căn hộ hay mặt bằng?");
                if (!slots.ContainsKey("area_name")) q.Add("Khu vực mong muốn ở đâu: Nha Trang, Diên Khánh, Cam Lâm, Ninh Hòa, Cam Ranh hay khu khác?");
                if (!HasBudget(slots)) q.Add("Ngân sách tối đa khoảng bao nhiêu? Ví dụ: dưới 1 tỷ, khoảng 1,5 tỷ, từ 2–3 tỷ.");
                if (!slots.ContainsKey("purpose")) q.Add("Mục đích mua là để ở, đầu tư, kinh doanh hay giữ tài sản?");
                if (!slots.ContainsKey("legal_requirement")) q.Add("Anh/Chị có bắt buộc sổ riêng/pháp lý rõ không?");
                return $"""
                {NeedSummary(slots, sc)}

                Em **chưa đề xuất tin ngay** vì thiếu thông tin lõi thì kết quả dễ sai. Để lọc chính xác hơn, Anh/Chị cho em biết:

                {string.Join("\n", q.Take(4).Select((x, i) => $"{i + 1}. {x}"))}

                Khi đủ thông tin, em mới lọc từ dữ liệu nội bộ để tránh gợi ý bậy.
                """;
            }
            if (sc.Name == "Rent")
            {
                List<string> q = new();
                if (!slots.ContainsKey("property_type")) q.Add("Anh/Chị muốn thuê loại nào: nhà nguyên căn, căn hộ, phòng trọ, mặt bằng hay văn phòng?");
                if (!slots.ContainsKey("area_name")) q.Add("Khu vực muốn thuê ở đâu?");
                if (!slots.ContainsKey("rent_max") && !HasBudget(slots)) q.Add("Ngân sách thuê mỗi tháng khoảng bao nhiêu?");
                if (!slots.ContainsKey("purpose")) q.Add("Thuê để ở, làm văn phòng hay kinh doanh?");
                return $"""
                {NeedSummary(slots, sc)}

                Em chưa đề xuất tin thuê ngay vì còn thiếu thông tin quan trọng. Anh/Chị cho em biết thêm:

                {string.Join("\n", q.Take(4).Select((x, i) => $"{i + 1}. {x}"))}
                """;
            }
            return "Anh/Chị cho em thêm khu vực, ngân sách và loại bất động sản để em tư vấn chính xác hơn nhé.";
        }

        private static string SearchResultAnswer(Scenario sc, Dictionary<string, string> slots, List<Property> props)
        {
            return $"""
            {NeedSummary(slots, sc)}

            Em tìm được **{Math.Min(MaxCards, props.Count)} tin** khớp tốt nhất trong dữ liệu nội bộ. Em chỉ hiển thị khi đã có đủ tiêu chí lõi để tránh đề xuất sai.

            **Lý do ưu tiên tin đầu tiên:** {Reason(props.First(), slots, sc)}

            Lưu ý: Em không tự cam kết pháp lý/quy hoạch. Trước khi đặt cọc, Anh/Chị vẫn cần kiểm tra sổ gốc, chủ sở hữu, quy hoạch, tranh chấp, thế chấp và xem thực tế.
            """;
        }

        private static string NoResultAnswer(Scenario sc, Dictionary<string, string> slots)
        {
            return $"""
            {NeedSummary(slots, sc)}

            Hiện tại em **chưa tìm thấy tin nào khớp hoàn toàn** với các tiêu chí này trong dữ liệu nội bộ.

            Em không muốn tự bịa hoặc lấy tin sai khu vực/sai loại hình để lấp chỗ trống. Anh/Chị có thể nới ngân sách, mở rộng khu vực, bỏ bớt tiêu chí phụ hoặc hỏi checklist pháp lý/giao dịch trước khi tìm tin.
            """;
        }

        private static string DirectAnswer(string original, string normalized, Scenario sc, Dictionary<string, string> slots, PageInfo page)
        {
            return sc.Name switch
            {
                "Buy" when sc.Intent == "BuySafetyAdviceIntent" => BuySafetyAnswer(),
                "BuyAdvice" => BuyNeedAdviceAnswer(normalized, slots),
                "RentAdvice" => RentNeedAdviceAnswer(normalized, slots),
                "Legal" => LegalAnswer(normalized),
                "Transaction" => TransactionAnswer(normalized),
                "Loan" => LoanAnswer(slots, normalized),
                "Posting" => PostingAnswer(normalized),
                "Project" => ProjectAnswer(normalized),
                "Care" => CareAnswer(normalized, slots),
                "Market" => MarketAnswer(normalized),
                "MultiIntent" => MultiIntentAnswer(normalized, slots),
                _ => GeneralAnswer()
            };
        }

        private static string MultiIntentAnswer(string n, Dictionary<string, string> slots)
        {
            StringBuilder sb = new();
            sb.AppendLine("Em thấy câu hỏi của Anh/Chị có nhiều ý, em tách ra để trả lời đúng từng phần:");
            sb.AppendLine();

            int index = 1;
            if (HasPropertyNeedIntent(n))
            {
                sb.AppendLine($"**{index++}. Về nhu cầu bất động sản**");
                string type = PropertyType(n) ?? slots.GetValueOrDefault("property_type", "bất động sản");
                string area = AreaName(n) ?? slots.GetValueOrDefault("area_name", "khu vực Anh/Chị quan tâm");
                string budget = MoneyValues(n).Any() ? Price(MoneyValues(n).Max()) : slots.TryGetValue("budget_max", out string? b) && decimal.TryParse(b, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal bd) ? Price(bd) : "ngân sách đã nêu";
                sb.AppendLine($"- Em hiểu Anh/Chị đang quan tâm **{type.ToLower(Vi)}** tại **{area}** với mức khoảng **{budget}**. Nếu đủ loại BĐS + khu vực cụ thể + ngân sách, em mới lọc tin SQL; nếu khu vực còn rộng như toàn Khánh Hòa thì nên chọn thêm Nha Trang, Diên Khánh, Cam Lâm, Ninh Hòa, Cam Ranh... để tránh gợi ý sai.");
                sb.AppendLine();
            }

            if (Legal(n) || BuildPermissionQuestion(n))
            {
                sb.AppendLine($"**{index++}. Về pháp lý**");
                if (BuildPermissionQuestion(n))
                    sb.AppendLine("- Muốn biết đất có được xây nhà không, cần xem trên sổ là đất ở hay loại đất khác, kiểm tra quy hoạch/lộ giới, điều kiện cấp phép xây dựng và hỏi cơ quan có thẩm quyền trước khi đặt cọc.");
                else
                    sb.AppendLine("- Cần kiểm tra sổ gốc, chủ sở hữu, quy hoạch/lộ giới, tranh chấp, thế chấp, mục đích sử dụng đất, diện tích thực tế và điều kiện sang tên.");
                sb.AppendLine();
            }

            if (Loan(n))
            {
                sb.AppendLine($"**{index++}. Về vay ngân hàng**");
                sb.AppendLine("- Có sổ đỏ/sổ hồng là điểm thuận lợi, nhưng ngân hàng vẫn thẩm định pháp lý, quy hoạch, giá trị tài sản, thu nhập, nợ hiện có và lịch sử tín dụng. Muốn tính áp lực vay, Anh/Chị gửi thêm giá tài sản, vốn tự có, thu nhập tháng và thời hạn vay.");
                sb.AppendLine();
            }

            if (Transaction(n))
            {
                sb.AppendLine($"**{index++}. Về giao dịch**");
                sb.AppendLine("- Đi theo luồng: xem thực tế → kiểm tra pháp lý → đặt cọc có điều kiện hoàn cọc rõ → công chứng → kê khai thuế phí → sang tên → bàn giao.");
                sb.AppendLine();
            }

            if (Posting(n))
            {
                sb.AppendLine($"**{index++}. Về đăng tin/bán tài sản**");
                sb.AppendLine("- Nên viết tiêu đề rõ, mô tả trung thực, ảnh thật, giá minh bạch; không ghi quá đà như ‘bao lời’ hoặc ‘chắc chắn tăng giá’.");
                sb.AppendLine();
            }

            if (Project(n))
            {
                sb.AppendLine($"**{index++}. Về dự án**");
                sb.AppendLine("- Cần kiểm tra chủ đầu tư, pháp lý dự án, điều kiện mở bán, tiến độ, bảo lãnh ngân hàng nếu áp dụng, hợp đồng mẫu và điều kiện cấp sổ.");
                sb.AppendLine();
            }

            sb.AppendLine("Thông tin này là tư vấn ban đầu, không thay thế ngân hàng, luật sư, công chứng viên hoặc cơ quan nhà nước.");
            return sb.ToString();
        }

        private static string BuyNeedAdviceAnswer(string n, Dictionary<string, string> slots)
        {
            if (ContainsAny(n, "cam lam", "dien khanh", "o on dinh")) return """
            Nếu có khoảng **2 tỷ** và mua để ở ổn định, Anh/Chị nên so theo nhu cầu sống thật hơn là chạy theo tin đồn tăng giá.

            **Diên Khánh** thường hợp với nhu cầu ở lâu dài nếu Anh/Chị muốn gần Nha Trang hơn, tiện chợ/trường/dịch vụ, nhịp sống ổn định và dễ ở ngay. **Cam Lâm** có thể hợp nếu Anh/Chị chấp nhận xa trung tâm hơn, ưu tiên không gian rộng hơn hoặc kỳ vọng dài hạn, nhưng cần kiểm tra kỹ quy hoạch và hạ tầng thực tế.

            Em gợi ý cách chọn:
            1. Đi thực tế cả ban ngày và buổi tối để xem dân cư, đường, ngập, tiếng ồn.
            2. Ưu tiên sổ riêng, đất ở/thổ cư rõ, đường vào hợp pháp.
            3. Kiểm tra quy hoạch/lộ giới trước khi cọc.
            4. Chừa tiền xây/sửa nhà, thuế phí, nội thất; không dồn hết 2 tỷ vào giá đất.
            5. Nếu đi làm/học ở Nha Trang thường xuyên, Diên Khánh có thể an toàn hơn về sinh hoạt.

            Kết luận: mua để ở ổn định thì em nghiêng về **khu dân cư hiện hữu, pháp lý rõ, tiện ích gần**; không nên chọn chỉ vì lời hứa tăng giá.
            """;

            if (ContainsAny(n, "chon xa nao", "dien khanh", "chua biet nen chon")) return """
            Với nhu cầu mua lô đất ở Diên Khánh khoảng ngân sách đã nêu, Anh/Chị nên chọn khu theo **mức độ thuận tiện để ở thật**:

            1. Gần trung tâm/chợ/trường/y tế nếu gia đình ở lâu dài.
            2. Đường vào rõ, không quá sâu, xe ô tô hoặc xe máy đi thuận tiện tùy ngân sách.
            3. Có dân cư hiện hữu, điện nước ổn định, không quá vắng.
            4. Sổ riêng, mục đích sử dụng đất phù hợp xây nhà.
            5. Kiểm tra quy hoạch/lộ giới và hỏi rõ có bị tranh chấp/thế chấp không.

            Với ngân sách khoảng **1,5 tỷ**, không nên chỉ hỏi “xã nào rẻ nhất”; nên chọn lô pháp lý chắc, đường tốt, tiện sinh hoạt. Nếu muốn em lọc tin thật trong website, Anh/Chị gửi thêm yêu cầu diện tích/đường ô tô hay xe máy là được.
            """;

            if (ContainsAny(n, "900 trieu", "900tr", "kha thi")) return """
            Với khoảng **900 triệu** để mua đất ở Khánh Hòa rồi sau này xây nhà, em đánh giá là **có thể khả thi nhưng phải chọn khu vực và tiêu chí rất thực tế**.

            Anh/Chị nên lưu ý:
            1. Nha Trang thường khó tìm đất pháp lý rõ trong mức này, trừ lô nhỏ/vị trí xa hoặc đường nhỏ.
            2. Có thể cân nhắc khu xa trung tâm hơn như Diên Khánh, Ninh Hòa, Cam Lâm, Vạn Ninh tùy dữ liệu thực tế.
            3. Phải ưu tiên sổ riêng, đường vào hợp pháp, không vướng quy hoạch.
            4. Nếu sau này xây nhà, cần chừa thêm chi phí xây dựng, giấy phép, san lấp, điện nước.
            5. Không nên mua đất giấy tay hoặc đất chưa rõ mục đích sử dụng chỉ vì giá rẻ.

            Nếu muốn lọc tin chính xác, Anh/Chị nên chọn thêm khu vực cụ thể, ví dụ Diên Khánh hoặc Ninh Hòa, để em không tìm quá rộng toàn tỉnh.
            """;

            if (ContainsAny(n, "gan bien", "von khong nhieu")) return """
            Mua đất gần biển để đầu tư khi vốn không nhiều thì cần rất thận trọng. Đất gần biển thường giá cao, dễ bị đẩy kỳ vọng, và nếu pháp lý/quy hoạch chưa rõ thì rủi ro lớn.

            Anh/Chị nên kiểm tra trước:
            1. Đất có sổ riêng không, mục đích sử dụng là gì.
            2. Có vướng quy hoạch biển, hành lang bảo vệ, đường dự phóng hoặc dự án không.
            3. Đường vào có hợp pháp, xe đi được quanh năm không.
            4. Thanh khoản khu đó có thật không hay chỉ là lời môi giới.
            5. Tổng vốn sau mua: thuế phí, san lấp, giữ đất, lãi vay nếu có.

            Nếu vốn mỏng, em khuyên không nên chạy theo chữ “gần biển” bằng mọi giá. Nên ưu tiên pháp lý rõ, giá hợp lý, dễ bán lại và không dùng đòn bẩy vay quá cao.
            """;

            if (ContainsAny(n, "vi tri dien tich hay phap ly", "phap ly hay vi tri", "uu tien phap ly"))
                return "Nếu mua lần đầu, Anh/Chị nên ưu tiên theo thứ tự: **pháp lý rõ → vị trí phù hợp nhu cầu → diện tích/giá**. Pháp lý không rõ thì vị trí đẹp cũng không nên cọc. Sau khi chắc sổ gốc, chủ sở hữu, quy hoạch, tranh chấp, thế chấp và điều kiện sang tên, Anh/Chị mới cân đối vị trí đi lại, tiện ích, dân cư và diện tích.";

            if (ContainsAny(n, "dat mat tien hay nha xay san", "vua o vua kinh doanh"))
                return "Nếu vừa ở vừa kinh doanh nhỏ, nhà xây sẵn hoặc nhà mặt tiền trong khu dân cư hiện hữu thường dễ vận hành hơn vì có sẵn công trình, điện nước và dòng người. Đất mặt tiền hợp khi Anh/Chị có thêm vốn xây/sửa và muốn thiết kế theo nhu cầu. Dù chọn phương án nào, cần kiểm tra pháp lý, lộ giới, chỗ đậu xe, tiếng ồn, PCCC/giấy phép nếu ngành nghề có điều kiện và khả năng bán lại.";

            if (ContainsAny(n, "gia re", "quy hoach", "hoi nguoi ban"))
                return "Khi gặp đất giá rẻ nhưng sợ quy hoạch, Anh/Chị nên hỏi người bán: có sổ gốc không, số thửa/tờ bản đồ, mục đích sử dụng đất, có vướng quy hoạch/lộ giới/kế hoạch thu hồi không, có tranh chấp/thế chấp không, lý do bán rẻ, đường vào có hợp pháp không và có đồng ý cho kiểm tra quy hoạch trước khi cọc không. Hợp đồng cọc phải ghi điều kiện hoàn cọc nếu pháp lý/quy hoạch không đúng cam kết.";

            return MarketAnswer(n);
        }

        private static string RentNeedAdviceAnswer(string n, Dictionary<string, string> slots)
        {
            if (ContainsAny(n, "coc 6 thang", "coc sau thang", "dat coc", "tien coc")) return """
            Chủ nhà đòi **cọc 6 tháng** là mức khá cao, không phải lúc nào cũng sai nhưng rủi ro cho người thuê lớn hơn.

            Anh/Chị nên kiểm tra và thương lượng:
            1. Lý do cọc cao là gì: mặt bằng kinh doanh, sửa chữa, nội thất hay rủi ro ngành nghề.
            2. Tiền cọc được hoàn trong trường hợp nào, thời hạn hoàn bao lâu.
            3. Nếu chủ nhà đơn phương lấy lại nhà/sang nhượng/bán nhà thì bồi thường thế nào.
            4. Có được sang nhượng, cho thuê lại, cải tạo, treo biển hiệu không.
            5. Nên chuyển khoản, ghi rõ nội dung tiền cọc và có hợp đồng/bàn giao hiện trạng.

            Nếu chưa rõ pháp lý chủ nhà hoặc hợp đồng không ghi điều kiện hoàn cọc, Anh/Chị không nên giao cọc lớn ngay.
            """;

            if (ContainsAny(n, "thu cung", "nuoi thu cung")) return """
            Thuê nhà để ở và nuôi thú cưng thì Anh/Chị nên hỏi rõ trước khi cọc, tránh sau này bị buộc dọn đi.

            Nên hỏi chủ nhà:
            1. Có cho nuôi thú cưng không, loại gì, số lượng bao nhiêu.
            2. Có phụ phí vệ sinh, khử mùi hoặc tăng cọc không.
            3. Trách nhiệm nếu thú cưng làm hư nội thất/sàn/tường/cửa.
            4. Quy định tiếng ồn, khu vực nuôi, vệ sinh chung nếu là căn hộ/phòng trọ.
            5. Điều khoản này phải ghi vào hợp đồng hoặc phụ lục, không chỉ nói miệng.

            Khi nhận nhà nên chụp ảnh hiện trạng để tránh tranh chấp tiền cọc lúc trả nhà.
            """;

            if (ContainsAny(n, "kinh doanh", "mo quan", "ca phe", "mat bang", "van phong")) return """
            Thuê nhà/mặt bằng để kinh doanh thì ngoài giá thuê, Anh/Chị cần kiểm tra kỹ khả năng kinh doanh hợp pháp và ổn định.

            Checklist nên hỏi:
            1. Chủ nhà có đúng quyền cho thuê không, có sổ/hợp đồng ủy quyền hợp lệ không.
            2. Thời hạn thuê, thời gian tăng giá, mức tăng tối đa và điều kiện gia hạn.
            3. Được sửa chữa, treo biển hiệu, đăng ký kinh doanh, xuất hóa đơn/khai thuế không.
            4. Điện nước tính giá nào, có đồng hồ riêng không.
            5. Quy định PCCC, tiếng ồn, chỗ để xe, giờ hoạt động, ngành nghề bị cấm/hạn chế.
            6. Điều kiện hoàn cọc nếu không xin được giấy phép hoặc mặt bằng không đúng cam kết.

            Với mặt bằng kinh doanh, hợp đồng càng phải rõ vì chi phí setup ban đầu thường lớn.
            """;

            if (ContainsAny(n, "khong co hop dong cong chung", "cong chung"))
                return "Thuê nhà không công chứng không đồng nghĩa chắc chắn vô hiệu trong mọi trường hợp, nhưng rủi ro sẽ cao hơn nếu hợp đồng sơ sài hoặc người cho thuê không đúng chủ. Anh/Chị nên có hợp đồng bằng văn bản, kiểm tra giấy tờ chủ nhà, ghi rõ giá thuê, cọc, thời hạn, tăng giá, sửa chữa, chấm dứt trước hạn, bàn giao hiện trạng và trách nhiệm hoàn cọc. Với hợp đồng giá trị lớn/dài hạn/mặt bằng kinh doanh, nên cân nhắc công chứng/chứng thực hoặc nhờ người có chuyên môn xem trước.";

            if (ContainsAny(n, "o lau dai", "dieu khoan"))
                return "Thuê nhà ở lâu dài nên ghi rõ trong hợp đồng: thời hạn thuê, giá thuê và chu kỳ tăng giá, tiền cọc và điều kiện hoàn cọc, quyền sửa chữa/lắp đặt, ai chịu phí điện nước/internet/rác/quản lý, điều kiện chấm dứt trước hạn, thời gian báo trước, bàn giao hiện trạng và xử lý hư hỏng. Không nên chỉ thỏa thuận miệng.";

            return "Khi thuê bất động sản, Anh/Chị nên kiểm tra 5 nhóm chính: người cho thuê có quyền cho thuê không, giá thuê/cọc/thanh toán, thời hạn và tăng giá, hiện trạng bàn giao, điều kiện chấm dứt và hoàn cọc. Nếu thuê để kinh doanh, cần kiểm tra thêm giấy phép, điện nước, PCCC, biển hiệu, chỗ đậu xe và quy định ngành nghề.";
        }

        private static string BuySafetyAnswer() => """
        Em hiểu Anh/Chị muốn **mua đất nhưng chưa biết mua như thế nào cho an toàn**. Câu này là tư vấn an toàn, nên em **không đề xuất tin ngay**.

        **Checklist mua đất an toàn lần đầu**
        1. Xác định nhu cầu: mua để ở, xây nhà, đầu tư, kinh doanh hay giữ tài sản; ngân sách và có vay hay không.
        2. Xem sổ đỏ/sổ hồng bản gốc, không chỉ xem ảnh chụp; đối chiếu người bán với tên trên sổ.
        3. Kiểm tra quy hoạch/lộ giới, tranh chấp, thế chấp, ngăn chặn giao dịch, mục đích sử dụng đất.
        4. Xem thực tế đường vào, điện nước, thoát nước, ranh giới, diện tích thực tế và khu dân cư.
        5. Không đặt cọc vội. Hợp đồng cọc phải ghi rõ giá, thời hạn công chứng, phạt cọc/hoàn cọc và trách nhiệm nếu pháp lý không đúng.
        6. Nếu có vay ngân hàng, nên hỏi thẩm định sơ bộ trước khi cọc lớn.

        Đây là tư vấn tham khảo, không thay thế luật sư, công chứng viên hoặc cơ quan nhà nước.

        Nếu Anh/Chị muốn em lọc tin phù hợp, hãy cho em biết: **khu vực**, **ngân sách**, **diện tích mong muốn**, và **mua để ở hay đầu tư**.
        """;

        private static string PageAnalysisAnswer(PageInfo page, string normalized)
        {
            if (!page.HasUsefulContext)
            {
                return "Em chưa đọc được đầy đủ thông tin tin đang xem nên không muốn phân tích bừa. Anh/Chị gửi giúp em tiêu đề, giá, diện tích, vị trí và pháp lý hiển thị; em sẽ đánh giá rủi ro và bước nên làm tiếp.";
            }

            string title = PageField(page, "Tiêu đề") ?? PageField(page, "Tiêu đề tin") ?? CleanTitle(page.PageTitle, "tin đang xem");
            string price = PageField(page, "Giá") ?? "chưa thấy giá rõ trong ngữ cảnh";
            string area = PageField(page, "Diện tích") ?? "chưa thấy diện tích rõ trong ngữ cảnh";
            string unitPrice = PageField(page, "Đơn giá") ?? "chưa thấy đơn giá rõ";
            string location = PageField(page, "Vị trí") ?? "chưa thấy vị trí rõ trong ngữ cảnh";
            string region = PageField(page, "Khu vực") ?? "chưa thấy khu vực rõ";
            string ward = PageField(page, "Phường/Xã") ?? "chưa thấy xã/phường rõ";
            string type = PageField(page, "Loại bất động sản") ?? PageField(page, "Loại BĐS") ?? "chưa thấy loại hình rõ";
            string legal = PageField(page, "Pháp lý") ?? "chưa thấy pháp lý rõ";
            string utilities = PageField(page, "Tiện ích") ?? "chưa thấy tiện ích rõ";
            string description = PageField(page, "Mô tả") ?? "chưa thấy mô tả rõ";
            string bedrooms = PageField(page, "Phòng ngủ") ?? "chưa thấy";
            string bathrooms = PageField(page, "Phòng tắm") ?? "chưa thấy";
            string status = PageField(page, "Tình trạng tin") ?? "chưa thấy";
            string postedDate = PageField(page, "Ngày đăng") ?? "chưa thấy";
            string owner = PageField(page, "Người đăng") ?? PageField(page, "Chủ tin") ?? PageField(page, "Người bán") ?? PageField(page, "Môi giới");
            string targetArea = AreaName(normalized) ?? string.Empty;

            if (IsLocationDistanceQuestion(normalized))
            {
                string target = string.IsNullOrWhiteSpace(targetArea) ? "khu vực Anh/Chị hỏi" : targetArea;
                bool sameArea = !string.IsNullOrWhiteSpace(targetArea) &&
                    (ContainsCompact(region, targetArea) || ContainsCompact(ward, targetArea) || ContainsAreaPhrase(location, Normalize(targetArea)));

                if (sameArea)
                {
                    return $"""
                    Tin này đang thuộc/nhắc tới **{target}** theo thông tin trang cung cấp.

                    **Vị trí em đọc được:** {location}
                    - Khu vực: **{region}**
                    - Phường/xã: **{ward}**

                    Nếu Anh/Chị mua để ở hoặc đi lại hằng ngày, vẫn nên mở bản đồ và đi thử thực tế từ tài sản đến nơi làm việc, trường học, chợ/bệnh viện vào giờ cao điểm. Thông tin trên tin đăng chỉ là bước đầu, không thay thế kiểm tra thực tế.
                    """;
                }

                string relationNote = BuildAreaRelationNote(region, ward, targetArea);
                return $"""
                Tin này **không nằm trực tiếp ở {target}** theo dữ liệu em đang đọc được.

                **Vị trí tin đăng hiện tại**
                - Khu vực: **{region}**
                - Phường/xã: **{ward}**
                - Địa chỉ/vị trí: **{location}**

                {relationNote}

                **Cách kiểm tra cho chắc:** Anh/Chị nên bấm xem bản đồ/địa chỉ, nhập điểm đến là **{target}**, xem thời gian di chuyển thực tế bằng xe máy/ô tô vào giờ cao điểm. Nếu mục tiêu là đi lại thường xuyên tới {target}, không nên chỉ dựa vào chữ “gần” trong tin đăng.
                """;
            }

            if (ContainsAny(normalized, "dau tu", "mua de dau tu", "dau tu duoc khong", "sinh loi", "khai thac", "cho thue duoc khong"))
            {
                return $"""
                **Trả lời nhanh:** Tin này **có thể đưa vào danh sách xem xét đầu tư**, nhưng em chưa khuyên xuống tiền ngay vì cần kiểm tra thêm giá thị trường, thanh khoản, quy hoạch và khả năng khai thác thực tế.

                **Dữ liệu em đang đọc được**
                - Loại BĐS: **{type}**
                - Giá/diện tích: **{price}**, **{area}**; đơn giá **{unitPrice}**
                - Vị trí: **{region}**, **{ward}**
                - Pháp lý hiển thị: **{legal}**
                - Mô tả/tiện ích: **{Trim(description + " " + utilities, 500)}**

                **Nếu mua để đầu tư, Anh/Chị cần kiểm tra 5 điểm:**
                1. Giá này có cao hơn/thấp hơn các tin cùng khu, cùng diện tích, cùng pháp lý không.
                2. Khu vực có nhu cầu thật để ở, nghỉ dưỡng, kinh doanh hoặc cho thuê không.
                3. Pháp lý có sạch không: sổ gốc, chủ sở hữu, quy hoạch, lộ giới, tranh chấp, thế chấp.
                4. Thanh khoản sau này: tài sản có dễ bán lại không, nhóm khách mua có rộng không.
                5. Chi phí sau mua: sửa chữa, nội thất, thuế phí, quản lý, lãi vay nếu có.

                **Kết luận sơ bộ:** phù hợp để **đi xem và thẩm định thêm**, chưa phù hợp để đặt cọc ngay nếu Anh/Chị chưa có dữ liệu so sánh giá và chưa kiểm tra pháp lý/quy hoạch.
                """;
            }

            if (ContainsAny(normalized, "co nen dat coc", "dat coc ngay", "coc ngay", "xuong tien", "chot ngay"))
            {
                return $"""
                **Em không khuyên đặt cọc ngay chỉ dựa trên tin đăng.**

                Với tin **{title}**, trước khi cọc Anh/Chị cần kiểm tra:
                1. Sổ gốc: chủ sở hữu, diện tích, mục đích sử dụng, tài sản gắn liền với đất.
                2. Quy hoạch/lộ giới, tranh chấp, thế chấp, ngăn chặn giao dịch.
                3. Vị trí thực tế có đúng mô tả: **{location}** hay không.
                4. Giá **{price}** có hợp lý so với các tài sản tương tự tại **{region}** không.
                5. Người nhận cọc có đúng chủ sở hữu hoặc có ủy quyền hợp lệ không.

                Nếu vẫn đặt cọc, hợp đồng cọc phải ghi rõ điều kiện **hoàn cọc** nếu pháp lý/quy hoạch/diện tích/vị trí không đúng như cam kết.
                """;
            }

            if (ContainsAny(normalized, "hoi nguoi ban gi", "can hoi gi", "di xem hoi gi", "nen hoi gi", "hoi chu nha gi", "hoi chu dat gi"))
            {
                return $"""
                Khi đi xem tin này, Anh/Chị nên hỏi người bán/người đăng các câu sau:

                1. **Pháp lý:** sổ gốc đang ở đâu, ai đứng tên, có đồng sở hữu/vợ chồng/thừa kế/ủy quyền không.
                2. **Quy hoạch:** tài sản có vướng quy hoạch, lộ giới, hành lang bảo vệ, kế hoạch thu hồi không.
                3. **Thế chấp/tranh chấp:** có đang thế chấp ngân hàng, bị ngăn chặn giao dịch hoặc tranh chấp ranh/lối đi không.
                4. **Giá:** giá **{price}** đã bao gồm thuế phí chưa, còn thương lượng không, lý do bán là gì.
                5. **Hiện trạng:** diện tích thực tế có khớp **{area}** không, đường vào/điện nước/thoát nước như thế nào.
                6. **Sử dụng:** nếu mua để ở/kinh doanh/đầu tư thì có hạn chế gì không.
                7. **Giao dịch:** thời hạn công chứng, mốc thanh toán và điều kiện hoàn cọc nếu phát hiện sai thông tin.
                """;
            }

            if (ContainsAny(normalized, "vay", "ngan hang", "tra gop", "co vay duoc khong", "the chap"))
            {
                return $"""
                Với tin này, em **chưa thể khẳng định ngân hàng cho vay hay không** chỉ từ tin đăng.

                **Thông tin đang có:** giá **{price}**, pháp lý hiển thị **{legal}**, loại BĐS **{type}**, khu vực **{region}**.

                Ngân hàng thường kiểm tra:
                1. Sổ gốc, chủ sở hữu, tình trạng tranh chấp/thế chấp/ngăn chặn.
                2. Quy hoạch/lộ giới và khả năng sang tên.
                3. Giá trị thẩm định của ngân hàng, có thể thấp hơn giá bán **{price}**.
                4. Thu nhập, nợ hiện có, lịch sử tín dụng của người vay.
                5. Tỷ lệ cho vay và tài sản có đủ điều kiện nhận thế chấp không.

                Nếu Anh/Chị định vay, nên hỏi ngân hàng thẩm định sơ bộ **trước khi đặt cọc lớn**.
                """;
            }

            if (ContainsAny(normalized, "gan bien", "gan cho", "gan truong", "gan benh vien", "tien ich", "xung quanh co gi", "duong vao", "o to vao duoc", "duong o to"))
            {
                return $"""
                Em chỉ có thể dựa trên dữ liệu trang đang cung cấp, chưa thay thế việc xem thực tế.

                **Thông tin vị trí/tiện ích đang thấy:**
                - Vị trí: **{location}**
                - Khu vực: **{region}**, phường/xã **{ward}**
                - Tiện ích/mô tả: **{utilities}**

                Anh/Chị nên kiểm tra thêm khi đi xem:
                1. Từ tài sản đến chợ, trường học, bệnh viện, biển/trung tâm mất bao lâu.
                2. Đường vào là đường công cộng hay lối đi chung, có tranh chấp không.
                3. Ô tô có vào được thật không, đường có ngập/khó quay đầu không.
                4. Khu dân cư xung quanh có ở thật hay còn thưa vắng.
                """;
            }

            if (ContainsAny(normalized, "o khu vuc nao", "khu vuc nao", "o dau", "dia chi", "vi tri", "dat nay o"))
                return $"Tin này ở **{region}**, xã/phường **{ward}**. Vị trí trang cung cấp: **{location}**. Anh/Chị nên đối chiếu địa chỉ này khi đi xem thực tế và kiểm tra trên giấy tờ/sổ gốc để tránh sai vị trí.";

            if (ContainsAny(normalized, "ai la chu", "chu cua tin", "nguoi dang", "ho uy tin", "uy tin khong", "chu tin"))
            {
                string ownerLine = string.IsNullOrWhiteSpace(owner) ? "Hiện ngữ cảnh trang chưa cung cấp rõ tên/người đăng tin nên em không thể kết luận họ là ai." : $"Ngữ cảnh trang cho thấy người đăng/chủ tin là: **{owner}**.";
                return $"""
                {ownerLine}

                Về mức độ uy tín, em **không nên khẳng định chắc chắn** chỉ dựa trên tin đăng. Anh/Chị nên kiểm tra thêm:
                1. Tài khoản có xác thực không, số điện thoại/email có rõ không.
                2. Người liên hệ có đúng chủ sở hữu hoặc được ủy quyền hợp lệ không.
                3. Có cho xem sổ gốc, CCCD và giấy tờ liên quan không.
                4. Có lịch sử đăng tin bất thường, giá quá rẻ, yêu cầu chuyển cọc sớm không.
                5. Khi đi xem nên hẹn tại tài sản thật hoặc văn phòng rõ ràng, không chuyển tiền trước khi xác minh.
                """;
            }

            if (ContainsAny(normalized, "gia re", "re hon khu vuc", "duong nho", "hem nho", "co so", "co nen mua khong", "co nen mua", "nen mua khong", "tin nay nen mua"))
                return $"""
                **Trả lời nhanh:** Em chỉ xếp tin này vào nhóm **có thể đi xem và thẩm định thêm**, chưa nên kết luận mua ngay.

                **Điểm đang có lợi**
                - Pháp lý hiển thị: **{legal}**.
                - Giá/diện tích: **{price}**, **{area}**, đơn giá **{unitPrice}**.
                - Khu vực: **{region}**, xã/phường **{ward}**.
                - Loại hình: **{type}**.

                **Rủi ro cần kiểm tra trước khi mua**
                1. Sổ gốc, chủ sở hữu, quy hoạch/lộ giới, tranh chấp, thế chấp.
                2. Vị trí thực tế có đúng mô tả không, đường vào có hợp pháp không.
                3. Giá có bị cao/ảo so với tài sản cùng khu vực không.
                4. Nếu mua để ở: tiện ích, an ninh, điện nước, thoát nước, khoảng cách đi lại.
                5. Nếu mua đầu tư: thanh khoản, khả năng cho thuê/bán lại, chi phí giữ tài sản.

                **Kết luận sơ bộ:** có thể đi xem, chụp thông tin sổ để kiểm tra, so sánh thêm 3–5 tin tương tự. Không nên đặt cọc khi chưa xác minh pháp lý và quy hoạch.
                """;

            if (ContainsAny(normalized, "bao cao", "vi pham", "tin sai", "lua dao", "tin gia"))
                return $"""
                Nếu Anh/Chị nghi tin này vi phạm hoặc có dấu hiệu sai sự thật, hãy dùng chức năng **Báo cáo vi phạm** trên trang chi tiết tin.

                Nên ghi rõ:
                1. Lý do báo cáo: sai giá, sai vị trí, ảnh không đúng, pháp lý không rõ, nghi lừa đảo, đã bán nhưng vẫn đăng.
                2. Bằng chứng nếu có: ảnh chụp màn hình, nội dung chat, số điện thoại, thông tin chuyển khoản.
                3. Mã/link tin: **{page.PageUrl}**.

                Không nên tiếp tục chuyển cọc hoặc cung cấp giấy tờ cá nhân nếu chưa xác minh được người đăng và pháp lý tài sản.
                """;

            if (ContainsAny(normalized, "ban lai", "thanh khoan", "sau nay ban", "de ban lai"))
                return $"""
                Với tin này, nếu xét khả năng **bán lại/thanh khoản**, em nhìn theo dữ liệu trang cung cấp:

                - Loại BĐS: **{type}**.
                - Giá: **{price}**, diện tích **{area}**, đơn giá **{unitPrice}**.
                - Khu vực: **{region}**, xã/phường **{ward}**.
                - Pháp lý hiển thị: **{legal}**.
                - Tiện ích/điểm mô tả: **{utilities}**.

                Khả năng bán lại thường tốt hơn khi: pháp lý rõ, giá không quá cao so với khu vực, đường vào thuận tiện, khu dân cư/tiện ích có nhu cầu thật, và loại hình phù hợp nhu cầu địa phương. Với khu vực **{region}**, Anh/Chị nên kiểm tra thêm thanh khoản thực tế: xung quanh có giao dịch không, tin tương tự đăng bao lâu, đường vào và quy hoạch có ảnh hưởng không.

                Em không cam kết chắc chắn bán lại dễ. Trước khi mua để bán lại, nên so sánh thêm 3–5 tin cùng khu vực, cùng diện tích và cùng pháp lý.
                """;

            if (ContainsAny(normalized, "xay nha", "cat nha", "de o", "o lau dai", "mua de o"))
                return $"""
                Nếu mua tin này để **ở lâu dài/xây nhà**, em đánh giá sơ bộ như sau:

                **Thông tin chính**
                - Loại BĐS: **{type}**.
                - Giá: **{price}**; diện tích: **{area}**.
                - Khu vực: **{region}**, xã/phường **{ward}**.
                - Pháp lý hiển thị: **{legal}**.
                - Tiện ích/mô tả: **{Trim(utilities + " " + description, 500)}**.

                **Có thể phù hợp để ở nếu:** pháp lý đúng là sổ riêng/sổ hồng riêng, đất/công trình có mục đích sử dụng phù hợp, đường vào và hạ tầng ổn, khu dân cư phù hợp sinh hoạt gia đình.

                **Cần kiểm tra trước khi quyết định:**
                1. Trên sổ ghi loại đất gì và tài sản gắn liền với đất ra sao.
                2. Có vướng quy hoạch/lộ giới/hành lang bảo vệ không.
                3. Có được cấp phép xây dựng/cải tạo nếu cần không.
                4. Đường vào, điện nước, thoát nước, an ninh và khoảng cách tới tiện ích.
                5. Diện tích thực tế có khớp sổ và ranh giới ngoài thực địa không.
                """;

            if (ContainsAny(normalized, "so do", "so hong", "phap ly", "kiem tra gi", "rui ro"))
                return $"""
                Với tin này, pháp lý trang hiển thị là: **{legal}**. Tuy nhiên, thông tin trên tin đăng chỉ là bước đầu.

                Anh/Chị cần kiểm tra trực tiếp:
                1. Sổ gốc: số thửa, tờ bản đồ, diện tích, mục đích sử dụng đất, thời hạn sử dụng.
                2. Chủ sở hữu/người ký có đúng tên trên sổ hoặc có ủy quyền hợp lệ không.
                3. Quy hoạch, lộ giới, tranh chấp, thế chấp, ngăn chặn giao dịch.
                4. Diện tích thực tế và ranh giới có khớp sổ không.
                5. Nếu là nhà/công trình: có hoàn công hoặc giấy tờ xây dựng liên quan không.

                Không nên đặt cọc chỉ dựa trên chữ “{legal}” trong tin đăng.
                """;

            if (ContainsAny(normalized, "gia", "hop ly", "thuong luong", "mac ca"))
                return $"""
                Tin này đang hiển thị giá **{price}**, diện tích **{area}**, đơn giá **{unitPrice}** tại **{region}**.

                Em chưa thể kết luận chắc chắn rẻ hay đắt nếu không có dữ liệu so sánh cùng khu vực/cùng loại hình/cùng pháp lý. Khi thương lượng, Anh/Chị nên dựa vào:
                1. Giá các tin tương tự tại {region}.
                2. Đường vào, diện tích thực tế, pháp lý, quy hoạch.
                3. Chi phí sửa chữa/cải tạo/xây dựng nếu có.
                4. Tin đăng lâu chưa bán hoặc thông tin còn thiếu là cơ sở thương lượng.
                """;

            return $"""
            Em đang đọc được thông tin chính của tin này:

            - Tiêu đề: **{title}**
            - Loại BĐS: **{type}**
            - Giá: **{price}**; diện tích: **{area}**; đơn giá: **{unitPrice}**
            - Vị trí: **{location}**
            - Khu vực: **{region}**; phường/xã: **{ward}**
            - Pháp lý hiển thị: **{legal}**
            - Phòng ngủ/phòng tắm: **{bedrooms}/{bathrooms}**
            - Tình trạng/ngày đăng: **{status}**, **{postedDate}**

            Anh/Chị có thể hỏi tiếp theo hướng rất cụ thể, ví dụ: **giá này hợp lý không, có nên đặt cọc không, mua để đầu tư được không, có gần Diên Khánh/Nha Trang không, pháp lý cần kiểm tra gì, nên hỏi người bán câu nào**.
            """;
        }

        private static string ProjectPageAnalysisAnswer(PageInfo page, string normalized)
        {
            string name = PageField(page, "Tên dự án") ?? CleanTitle(page.PageTitle, "dự án đang xem");
            string investor = PageField(page, "Chủ đầu tư") ?? "chưa thấy chủ đầu tư rõ trong ngữ cảnh";
            string location = PageField(page, "Khu vực") ?? PageField(page, "Vị trí") ?? "chưa thấy khu vực rõ";
            string legal = PageField(page, "Pháp lý") ?? "chưa thấy pháp lý rõ";
            string status = PageField(page, "Trạng thái") ?? "chưa thấy trạng thái rõ";
            string description = PageField(page, "Mô tả") ?? "chưa thấy mô tả rõ";
            string price = PageField(page, "Giá") ?? "chưa thấy giá rõ";
            string scale = PageField(page, "Quy mô") ?? "chưa thấy quy mô rõ";

            if (ContainsAny(normalized, "chu dau tu", "uy tin", "nang luc", "co dang tin khong"))
            {
                return $"""
                **Chủ đầu tư dự án này:** {investor}.

                Em không nên khẳng định uy tín chỉ dựa trên trang giới thiệu. Anh/Chị nên kiểm tra:
                1. Pháp nhân chủ đầu tư, giấy đăng ký doanh nghiệp, người đại diện.
                2. Các dự án đã triển khai trước đây, tiến độ bàn giao, phản hồi cư dân/khách hàng.
                3. Tình trạng pháp lý dự án: đất, quy hoạch, giấy phép, điều kiện mở bán.
                4. Ngân hàng bảo lãnh nếu là nhà ở hình thành trong tương lai và trường hợp pháp luật yêu cầu.
                5. Hợp đồng mẫu, tiến độ thanh toán, điều khoản phạt chậm bàn giao.

                Với dự án, uy tín phải được kiểm chứng bằng hồ sơ và lịch sử triển khai, không chỉ bằng quảng cáo.
                """;
            }

            if (ContainsAny(normalized, "phap ly", "giay to", "mo ban", "duoc ban", "so hong", "ra so", "bao lanh"))
            {
                return $"""
                **Pháp lý dự án cần kiểm tra**

                Dữ liệu trang đang có:
                - Dự án: **{name}**
                - Chủ đầu tư: **{investor}**
                - Khu vực: **{location}**
                - Pháp lý hiển thị: **{legal}**
                - Trạng thái: **{status}**

                Trước khi đặt cọc/booking/mua, Anh/Chị nên kiểm tra:
                1. Quyền sử dụng đất của dự án và mục đích sử dụng đất.
                2. Quy hoạch chi tiết, giấy phép xây dựng nếu thuộc trường hợp phải có.
                3. Điều kiện mở bán/chuyển nhượng theo loại sản phẩm.
                4. Bảo lãnh ngân hàng nếu là nhà ở hình thành trong tương lai và thuộc diện áp dụng.
                5. Hợp đồng mẫu, tiến độ thanh toán, điều khoản bàn giao, phí quản lý/quỹ bảo trì.
                6. Cam kết ra sổ/cấp giấy chứng nhận phải thể hiện bằng văn bản rõ ràng.

                Không nên tin chỉ vào lời tư vấn miệng hoặc quảng cáo “chắc chắn ra sổ/chắc chắn sinh lời”.
                """;
            }

            if (ContainsAny(normalized, "booking", "giu cho", "dat coc", "coc", "xuong tien"))
            {
                return $"""
                **Không nên chuyển tiền booking/đặt cọc dự án khi chưa đọc kỹ hồ sơ.**

                Với dự án **{name}**, Anh/Chị cần hỏi rõ:
                1. Tiền booking/cọc chuyển cho ai, tài khoản cá nhân hay tài khoản công ty.
                2. Phiếu giữ chỗ có hoàn tiền không, thời hạn hoàn tiền bao lâu.
                3. Nếu không mua tiếp hoặc dự án chưa đủ điều kiện bán thì xử lý tiền thế nào.
                4. Hợp đồng chính thức là hợp đồng mua bán, đặt cọc, hợp tác đầu tư hay góp vốn.
                5. Tiến độ thanh toán và nghĩa vụ thuế/phí phát sinh.

                Chỉ nên chuyển tiền khi thông tin chủ đầu tư, pháp lý và điều kiện hoàn tiền rõ ràng bằng văn bản.
                """;
            }

            if (ContainsAny(normalized, "dau tu", "cho thue", "sinh loi", "thanh khoan", "ban lai", "co nen mua", "nen mua khong"))
            {
                return $"""
                **Đánh giá sơ bộ dự án để mua/đầu tư**

                Dữ liệu em đang đọc được:
                - Dự án: **{name}**
                - Chủ đầu tư: **{investor}**
                - Khu vực: **{location}**
                - Giá/thông tin giá: **{price}**
                - Quy mô: **{scale}**
                - Pháp lý/trạng thái: **{legal}**, **{status}**

                **Có thể xem xét nếu:** vị trí có nhu cầu thật, chủ đầu tư có năng lực, pháp lý đủ điều kiện, tiến độ rõ, giá không vượt quá mặt bằng khu vực và sản phẩm có khả năng ở/cho thuê/bán lại.

                **Rủi ro cần tránh:** mua theo tin đồn, cam kết lợi nhuận không rõ điều kiện, pháp lý chưa đủ, thanh toán quá nhanh, hợp đồng bất lợi, chậm bàn giao, chưa rõ thời điểm cấp sổ.

                **Kết luận:** dự án này chỉ nên đưa vào danh sách khảo sát. Trước khi xuống tiền, Anh/Chị cần xem hồ sơ pháp lý và so sánh ít nhất 2–3 dự án/tài sản cùng khu vực.
                """;
            }

            return $"""
            Em đang đọc được thông tin chính của dự án này:

            - Tên dự án: **{name}**
            - Chủ đầu tư: **{investor}**
            - Khu vực: **{location}**
            - Pháp lý: **{legal}**
            - Trạng thái: **{status}**
            - Mô tả: **{Trim(description, 600)}**

            Anh/Chị có thể hỏi tiếp: **pháp lý dự án cần kiểm tra gì, chủ đầu tư có uy tín không, có nên booking không, mua để đầu tư được không, rủi ro dự án này là gì**.
            """;
        }

        private static bool IsLocationDistanceQuestion(string normalized)
        {
            return ContainsAny(normalized, "gan", "gan khong", "co gan", "cach", "bao xa", "xa khong", "di den", "di toi", "tu day den") &&
                   AreaName(normalized) != null;
        }

        private static string BuildAreaRelationNote(string region, string ward, string targetArea)
        {
            string current = Normalize(region + " " + ward);
            string target = Normalize(targetArea);

            if (string.IsNullOrWhiteSpace(target))
                return "Em chưa xác định rõ khu vực Anh/Chị muốn so sánh, nên chỉ có thể nói theo vị trí hiện tại của tin đăng.";

            if ((ContainsAny(current, "cam lam") && ContainsAny(target, "dien khanh")) ||
                (ContainsAny(current, "dien khanh") && ContainsAny(target, "cam lam")))
            {
                return "Cam Lâm và Diên Khánh là hai khu vực khác nhau. Một số vị trí có thể đi lại được, nhưng không nên hiểu là cùng khu hoặc sát nhau. Nếu cần đi Diên Khánh thường xuyên, thời gian di chuyển thực tế quan trọng hơn lời mô tả trong tin.";
            }

            if ((ContainsAny(current, "cam lam") && ContainsAny(target, "nha trang")) ||
                (ContainsAny(current, "dien khanh") && ContainsAny(target, "nha trang")))
            {
                return "Khu vực này có thể kết nối về Nha Trang tùy vị trí cụ thể, nhưng mức độ gần/xa phải kiểm tra bằng bản đồ và đi thực tế vì mỗi xã/đường nội khu khác nhau rất nhiều.";
            }

            return "Hai khu vực có thể khác nhau về khoảng cách, tiện ích, giá và thanh khoản. Em không tự đo km nếu trang chưa cung cấp bản đồ/tọa độ, để tránh nói sai.";
        }

        private static bool CurrentProjectQuestion(string n)
        {
            return ContainsAny(n,
                "du an nay", "project nay", "can ho nay", "shophouse nay", "nen mua khong", "co nen mua", "co nen booking", "booking duoc khong",
                "chu dau tu", "phap ly du an", "tien do", "ban giao", "ra so", "so hong", "dau tu duoc khong", "cho thue duoc khong",
                "rui ro du an", "hop dong", "bao lanh ngan hang", "cam ket loi nhuan");
        }

        private static string LegalAnswer(string n)
        {
            if (ContainsAny(n, "so do va so hong", "so hong voi so do", "khac nhau")) return """
            **Sổ đỏ và sổ hồng khác nhau thế nào?**

            Đây là cách gọi phổ biến theo màu bìa qua từng thời kỳ. Khi mua bán, Anh/Chị không nên chỉ nhìn tên gọi mà cần kiểm tra nội dung trên giấy chứng nhận:
            1. Người đứng tên/chủ sở hữu là ai.
            2. Số thửa, tờ bản đồ, diện tích, mục đích sử dụng đất.
            3. Thời hạn sử dụng đất, tài sản gắn liền với đất.
            4. Ghi chú hạn chế quyền, thế chấp, quy hoạch, lộ giới nếu có.
            5. Diện tích thực tế có khớp với giấy chứng nhận không.

            Trước khi cọc nên xem bản gốc và xác minh tại văn phòng đăng ký đất đai/công chứng.
            """;

            if (ContainsAny(n, "quy hoach", "dinh quy hoach", "vuong quy hoach", "kiem tra quy hoach")) return """
            **Kiểm tra quy hoạch như thế nào cho chắc?**

            Anh/Chị nên kiểm tra từ nguồn chính thức thay vì chỉ nghe người bán nói:
            1. Tra cứu tại cơ quan quản lý đất đai/quy hoạch địa phương hoặc cổng thông tin quy hoạch nếu có.
            2. Đối chiếu số thửa, tờ bản đồ, vị trí thực tế trên sổ.
            3. Hỏi rõ có nằm trong lộ giới, đường dự phóng, hành lang bảo vệ, đất cây xanh/công cộng không.
            4. Kiểm tra có trong kế hoạch sử dụng đất hằng năm hoặc có thông báo thu hồi chưa.
            5. Ghi điều kiện hoàn cọc nếu phát hiện thông tin quy hoạch không đúng.

            Nếu mua để ở/xây nhà lâu dài, đất vướng quy hoạch là rủi ro lớn và không nên cọc khi chưa xác minh rõ.
            """;

            if (BuildPermissionQuestion(n)) return "Muốn biết đất có được xây nhà không, Anh/Chị cần kiểm tra trên sổ ghi mục đích sử dụng đất là đất ở hay loại đất khác, đối chiếu quy hoạch/lộ giới, hỏi điều kiện cấp phép xây dựng tại địa phương và kiểm tra có bị hạn chế xây dựng không. Nếu đất là đất nông nghiệp hoặc đất chưa chuyển mục đích thì không nên mặc định được xây nhà ở; cần xác minh trước khi đặt cọc.";

            if (ContainsAny(n, "the chap", "ngan hang")) return "Đất/nhà đang thế chấp vẫn có thể giao dịch trong một số trường hợp, nhưng phải giải chấp đúng quy trình. Anh/Chị cần kiểm tra ngân hàng giữ sổ, dư nợ, thời hạn giải chấp, ai chịu trách nhiệm giải chấp và chỉ công chứng/chuyển tiền lớn khi có cơ chế an toàn tại ngân hàng hoặc văn phòng công chứng.";
            if (ContainsAny(n, "giay tay")) return "Mua đất giấy tay có rủi ro cao: khó sang tên, khó chứng minh quyền sử dụng, dễ tranh chấp, khó vay ngân hàng và có thể không được xây dựng/hợp thức hóa. Không nên cọc nếu chưa được cơ quan có thẩm quyền hoặc luật sư kiểm tra điều kiện pháp lý.";
            if (ContainsAny(n, "anh chup so", "chi cho xem anh")) return "Chỉ xem ảnh chụp sổ thì chưa đủ để đặt cọc. Anh/Chị cần xem sổ gốc, đối chiếu người đứng tên, kiểm tra quy hoạch, tranh chấp, thế chấp/ngăn chặn giao dịch và ghi điều kiện hoàn cọc nếu pháp lý không đúng.";
            if (ContainsAny(n, "nha cu", "mua nha cu")) return "Mua nhà cũ ngoài kiểm tra đất/sổ như mua đất trống, còn phải kiểm tra thêm hiện trạng nhà, kết cấu, thấm dột, hệ thống điện nước, giấy phép xây dựng/hoàn công nếu có, tài sản gắn liền với đất có được ghi nhận trên sổ không và có tranh chấp sử dụng chung lối đi/tường/rào không.";
            if (ContainsAny(n, "dat nong nghiep", "xay nha duoc khong", "dat co duoc xay", "kiem tra dat co duoc xay", "co duoc xay nha")) return "Muốn biết đất có được xây nhà không, Anh/Chị cần kiểm tra: mục đích sử dụng đất trên sổ có phải đất ở không, có vướng quy hoạch/lộ giới/hành lang bảo vệ không, khu vực có được cấp phép xây dựng không, diện tích/lối đi có đủ điều kiện không. Đất nông nghiệp thường không được tự ý xây nhà nếu chưa chuyển mục đích và phù hợp quy hoạch.";
            if (ContainsAny(n, "dong so huu", "thua ke")) return "Tài sản đồng sở hữu hoặc thừa kế vẫn có thể giao dịch nếu đủ người có quyền đồng ý và hồ sơ hợp lệ. Cần kiểm tra tất cả đồng sở hữu/người thừa kế, giấy tờ hôn nhân, ủy quyền, văn bản khai nhận/phân chia di sản và điều kiện công chứng.";

            return """
            **Checklist kiểm tra sổ đỏ/sổ hồng khi mua nhà đất**

            1. Xem bản gốc, không chỉ xem ảnh chụp.
            2. Kiểm tra người đứng tên, đồng sở hữu, vợ/chồng, thừa kế hoặc ủy quyền.
            3. Kiểm tra số thửa, tờ bản đồ, diện tích, mục đích sử dụng đất, thời hạn sử dụng.
            4. Đối chiếu diện tích thực tế, ranh giới, đường vào với thông tin trên sổ.
            5. Kiểm tra quy hoạch/lộ giới, tranh chấp, thế chấp, ngăn chặn giao dịch.
            6. Nếu có nhà/công trình: kiểm tra hoàn công, giấy phép xây dựng hoặc tài sản gắn liền với đất.
            7. Trước khi cọc, ghi rõ điều kiện hoàn cọc nếu pháp lý không đúng như cam kết.

            Đây là tư vấn tham khảo, Anh/Chị nên xác minh tại văn phòng đăng ký đất đai, văn phòng công chứng hoặc cơ quan có thẩm quyền.
            """;
        }

        private static string TransactionAnswer(string n = "")
        {
            if (ContainsAny(n, "chi phi", "thue khi ban lai", "ban lai", "thue ban lai")) return """
            **Chi phí và thuế khi bán lại bất động sản cần lưu ý**

            Khi bán lại nhà đất, các khoản thường cần xem xét gồm:
            1. Thuế thu nhập cá nhân từ chuyển nhượng bất động sản theo quy định áp dụng tại thời điểm giao dịch.
            2. Lệ phí trước bạ thường do bên mua chịu nếu hai bên không thỏa thuận khác.
            3. Phí công chứng hợp đồng chuyển nhượng.
            4. Phí thẩm định/hồ sơ/sang tên nếu có.
            5. Chi phí môi giới, quảng cáo tin, đo vẽ, trích lục, sửa chữa hồ sơ nếu phát sinh.

            Ai chịu khoản nào cần ghi rõ trong đặt cọc/hợp đồng. Em không hướng dẫn né thuế; nên kê khai đúng và hỏi văn phòng công chứng/cơ quan thuế để có số tiền chính xác.
            """;

            if (ContainsAny(n, "dat coc", "hop dong dat coc", "dieu khoan", "ghi dieu khoan")) return """
            **Đặt cọc mua đất cần ghi điều khoản gì để an toàn?**

            Hợp đồng đặt cọc nên ghi rõ:
            1. Thông tin bên mua, bên bán, người được ủy quyền nếu có.
            2. Thông tin tài sản: số thửa, tờ bản đồ, diện tích, địa chỉ, loại đất, tình trạng sổ.
            3. Giá mua bán, số tiền cọc, phương thức thanh toán và các mốc thanh toán.
            4. Thời hạn công chứng/chuyển nhượng.
            5. Ai chịu thuế, phí công chứng, lệ phí trước bạ và chi phí phát sinh.
            6. Điều kiện hoàn cọc nếu phát hiện quy hoạch, tranh chấp, thế chấp, sai diện tích, sai chủ sở hữu hoặc không đủ điều kiện sang tên.
            7. Mức phạt cọc nếu một bên tự ý hủy giao dịch.
            8. Thời điểm bàn giao đất/nhà và giấy tờ.

            Không nên cọc lớn nếu chưa xem sổ gốc, chưa kiểm tra quy hoạch và chưa xác minh người bán có quyền ký.
            """;

            if (ContainsAny(n, "cong chung", "giay to cong chung", "ben mua can chuan bi", "ben ban can chuan bi")) return """
            **Công chứng mua bán nhà đất cần chuẩn bị gì?**

            Bên mua thường cần: CCCD/hộ chiếu, giấy đăng ký kết hôn hoặc xác nhận độc thân, thông tin cư trú và tiền/thỏa thuận thanh toán.

            Bên bán thường cần: CCCD/hộ chiếu, giấy đăng ký kết hôn hoặc xác nhận độc thân, sổ đỏ/sổ hồng bản gốc, giấy tờ liên quan đến tài sản, giấy ủy quyền hợp lệ nếu không trực tiếp ký.

            Trước khi công chứng nên hỏi văn phòng công chứng danh sách hồ sơ cụ thể vì từng trường hợp đồng sở hữu, thừa kế, ủy quyền, tài sản chung vợ chồng sẽ khác nhau.
            """;

            if (ContainsAny(n, "sau khi cong chung", "lam gi tiep", "nhan so", "bao lau")) return """
            **Sau khi công chứng xong thì làm gì tiếp?**

            Thông thường các bước tiếp theo gồm:
            1. Kê khai thuế thu nhập cá nhân, lệ phí trước bạ và các khoản phí liên quan.
            2. Nộp hồ sơ sang tên tại cơ quan tiếp nhận hồ sơ đất đai.
            3. Theo dõi thông báo thuế/phí và hoàn tất nghĩa vụ tài chính.
            4. Nhận giấy chứng nhận đã sang tên.
            5. Bàn giao tài sản, chìa khóa, giấy tờ và xác nhận thanh toán còn lại nếu có.

            Thời gian nhận sổ phụ thuộc địa phương, hồ sơ và tình trạng pháp lý tài sản.
            """;

            if (ContainsAny(n, "dieu khoan gi", "ghi dieu khoan", "dat coc mua dat can ghi", "dat coc can ghi", "hop dong coc can ghi")) return """
            **Đặt cọc mua đất cần ghi điều khoản gì để an toàn?**

            Hợp đồng đặt cọc nên ghi rõ:
            1. Thông tin hai bên và căn cứ người bán có quyền bán: chủ sở hữu, đồng sở hữu, ủy quyền nếu có.
            2. Thông tin tài sản: số thửa, tờ bản đồ, diện tích, địa chỉ, mục đích sử dụng đất, tài sản gắn liền với đất.
            3. Giá chuyển nhượng, số tiền cọc, phương thức thanh toán, từng mốc thanh toán.
            4. Thời hạn ký công chứng và điều kiện gia hạn nếu có.
            5. Điều kiện hoàn cọc nếu phát hiện quy hoạch, tranh chấp, thế chấp, sai diện tích, sai chủ sở hữu hoặc không đủ điều kiện sang tên.
            6. Mức phạt cọc nếu bên mua/bên bán vi phạm.
            7. Ai chịu thuế, phí công chứng, lệ phí trước bạ và chi phí phát sinh.
            8. Cam kết bàn giao giấy tờ gốc, bàn giao đất/nhà, tài sản kèm theo.

            Không nên đặt cọc lớn khi chưa xem sổ gốc và chưa kiểm tra quy hoạch.
            """;

            return """
            **Quy trình giao dịch nhà đất an toàn**

            1. Xác định nhu cầu và chọn tin phù hợp.
            2. Xem thực tế tài sản, đối chiếu vị trí/diện tích/đường vào.
            3. Kiểm tra sổ gốc, chủ sở hữu, quy hoạch, tranh chấp, thế chấp.
            4. Thương lượng giá, thuế phí, bàn giao và mốc thanh toán.
            5. Đặt cọc bằng văn bản rõ điều kiện hoàn cọc/phạt cọc.
            6. Công chứng hợp đồng chuyển nhượng.
            7. Kê khai thuế, lệ phí và nộp hồ sơ sang tên.
            8. Nhận sổ đã sang tên và bàn giao tài sản.

            Không nên chuyển hết tiền trước khi có cơ chế bảo vệ rõ ràng.
            """;
        }

        private static string LoanAnswer(Dictionary<string, string> slots, string n)
        {
            LoanInput input = ExtractLoanInput(n, slots);
            string intro = "Em tính theo hướng **tham khảo dòng tiền**, không thay ngân hàng thẩm định hồ sơ.";
            string disclaimer = "Kết quả chỉ để Anh/Chị ước lượng ban đầu. Ngân hàng còn xét pháp lý tài sản, giá trị thẩm định, lịch sử tín dụng, nợ hiện có, người phụ thuộc, nghề nghiệp, độ ổn định thu nhập và chính sách từng thời điểm.";

            if (ContainsAny(n, "lai suat tha noi", "lãi suất thả nổi"))
            {
                return """
                **Lãi suất thả nổi có rủi ro gì?**

                Lãi suất thả nổi có thể tăng sau thời gian ưu đãi, làm tiền trả hằng tháng cao hơn dự kiến. Khi vay, Anh/Chị nên hỏi ngân hàng thật rõ:

                1. Lãi suất ưu đãi trong bao lâu.
                2. Sau ưu đãi tính bằng công thức nào: lãi tham chiếu + biên độ bao nhiêu.
                3. Chu kỳ điều chỉnh lãi suất: 3 tháng, 6 tháng hay 12 tháng.
                4. Phí trả nợ trước hạn.
                5. Nếu lãi tăng 2-4%/năm thì tiền trả tháng còn chịu được không.

                Không nên chỉ nhìn mức lãi ưu đãi ban đầu. Nên tính kịch bản xấu trước khi ký hợp đồng vay.
                """;
            }

            if (ContainsAny(n, "mua dat co so do", "co so do thi ngan hang", "ngan hang co cho vay", "co so do ngan hang co cho vay"))
            {
                return """
                **Mua đất có sổ đỏ/sổ hồng thì ngân hàng có cho vay không?**

                Có sổ là điều kiện thuận lợi, nhưng **không có nghĩa chắc chắn vay được**. Ngân hàng thường kiểm tra:

                1. Sổ thật, đúng chủ sở hữu, không tranh chấp, không bị ngăn chặn giao dịch.
                2. Mục đích sử dụng đất, quy hoạch, lộ giới và khả năng sang tên.
                3. Tài sản có dễ định giá, dễ thanh khoản không.
                4. Giá trị thẩm định của ngân hàng, thường có thể thấp hơn giá mua.
                5. Thu nhập, lịch sử tín dụng và nợ hiện có của người vay.

                Trước khi đặt cọc lớn, Anh/Chị nên hỏi ngân hàng thẩm định sơ bộ tài sản và hồ sơ vay trước.
                """;
            }

            if (ContainsAny(n, "dat chua co tho cu", "chua co tho cu", "dat nong nghiep ngan hang"))
            {
                return """
                **Đất chưa có thổ cư thì ngân hàng có nhận thế chấp không?**

                Có thể khó vay hơn hoặc tỷ lệ vay thấp hơn, tùy loại đất, quy hoạch, pháp lý, vị trí và chính sách từng ngân hàng. Một số ngân hàng không thích tài sản khó thanh khoản hoặc chưa có mục đích đất ở.

                Anh/Chị nên kiểm tra:
                1. Trên sổ ghi loại đất gì.
                2. Có chuyển mục đích lên đất ở được không.
                3. Có vướng quy hoạch/lộ giới không.
                4. Ngân hàng định giá được bao nhiêu.
                5. Tỷ lệ cho vay trên giá trị thẩm định là bao nhiêu.

                Không nên đặt cọc lớn trước khi ngân hàng trả lời sơ bộ.
                """;
            }

            if (ContainsAny(n, "vay 50", "vay 70", "50%", "70%", "nen vay 50", "nen vay 70"))
            {
                return """
                **Nên vay 50% hay 70% giá trị nhà đất?**

                Nếu mới mua lần đầu hoặc thu nhập chưa thật dư dả, **vay khoảng 50% thường an toàn hơn 70%**.

                - Vay 50%: áp lực trả nợ thấp hơn, chịu được biến động lãi suất tốt hơn, ít rủi ro phải bán gấp.
                - Vay 70%: cần dòng tiền mạnh và ổn định, dễ áp lực khi lãi suất tăng hoặc có chi phí phát sinh.
                - Dù vay mức nào, nên giữ quỹ dự phòng 3-6 tháng chi phí sinh hoạt và tiền trả nợ.

                Quy tắc an toàn: tiền trả nợ hằng tháng nên nằm trong mức gia đình chịu được sau khi trừ chi phí sinh hoạt, không nên tính quá sát.
                """;
            }

            if (input.LoanAmount.HasValue || input.PropertyPrice.HasValue || input.MonthlyIncome.HasValue || ContainsAny(n, "ap luc", "vay toi da", "vay duoc bao nhieu", "vay 1 ty", "trong 20 nam", "20 nam", "15 nam", "25 nam"))
            {
                return BuildBankCalculationAnswer(input, n);
            }

            return $"""
            **Bộ tính vay ngân hàng tham khảo**

            {intro}

            Để tính sát hơn, Anh/Chị gửi theo mẫu:
            - Giá nhà/đất muốn mua.
            - Vốn tự có.
            - Thu nhập ổn định mỗi tháng.
            - Chi phí cố định mỗi tháng nếu có.
            - Số tiền muốn vay hoặc tỷ lệ vay.
            - Thời hạn vay mong muốn.

            Ví dụ: “Tôi có 500 triệu, mua nhà 1,5 tỷ, thu nhập 25 triệu/tháng, muốn vay 20 năm”.

            {disclaimer}
            """;
        }


        private sealed class LoanInput
        {
            public decimal? PropertyPrice { get; set; }
            public decimal? CashAvailable { get; set; }
            public decimal? LoanAmount { get; set; }
            public decimal? MonthlyIncome { get; set; }
            public decimal? FixedMonthlyExpense { get; set; }
            public int Years { get; set; } = 20;
            public decimal AnnualRate { get; set; } = 10m;
        }

        private sealed class LoanScenarioResult
        {
            public decimal AnnualRate { get; set; }
            public decimal EqualPayment { get; set; }
            public decimal FirstDecreasingPayment { get; set; }
            public decimal SafeIncomeMin { get; set; }
        }

        private static LoanInput ExtractLoanInput(string n, Dictionary<string, string> slots)
        {
            LoanInput input = new();

            input.MonthlyIncome = FirstMoneyAfter(n, "thu nhap", "luong", "lương") ?? DecimalSlot(slots, "monthly_income");
            input.CashAvailable = FirstMoneyAfter(n, "von tu co", "von", "co san", "co ", "tien mat", "tiet kiem") ?? DecimalSlot(slots, "cash_available");
            input.PropertyPrice = ExtractPropertyPrice(n) ?? DecimalSlot(slots, "budget_max");
            input.LoanAmount = ExtractExplicitLoanAmount(n);

            Match years = Regex.Match(n, @"(?<y>\d{1,2})\s*(?:nam|năm)", RegexOptions.IgnoreCase);
            if (years.Success && int.TryParse(years.Groups["y"].Value, out int y) && y >= 1 && y <= 35)
                input.Years = y;

            decimal? rate = ExtractInterestRate(n);
            if (rate.HasValue && rate.Value > 0 && rate.Value < 40) input.AnnualRate = rate.Value;

            if (!input.LoanAmount.HasValue && input.PropertyPrice.HasValue && input.CashAvailable.HasValue && input.PropertyPrice.Value > input.CashAvailable.Value)
                input.LoanAmount = input.PropertyPrice.Value - input.CashAvailable.Value;

            return input;
        }

        private static decimal? ExtractPropertyPrice(string n)
        {
            string normalized = Normalize(n);
            string[] anchors = { "mua nha", "mua dat", "mua can ho", "nha", "dat", "can ho", "bds", "bat dong san", "gia" };
            foreach (string anchor in anchors)
            {
                int idx = normalized.IndexOf(Normalize(anchor), StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                string part = normalized[idx..];
                List<decimal> values = MoneyValues(part);
                if (values.Any()) return values.First();
            }

            List<decimal> all = MoneyValues(normalized);
            if (all.Count >= 2) return all.Max();
            return null;
        }

        private static decimal? ExtractExplicitLoanAmount(string n)
        {
            string normalized = Normalize(n);
            foreach (string anchor in new[] { "muon vay", "can vay", "vay" })
            {
                int idx = normalized.IndexOf(anchor, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                List<decimal> values = MoneyValues(normalized[idx..]);
                if (values.Any()) return values.First();
            }
            return null;
        }

        private static decimal? ExtractInterestRate(string n)
        {
            Match m = Regex.Match(n, @"(?<r>\d+(?:[\.,]\d+)?)\s*%\s*(?:/nam|nam|moi nam)?", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            if (decimal.TryParse(m.Groups["r"].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal r))
                return r;
            return null;
        }

        private static string BuildBankCalculationAnswer(LoanInput input, string n)
        {
            decimal? loan = input.LoanAmount;
            if (!loan.HasValue && input.MonthlyIncome.HasValue && ContainsAny(n, "vay toi da", "mua nha toi da", "toi da bao nhieu", "vay duoc bao nhieu", "nen vay bao nhieu", "nen vay mua nha toi da", "thu nhap"))
            {
                decimal safeMonthly = input.MonthlyIncome.Value * 0.40m;
                decimal estimatedLoan = EstimatePrincipalFromPayment(safeMonthly, 10m, input.Years);
                return $"""
                **Ước tính khoản vay tối đa theo thu nhập**

                Em tạm lấy ngưỡng an toàn là khoảng **35–45% thu nhập ổn định** để trả nợ.

                - Thu nhập: **{Price(input.MonthlyIncome.Value)}/tháng**
                - Khoản trả nợ tham khảo nên quanh: **{Price(input.MonthlyIncome.Value * 0.35m)} – {Price(input.MonthlyIncome.Value * 0.45m)}/tháng**
                - Nếu vay khoảng **{input.Years} năm**, lãi suất giả định **10%/năm**, khoản vay tương ứng tham khảo khoảng **{Price(estimatedLoan)}**

                Đây không phải hạn mức ngân hàng cam kết. Hạn mức thật còn phụ thuộc chi phí sinh hoạt, nợ hiện có, người phụ thuộc, lịch sử tín dụng và pháp lý tài sản.
                """;
            }

            if (!loan.HasValue)
            {
                return """
                Em chưa đủ dữ liệu để tính số tiền trả ngân hàng. Anh/Chị cho em thêm ít nhất 3 thông tin:
                1. Giá nhà/đất muốn mua.
                2. Vốn tự có.
                3. Thu nhập ổn định mỗi tháng.
                4. Thời hạn vay mong muốn nếu có.

                Ví dụ: “Tôi có 500 triệu, mua nhà 1,5 tỷ, thu nhập 25 triệu/tháng, vay 20 năm”.
                """;
            }

            List<LoanScenarioResult> scenarios = new();
            foreach (decimal r in new[] { 8m, 10m, 12m })
            {
                scenarios.Add(new LoanScenarioResult
                {
                    AnnualRate = r,
                    EqualPayment = EqualMonthlyPayment(loan.Value, r, input.Years),
                    FirstDecreasingPayment = FirstDecreasingPayment(loan.Value, r, input.Years),
                    SafeIncomeMin = EqualMonthlyPayment(loan.Value, r, input.Years) / 0.40m
                });
            }

            string cashLine = input.CashAvailable.HasValue ? $"\n- Vốn tự có: **{Price(input.CashAvailable.Value)}**" : "";
            string propertyLine = input.PropertyPrice.HasValue ? $"\n- Giá tài sản dự kiến: **{Price(input.PropertyPrice.Value)}**" : "";
            string incomeLine = input.MonthlyIncome.HasValue ? $"\n- Thu nhập ổn định: **{Price(input.MonthlyIncome.Value)}/tháng**" : "";
            string verdict = BuildLoanVerdict(input, scenarios);

            string scenarioLines = string.Join("\n", scenarios.Select(x =>
                $"- Lãi {x.AnnualRate:0.#}%/năm: trả góp đều khoảng **{Price(x.EqualPayment)}/tháng**; dư nợ giảm dần tháng đầu khoảng **{Price(x.FirstDecreasingPayment)}**; thu nhập an toàn nên từ **{Price(x.SafeIncomeMin)}/tháng** trở lên."));

            return $"""
            **Bộ tính vay ngân hàng tham khảo**

            Em tính chậm theo 3 bước: xác định số tiền vay → ước tính tiền trả tháng → so với thu nhập an toàn.

            **Dữ liệu em hiểu**
            {propertyLine}{cashLine}
            - Số tiền vay ước tính: **{Price(loan.Value)}**
            - Thời hạn vay: **{input.Years} năm**{incomeLine}

            **Kịch bản trả nợ tham khảo**
            {scenarioLines}

            **Kết luận sơ bộ**
            {verdict}

            **Cần hỏi ngân hàng trước khi đặt cọc lớn**
            1. Ngân hàng định giá tài sản bao nhiêu, có thấp hơn giá mua không.
            2. Tỷ lệ cho vay tối đa trên giá trị thẩm định.
            3. Lãi suất ưu đãi, lãi sau ưu đãi, biên độ và chu kỳ thả nổi.
            4. Phí trả nợ trước hạn, phí bảo hiểm nếu có.
            5. Hồ sơ cần chuẩn bị và khả năng duyệt sơ bộ.

            Đây là tính toán tham khảo, không phải cam kết duyệt vay.
            """;
        }

        private static string BuildLoanVerdict(LoanInput input, List<LoanScenarioResult> scenarios)
        {
            if (!input.MonthlyIncome.HasValue)
                return "Chưa có thu nhập tháng nên em chưa kết luận áp lực nặng hay nhẹ. Anh/Chị nên bổ sung thu nhập và chi phí cố định để đánh giá sát hơn.";

            decimal income = input.MonthlyIncome.Value;
            decimal low = scenarios.First(x => x.AnnualRate == 10m).EqualPayment;
            decimal ratio = low / Math.Max(income, 1);

            if (ratio <= 0.35m)
                return $"Khoản vay này **khá an toàn hơn** nếu thu nhập {Price(income)}/tháng ổn định và không có nhiều nợ khác, vì tiền trả ước tính chiếm khoảng {(ratio * 100):0.#}% thu nhập.";
            if (ratio <= 0.45m)
                return $"Khoản vay này **ở mức cần cân nhắc kỹ**, vì tiền trả ước tính chiếm khoảng {(ratio * 100):0.#}% thu nhập. Nên giữ quỹ dự phòng 3–6 tháng và thử kịch bản lãi tăng.";
            if (ratio <= 0.60m)
                return $"Khoản vay này **áp lực cao**, vì tiền trả ước tính chiếm khoảng {(ratio * 100):0.#}% thu nhập. Nên tăng vốn tự có, giảm giá trị tài sản hoặc kéo dài thời hạn vay nếu phù hợp.";
            return $"Khoản vay này **rất áp lực** so với thu nhập hiện tại, vì tiền trả ước tính chiếm khoảng {(ratio * 100):0.#}% thu nhập. Không nên quyết định nếu chưa có thêm nguồn thu hoặc vốn tự có.";
        }

        private static decimal EqualMonthlyPayment(decimal principal, decimal annualRatePercent, int years)
        {
            int months = Math.Max(1, years * 12);
            decimal monthlyRate = annualRatePercent / 100m / 12m;
            if (monthlyRate <= 0) return principal / months;
            double r = (double)monthlyRate;
            double p = (double)principal;
            double pow = Math.Pow(1 + r, months);
            return (decimal)(p * r * pow / (pow - 1));
        }

        private static decimal FirstDecreasingPayment(decimal principal, decimal annualRatePercent, int years)
        {
            int months = Math.Max(1, years * 12);
            decimal principalPerMonth = principal / months;
            decimal firstInterest = principal * (annualRatePercent / 100m / 12m);
            return principalPerMonth + firstInterest;
        }

        private static decimal EstimatePrincipalFromPayment(decimal monthlyPayment, decimal annualRatePercent, int years)
        {
            int months = Math.Max(1, years * 12);
            decimal monthlyRate = annualRatePercent / 100m / 12m;
            if (monthlyRate <= 0) return monthlyPayment * months;
            double r = (double)monthlyRate;
            double pay = (double)monthlyPayment;
            double pv = pay * (1 - Math.Pow(1 + r, -months)) / r;
            return (decimal)pv;
        }

        private static string NormalizeDisplayMoney(string value, string fallback)
        {
            string v = value.Trim();
            if (ContainsAny(v, "ty", "tỷ", "ti", "tỉ", "trieu", "triệu", "tr")) return v;
            if (fallback.Contains("tỷ", StringComparison.OrdinalIgnoreCase) || fallback.Contains("ty", StringComparison.OrdinalIgnoreCase)) return v + " tỷ";
            if (fallback.Contains("triệu", StringComparison.OrdinalIgnoreCase) || fallback.Contains("tr", StringComparison.OrdinalIgnoreCase)) return v + " triệu";
            return v;
        }

        private static string PostingAnswer(string n)
        {
            if (ContainsAny(n, "bao loi", "bao lai", "chac chan tang gia", "cam ket loi nhuan", "co nen ghi"))
                return """
                **Có nên ghi “bao lời, chắc chắn tăng giá” trong tin đăng không?**

                Không nên ghi như vậy nếu không có cơ sở pháp lý/tài liệu chứng minh rõ ràng. Những câu kiểu “bao lời”, “chắc chắn tăng giá”, “đầu tư là thắng” dễ làm tin thiếu trung thực, bị người mua phản ứng, bị báo cáo vi phạm hoặc bị admin từ chối.

                Nên viết an toàn hơn:
                - “Phù hợp tham khảo đầu tư dài hạn” nếu đúng thực tế.
                - “Khu vực có tiềm năng nhờ hạ tầng/tiện ích xung quanh” nhưng không cam kết lợi nhuận.
                - “Giá còn thương lượng, pháp lý/diện tích/vị trí như mô tả”.

                Nguyên tắc: mô tả đúng sự thật, không cam kết lợi nhuận, không che giấu rủi ro pháp lý/quy hoạch.
                """;

            if (ContainsAny(n, "ban nhanh", "khong muon bi ep gia", "bi ep gia", "trinh bay tin", "de co khach", "de co khach hang"))
                return """
                **Muốn bán nhanh nhưng không bị ép giá, nên trình bày tin như thế nào?**

                Anh/Chị nên làm tin đăng rõ, đủ dữ liệu và có cơ sở giá để khách khó ép vô lý:

                1. Tiêu đề ghi đúng loại BĐS, khu vực, diện tích, pháp lý và điểm mạnh chính.
                2. Mô tả rõ: diện tích, đường vào, hướng, tiện ích, pháp lý, hiện trạng, lý do bán nếu phù hợp.
                3. Ảnh thật: mặt tiền/lối vào, đường trước nhà/đất, tổng thể, giấy tờ che thông tin nhạy cảm nếu cần.
                4. Giá nên có căn cứ: so với tin cùng khu, cùng diện tích, cùng pháp lý.
                5. Ghi “có thương lượng cho khách thiện chí” thay vì giảm giá sâu ngay từ đầu.
                6. Chuẩn bị sẵn câu trả lời về sổ, quy hoạch, đường vào, thuế phí và mốc công chứng.

                Không nên phóng đại như “bao lời”, “chắc chắn tăng giá”, vì dễ mất uy tín và bị báo cáo.
                """;

            if (ContainsAny(n, "3 tieu de", "ba tieu de", "viet tieu de")) return """
            Em gợi ý 3 tiêu đề trung thực, chuyên nghiệp:
            1. Bán đất Diên Khánh 100m², giá 1,2 tỷ, pháp lý rõ.
            2. Cần bán lô đất 100m² tại Diên Khánh, đường thuận tiện, giá thương lượng.
            3. Bán đất phù hợp xây nhà tại Diên Khánh, diện tích 100m², sổ riêng nếu đúng thực tế.

            Anh/Chị nên bổ sung đúng khu vực, đường vào, pháp lý, tiện ích và ảnh thật để tin đáng tin hơn.
            """;

            if (ContainsAny(n, "so rieng", "duong o to", "gan cho", "dang tin the nao", "de co khach"))
                return """
                **Cách đăng tin bán đất có sổ riêng, đường ô tô, gần chợ**

                Tiêu đề nên nêu đúng điểm mạnh:
                “Bán đất sổ riêng, đường ô tô, gần chợ, phù hợp xây nhà ở lâu dài”

                Nội dung nên có:
                1. Diện tích, ngang/dài nếu có.
                2. Vị trí cụ thể đến mức được phép công khai.
                3. Đường trước đất: ô tô/xe máy, bê tông/nhựa, rộng khoảng bao nhiêu.
                4. Pháp lý: sổ riêng/sổ hồng/sổ đỏ, loại đất nếu biết.
                5. Tiện ích: gần chợ, trường, khu dân cư, trung tâm.
                6. Giá bán, có thương lượng hay không.
                7. Ảnh thật và thông tin liên hệ rõ.

                Không nên ghi quá mức như “bao lời”, “chắc chắn tăng giá”. Nên để khách tin vì thông tin đầy đủ và trung thực.
                """;

            return "Tin đăng tốt cần tiêu đề rõ, mô tả trung thực, ảnh thật, giá minh bạch, pháp lý ghi đúng thực tế. Không nên ghi 'bao lời', 'chắc chắn tăng giá', 'pháp lý 100%' nếu chưa có cơ sở. Nếu tin bị từ chối, thường do thiếu thông tin, sai loại hình/khu vực, ảnh kém, trùng tin hoặc nội dung phóng đại.";
        }
        private static string ProjectAnswer(string n)
        {
            if (ContainsAny(n, "hinh thanh trong tuong lai", "nha o hinh thanh", "bao lanh ngan hang"))
            {
                return """
                **Mua bất động sản/dự án hình thành trong tương lai cần kiểm tra gì?**

                Anh/Chị nên kiểm tra chủ đầu tư, quyền sử dụng đất của dự án, quy hoạch, giấy phép xây dựng nếu thuộc trường hợp phải có, điều kiện mở bán, bảo lãnh ngân hàng nếu áp dụng, hợp đồng mẫu, tiến độ thanh toán, thời hạn bàn giao, điều khoản phạt chậm bàn giao và điều kiện cấp sổ.

                Không nên chỉ nghe tư vấn miệng hoặc tin vào cam kết lợi nhuận. Mọi cam kết quan trọng phải thể hiện bằng văn bản trong hợp đồng/phụ lục.
                """;
            }

            if (ContainsAny(n, "cam ket loi nhuan", "loi nhuan cam ket", "co nen tin"))
            {
                return """
                **Dự án có cam kết lợi nhuận thì có nên tin không?**

                Không nên tin tuyệt đối. Anh/Chị cần đọc kỹ:
                1. Ai là bên cam kết và năng lực tài chính của họ.
                2. Điều kiện để được nhận lợi nhuận.
                3. Thời hạn cam kết, cách chi trả, trường hợp được từ chối chi trả.
                4. Cam kết nằm trong hợp đồng chính hay chỉ trong tài liệu quảng cáo.
                5. Nếu dự án chậm tiến độ hoặc khai thác không đạt thì xử lý ra sao.

                Nếu cam kết quá cao so với mặt bằng thị trường, cần xem đó là tín hiệu rủi ro.
                """;
            }

            if (ContainsAny(n, "cham tien do", "cham ban giao", "tien do"))
            {
                return """
                **Dự án chậm tiến độ cần chú ý điều khoản nào?**

                Anh/Chị nên đọc kỹ thời hạn bàn giao, điều kiện gia hạn, mức phạt chậm bàn giao, quyền chấm dứt hợp đồng, hoàn tiền/lãi phạt, tiến độ thanh toán có gắn với tiến độ xây dựng không và trách nhiệm của chủ đầu tư nếu không cấp sổ đúng hẹn.
                """;
            }

            return """
            **Checklist mua bất động sản trong dự án**

            1. Kiểm tra chủ đầu tư: pháp nhân, năng lực, dự án đã làm, phản hồi thị trường.
            2. Kiểm tra pháp lý: quyền sử dụng đất, quy hoạch, giấy phép, điều kiện mở bán/chuyển nhượng.
            3. Kiểm tra hợp đồng: tiến độ thanh toán, bàn giao, phạt chậm, phí quản lý, quỹ bảo trì, cấp sổ.
            4. Kiểm tra tài chính: giá, chiết khấu, vay ngân hàng, lãi suất, phí phát sinh.
            5. Kiểm tra rủi ro: cam kết lợi nhuận, booking giữ chỗ, chậm tiến độ, thanh khoản sau này.

            Đây là tư vấn ban đầu, không thay thế luật sư/công chứng/cơ quan quản lý nhà ở.
            """;
        }
        private static string CareAnswer(string n, Dictionary<string, string> slots)
        {
            if (WebsiteGuide(n)) return WebsiteGuideAnswer(n);

            if (Complaint(n))
                return "Nếu muốn báo cáo tin vi phạm, Anh/Chị bấm nút **Báo cáo vi phạm** trên trang chi tiết tin, chọn lý do, mô tả dấu hiệu sai phạm và gửi kèm bằng chứng nếu có. Nếu đã chuyển tiền, hãy lưu lại tin nhắn, biên lai, số tài khoản/số điện thoại và cân nhắc liên hệ cơ quan chức năng.";

            if (Appointment(n))
                return "Để đặt lịch xem, Anh/Chị mở trang chi tiết tin, bấm **Đặt lịch xem/Yêu cầu tư vấn**, nhập họ tên, số điện thoại, thời gian muốn xem và ghi chú. Trước khi đi xem nên chuẩn bị câu hỏi về pháp lý, đường vào, hiện trạng và giá.";

            return "Anh/Chị vui lòng mô tả vấn đề, kèm mã tin/mã giao dịch/tài khoản và ảnh chụp màn hình nếu có để bộ phận hỗ trợ kiểm tra chính xác.";
        }

        private static bool WebsiteGuide(string n)
        {
            return ContainsAny(n,
                "cach su dung web", "huong dan su dung", "web nay dung sao", "su dung website", "chuc nang web", "co nhung chuc nang gi",
                "xem tin le", "xem tin dang le", "xem chi tiet tin", "tin dang le", "tin du an", "xem du an", "tim kiem tin", "bo loc tin",
                "luu tin", "yeu thich", "dat lich xem", "yeu cau tu van", "binh luan", "bao cao vi pham", "dang tin", "quan ly tin",
                "goi dang tin", "thanh toan", "thong bao", "tai khoan", "dang nhap", "dang ky", "quen mat khau", "cach dung");
        }

        private static string WebsiteGuideAnswer(string n)
        {
            if (ContainsAny(n, "xem tin le", "xem chi tiet tin", "tin dang le", "tin le"))
            {
                return """
                **Cách sử dụng trang tin đăng lẻ**

                1. Vào danh sách mua bán/cho thuê và dùng bộ lọc khu vực, loại BĐS, khoảng giá, diện tích.
                2. Bấm vào một tin để xem chi tiết: giá, diện tích, vị trí, pháp lý, hình ảnh, mô tả và thông tin liên hệ.
                3. Nếu quan tâm, Anh/Chị có thể bấm **Yêu cầu tư vấn** hoặc **Đặt lịch xem**.
                4. Có thể lưu tin để xem lại nếu đã đăng nhập.
                5. Nếu thấy tin sai giá, sai vị trí, trùng lặp hoặc nghi lừa đảo, dùng **Báo cáo vi phạm** để gửi cho nhân viên kiểm duyệt.

                Khi xem tin lẻ, chatbot có thể phân tích: giá, pháp lý, vị trí, rủi ro, câu hỏi nên hỏi người bán và có nên đi xem thực tế không.
                """;
            }

            if (ContainsAny(n, "du an", "tin du an", "xem du an"))
            {
                return """
                **Cách sử dụng phần dự án**

                1. Vào mục **Dự án** để xem danh sách dự án đang được đăng trên hệ thống.
                2. Mở chi tiết dự án để xem tên dự án, chủ đầu tư, khu vực, pháp lý, trạng thái và mô tả.
                3. Nếu quan tâm, Anh/Chị có thể gửi yêu cầu tư vấn hoặc đặt lịch xem dự án nếu hệ thống có hỗ trợ.
                4. Chatbot có thể giúp phân tích: pháp lý dự án, chủ đầu tư, rủi ro booking, tiến độ, khả năng đầu tư và các câu hỏi cần hỏi nhân viên tư vấn.
                5. Với dự án, không nên xuống tiền chỉ dựa trên quảng cáo; cần kiểm tra hồ sơ pháp lý và hợp đồng.
                """;
            }

            if (ContainsAny(n, "dang tin", "quan ly tin", "goi dang tin", "thanh toan"))
            {
                return """
                **Cách đăng và quản lý tin**

                1. Đăng nhập tài khoản.
                2. Vào chức năng **Đăng tin** và nhập đúng loại BĐS, tiêu đề, giá, diện tích, vị trí, pháp lý, mô tả và hình ảnh.
                3. Nội dung nên trung thực, không ghi quá đà như “bao lời”, “chắc chắn tăng giá”.
                4. Sau khi gửi, tin có thể chờ duyệt. Nhân viên/Admin sẽ kiểm tra nội dung, ảnh, pháp lý mô tả và trùng lặp.
                5. Vào **Quản lý tin** để sửa, cập nhật, xem trạng thái duyệt hoặc xử lý tin bị từ chối.
                6. Nếu dùng gói đăng tin/thanh toán, cần kiểm tra lịch sử giao dịch và thông báo hệ thống.
                """;
            }

            return """
            **Các chức năng chính của website BĐS Khánh Hòa**

            1. **Tìm kiếm/lọc tin:** lọc theo khu vực, loại bất động sản, giá, diện tích và nhu cầu mua/thuê.
            2. **Xem chi tiết tin lẻ:** xem giá, diện tích, vị trí, pháp lý, hình ảnh, mô tả và thông tin liên hệ.
            3. **Xem dự án:** xem thông tin dự án, chủ đầu tư, khu vực, pháp lý, trạng thái và gửi yêu cầu tư vấn.
            4. **Yêu cầu tư vấn/đặt lịch xem:** gửi thông tin liên hệ và thời gian muốn xem để người bán/chủ đầu tư xử lý.
            5. **Lưu tin/bình luận/báo cáo vi phạm:** hỗ trợ theo dõi tin quan tâm và phản ánh tin sai phạm.
            6. **Đăng tin/quản lý tin:** dành cho người bán đăng bán/cho thuê và theo dõi trạng thái duyệt.
            7. **Thông báo:** nhận thông báo về lịch hẹn, tư vấn, duyệt tin, báo cáo vi phạm và các xử lý liên quan.

            Anh/Chị đang muốn em hướng dẫn phần nào: tìm tin, xem tin lẻ, xem dự án, đăng tin, đặt lịch hay báo cáo vi phạm?
            """;
        }
        private static string MarketAnswer(string n = "")
        {
            if (ContainsAny(n, "ngan sach gioi han", "giam yeu cau dien tich hay vi tri", "giam dien tich hay vi tri", "nen giam dien tich hay vi tri"))
                return """
                **Ngân sách giới hạn thì nên giảm diện tích hay vị trí?**

                Không có đáp án cố định cho mọi người. Cách chọn an toàn hơn là xem mục đích chính:

                1. Nếu mua **để ở lâu dài**: thường nên ưu tiên vị trí đủ thuận tiện, pháp lý rõ, hạ tầng ổn; có thể giảm diện tích vừa phải.
                2. Nếu mua **để đầu tư/giữ tài sản**: xem thanh khoản, quy hoạch, dân cư thật và khả năng tăng nhu cầu; không nên chỉ chọn diện tích lớn nhưng quá xa/kém thanh khoản.
                3. Nếu mua **để xây nhà**: phải kiểm tra loại đất, quy hoạch, đường vào, điện nước, chi phí xây dựng sau mua.
                4. Nếu ngân sách quá sát: nên giữ quỹ dự phòng, không dồn hết tiền vào đất rồi thiếu tiền xây/sửa.

                Kết luận sơ bộ: với người mua lần đầu, em thường ưu tiên **pháp lý rõ + vị trí sống được + chi phí sau mua kiểm soát được**, rồi mới tối ưu diện tích.
                """;

            if (ContainsAny(n, "dat mat tien gia cao", "mat tien gia cao", "dat mat tien", "co dang mua", "dang mua khong"))
                return """
                **Đất mặt tiền giá cao hơn nhiều có đáng mua không?**

                Đáng mua khi mặt tiền tạo ra giá trị thật: dễ kinh doanh, dễ cho thuê, dễ bán lại, đường lớn hợp pháp, khu dân cư có nhu cầu và pháp lý rõ. Nhưng không đáng mua nếu giá cao chỉ vì “mặt tiền” mà khu vực ít nhu cầu, đường quy hoạch chưa chắc, pháp lý chưa rõ hoặc dòng tiền không bù được phần chênh.

                Anh/Chị nên kiểm tra:
                1. Mặt tiền là đường hiện hữu hợp pháp hay đường dự kiến/quy hoạch.
                2. Giá chênh bao nhiêu so với lô trong hẻm cùng khu.
                3. Có thể khai thác kinh doanh/cho thuê thật không.
                4. Có vướng lộ giới, hành lang, quy hoạch mở đường không.
                5. Nếu sau này bán lại, nhóm khách mua có đủ rộng không.

                Kết luận: mua mặt tiền chỉ hợp lý khi **pháp lý rõ + vị trí có nhu cầu thật + giá chênh có lý do**.
                """;

            if (ContainsAny(n, "tin nay gia re", "gia re co so duong nho", "gia re", "duong nho", "hem nho", "co so"))
                return """
                **Tin giá rẻ, có sổ nhưng đường nhỏ có nên mua không?**

                Em chưa có đủ dữ liệu để kết luận “nên mua”. Trường hợp này cần kiểm tra kỹ vì giá rẻ thường đi kèm một lý do nào đó.

                Nên hỏi và kiểm tra:
                1. Đường vào có hợp pháp không, có tranh chấp lối đi không.
                2. Đường nhỏ có ảnh hưởng xây dựng, vận chuyển vật liệu, PCCC, sinh hoạt và bán lại không.
                3. Sổ ghi đúng diện tích, mục đích sử dụng đất và chủ sở hữu không.
                4. Có vướng quy hoạch/lộ giới/hành lang bảo vệ không.
                5. Lý do bán rẻ là gì, giá có thấp bất thường so với các lô xung quanh không.

                Kết luận sơ bộ: **có thể xem xét**, nhưng không nên cọc ngay. Chỉ đi tiếp nếu đường vào hợp pháp, pháp lý sạch, giá thấp có lý do hợp lý và phù hợp mục đích sử dụng.
                """;

            if (ContainsAny(n, "dat mat tien hay nha xay san", "vua o vua kinh doanh", "kinh doanh nho", "chon dat mat tien", "nha xay san"))
                return """
                **Vừa ở vừa kinh doanh nhỏ nên chọn đất mặt tiền hay nhà xây sẵn?**

                Nếu cần kinh doanh sớm, **nhà xây sẵn/nhà mặt tiền** thường thực tế hơn vì có thể khai thác ngay, dễ tính dòng tiền và chi phí ban đầu rõ hơn. Nếu muốn tự thiết kế lâu dài, **đất mặt tiền** linh hoạt hơn nhưng phải kiểm tra được xây dựng, chi phí xây, thời gian xin phép và rủi ro phát sinh.

                Nên ưu tiên:
                1. Pháp lý rõ, được phép ở và kinh doanh phù hợp.
                2. Mặt tiền/đường vào đủ thuận tiện, có nhu cầu dân cư thật.
                3. Chỗ đậu xe, tiếng ồn, PCCC/giấy phép nếu kinh doanh ngành có điều kiện.
                4. Tổng chi phí sau mua: sửa chữa, xây dựng, nội thất, thuế phí.
                5. Thanh khoản sau này nếu cần bán lại.

                Với người mua lần đầu, em nghiêng về phương án **pháp lý rõ + vị trí có nhu cầu thật + chi phí sau mua kiểm soát được**, không nên chỉ chọn vì rẻ.
                """;

            if (ContainsAny(n, "900 trieu", "900tr", "co kha thi", "kha thi khong"))
                return "Với ngân sách khoảng 900 triệu để mua đất ở Khánh Hòa và sau này xây nhà, vẫn có thể khả thi ở một số khu vực xa trung tâm hơn, nhưng cần rất thực tế: Nha Trang thường khó hơn, còn Diên Khánh, Ninh Hòa, Cam Lâm, Vạn Ninh hoặc khu vực xa trung tâm có thể dễ tìm hơn tùy dữ liệu. Nên ưu tiên sổ riêng, đất ở hoặc có khả năng xây dựng, đường vào rõ và không vướng quy hoạch. Nếu muốn em lọc tin, Anh/Chị nên chọn thêm khu vực cụ thể để tránh tìm quá rộng toàn tỉnh.";

            if (ContainsAny(n, "gia re", "so dinh quy hoach", "so vuong quy hoach", "hoi nguoi ban gi"))
                return "Khi gặp đất giá rẻ nhưng sợ dính quy hoạch, Anh/Chị nên hỏi người bán: đất có sổ gốc không, số thửa/tờ bản đồ là gì, mục đích sử dụng đất, có nằm quy hoạch/lộ giới/kế hoạch thu hồi không, có tranh chấp/thế chấp không, lý do bán rẻ, đường vào có hợp pháp không và có cho kiểm tra quy hoạch trước khi cọc không. Hợp đồng cọc phải ghi điều kiện hoàn cọc nếu thông tin quy hoạch/pháp lý không đúng.";

            if (ContainsAny(n, "vi tri dien tich hay phap ly", "uu tien phap ly hay vi tri", "phap ly hay vi tri"))
                return "Nếu mua lần đầu, pháp lý là điều kiện nền tảng: sổ gốc, chủ sở hữu, quy hoạch, tranh chấp, thế chấp và điều kiện sang tên phải rõ trước. Sau đó mới cân đối vị trí và diện tích. Vị trí tốt nhưng pháp lý rủi ro thì không nên cọc; pháp lý rõ nhưng vị trí quá bất tiện cũng ảnh hưởng ở thật và bán lại.";

            return "Nếu mua để ở: ưu tiên pháp lý rõ, hạ tầng, tiện ích và môi trường sống. Nếu đầu tư/bán lại: xem thanh khoản, quy hoạch, khả năng cho thuê, dòng tiền, đường vào và nhu cầu thật của khu vực. Giá rẻ bất thường cần kiểm tra kỹ pháp lý, quy hoạch, tranh chấp, đường vào và lý do bán. Em không dự đoán chắc chắn tăng giá.";
        }
        private static string GeneralAnswer() => "Em chưa đủ dữ liệu để trả lời chắc chắn câu này nên em không đoán bừa. Anh/Chị vui lòng nói rõ đang hỏi về: tìm mua/thuê, pháp lý, giao dịch, vay ngân hàng, đăng tin, dự án hay tin đang xem. Nếu hỏi về một tin cụ thể, hãy gửi tiêu đề, giá, diện tích, vị trí và pháp lý hiển thị để em phân tích đúng hơn.";

        private async Task<string> AiOrFallbackAsync(string original, string normalized, Scenario sc, Dictionary<string, string> slots, SlotPlan plan, PageInfo page, List<AIChatMessage> history)
        {
            string fallback = BuildLocalFallbackForAI(original, normalized, sc, slots, page);

            try
            {
                string knowledge = await KnowledgeContextAsync(sc);
                bool useExternalGrounding = NeedsExternalGrounding(sc, normalized);

                string prompt = BuildAIUserPrompt(
                    original,
                    normalized,
                    sc,
                    slots,
                    plan,
                    page,
                    history,
                    knowledge,
                    useExternalGrounding);

                AIChatCompletionResult rs = await _aiClient.GenerateAsync(new AIChatCompletionRequest
                {
                    SystemPrompt = SystemPrompt(),
                    UserPrompt = prompt,
                    Temperature = 0.18,
                    MaxOutputTokens = 6144,
                    UseAnswerModel = true,
                    UseGoogleSearchGrounding = useExternalGrounding
                });

                if (rs.Success && !string.IsNullOrWhiteSpace(rs.Text))
                {
                    return CleanAiAnswer(rs.Text);
                }

                return fallback;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI fallback error");
                return fallback;
            }
        }

        private static string BuildAIUserPrompt(
            string original,
            string normalized,
            Scenario sc,
            Dictionary<string, string> slots,
            SlotPlan plan,
            PageInfo page,
            List<AIChatMessage> history,
            string knowledge,
            bool useExternalGrounding)
        {
            string recentHistory = string.Join("\n", history
                .TakeLast(8)
                .Select(x => $"{x.Role}: {Trim(x.Content, 700)}"));

            string groundingNote = useExternalGrounding
                ? "Được phép dùng nguồn ngoài/Google Search Grounding cho kiến thức chung, quy định, pháp lý, thị trường, vay vốn, dự án, thủ tục. Nếu dùng nguồn ngoài, tóm tắt dễ hiểu và kèm mục 'Nguồn tham khảo' khi model trả về nguồn."
                : "Ưu tiên dữ liệu nội bộ và kiến thức đã huấn luyện. Không cần tìm web nếu câu hỏi chỉ là điều hướng website hoặc dữ liệu tin nội bộ.";

            return $"""
            Câu hỏi mới nhất của người dùng:
            {original}

            Câu đã chuẩn hóa không dấu:
            {normalized}

            Kịch bản phát hiện:
            - Scenario: {sc.Name}
            - Intent: {sc.Intent}
            - Stage thiếu slot: {string.Join(", ", plan.Missing)}
            - Slots đã thu thập: {JsonSerializer.Serialize(CleanSlots(slots))}
            - Trang hiện tại: {page.PageType} | {page.PageTitle} | {page.PageUrl}

            Lịch sử gần nhất:
            {recentHistory}

            Tri thức nội bộ/RAG từ hệ thống:
            {knowledge}

            Chế độ nguồn:
            {groundingNote}

            Nhiệm vụ trả lời:
            1. Trả lời bằng tiếng Việt, xưng "em", gọi người dùng là "Anh/Chị".
            2. Nếu là câu hỏi kiến thức chung về BĐS/pháp lý/vay/giao dịch/thuê/đăng tin/dự án thì PHẢI trả lời trực tiếp theo checklist dễ hiểu, không được nói "thiếu dữ liệu" một cách máy móc.
            3. Nếu là câu tìm tin cụ thể thì chỉ đề xuất tin khi đã đủ: mua/thuê + loại BĐS + khu vực cụ thể + ngân sách. Không được tự bịa tin, giá, mã tin hoặc nguồn tin ngoài website.
            4. Nếu hỏi pháp lý: chỉ tư vấn cơ bản, nhắc kiểm tra sổ gốc, quy hoạch, tranh chấp, thế chấp, công chứng/cơ quan có thẩm quyền; không tự nhận là luật sư.
            5. Nếu hỏi "Đất nông nghiệp có xây nhà được không?": trả lời rõ là không được tự ý xây nhà ở trên đất nông nghiệp nếu chưa chuyển mục đích/phù hợp quy hoạch/được cấp phép theo quy định; hướng dẫn các bước kiểm tra.
            6. Nếu hỏi đặt cọc/công chứng/sang tên: trình bày quy trình và các giấy tờ cần kiểm tra.
            7. Nếu hỏi vay vốn: ước lượng theo nguyên tắc an toàn, không cam kết ngân hàng duyệt vay; hỏi thêm giá tài sản, vốn tự có, thu nhập, thời hạn vay khi cần tính.
            8. Nếu hỏi đăng tin: hỗ trợ viết tiêu đề/mô tả trung thực, không phóng đại, không cam kết lợi nhuận.
            9. Nếu hỏi ngoài phạm vi hoặc nguy hiểm: từ chối ngắn gọn và chuyển hướng sang cách hợp pháp/an toàn.
            10. Câu trả lời nên có cấu trúc: nhận định ngắn → checklist/bước xử lý → lưu ý an toàn → câu hỏi tiếp theo nếu cần.
            11. Tuyệt đối không lặp lại cùng một câu trả lời hai lần. Không viết lại phần mở đầu lần thứ hai.
            12. Không kết thúc bằng dấu "..." do bị cắt. Nếu nội dung dài, hãy chọn ý chính nhất, viết đủ câu, đủ đoạn, kết thúc trọn vẹn.
            13. Độ dài nên vừa đủ để đọc trong khung chat: ưu tiên 5-7 mục chính, mỗi mục ngắn gọn; chỉ viết dài khi người dùng yêu cầu "chi tiết".
            14. Nếu có nguồn tham khảo ngoài, chỉ tóm tắt nguồn cần thiết, không bê quá nhiều thông tin khiến câu trả lời bị dài và lặp.
            """;
        }

        private static bool NeedsExternalGrounding(Scenario sc, string normalized)
        {
            if (sc.Name is "Legal" or "Transaction" or "Loan" or "Project" or "Market" or "MultiIntent")
                return true;

            if (ContainsAny(normalized,
                "luat moi", "quy dinh moi", "hien nay", "nam nay", "hom nay",
                "lai suat", "ngan hang", "thue", "le phi", "quy hoach",
                "dat nong nghiep", "chuyen muc dich", "cap phep xay dung",
                "du an", "bao lanh ngan hang", "hop dong mua ban can ho"))
            {
                return true;
            }

            return false;
        }

        private static string BuildLocalFallbackForAI(string original, string normalized, Scenario sc, Dictionary<string, string> slots, PageInfo page)
        {
            string direct = DirectAnswer(original, normalized, sc, slots, page);

            if (!string.IsNullOrWhiteSpace(direct) && direct != GeneralAnswer())
            {
                return direct + "\n\nLưu ý: đây là tư vấn cơ bản để Anh/Chị định hướng ban đầu. Với pháp lý, vay vốn hoặc thuế phí, Anh/Chị nên kiểm tra lại tại cơ quan có thẩm quyền, ngân hàng, văn phòng công chứng hoặc chuyên viên pháp lý.";
            }

            return """
            Em có thể hỗ trợ các nhóm chính của BĐS Khánh Hòa: tìm mua, thuê, phân tích tin đang xem, pháp lý cơ bản, giao dịch công chứng, vay vốn, đăng tin, dự án và kinh nghiệm thị trường.

            Với câu hỏi kiến thức chung, Anh/Chị có thể hỏi trực tiếp như:
            - Đất nông nghiệp có xây nhà được không?
            - Đặt cọc mua đất cần ghi điều khoản gì?
            - Mua đất lần đầu cần kiểm tra giấy tờ gì?
            - Thu nhập 20 triệu/tháng nên vay mua nhà tối đa bao nhiêu?

            Với câu tìm tin cụ thể, Anh/Chị gửi thêm loại BĐS, khu vực và ngân sách để em lọc từ dữ liệu nội bộ, tránh gợi ý sai.
            """;
        }

        private async Task<string> KnowledgeContextAsync(Scenario sc)
        {
            List<string> cats = new() { "Core", "Guardrail", "Fallback", sc.Name };
            if (sc.Name == "Buy" || sc.Name == "BuyAdvice") cats.AddRange(new[] { "Buy", "Legal", "Search", "Market" });
            if (sc.Name == "Rent" || sc.Name == "RentAdvice") cats.AddRange(new[] { "Rent", "Search", "Legal" });
            cats = cats.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var arts = await _context.Set<AIKnowledgeArticle>().AsNoTracking().Where(x => x.IsPublished && cats.Contains(x.Category)).OrderByDescending(x => x.UpdatedAt).Take(8).ToListAsync();
            StringBuilder sb = new();
            foreach (var a in arts) sb.AppendLine($"## {a.Title}\n{Trim(a.Content, 2200)}");
            return Trim(sb.ToString(), 12000);
        }

        private static string SystemPrompt() => """
        Bạn là Trợ lý AI BĐS Khánh Hòa, vai trò là trợ lý tư vấn bất động sản hiện đại cho website batdongsanhot.online.

        PHONG CÁCH:
        - Luôn xưng "em", gọi người dùng là "Anh/Chị".
        - Trả lời rõ, thực tế, có checklist, có bước xử lý, dễ hiểu cho người Việt.
        - Không trả lời cụt kiểu "không đủ dữ liệu" cho câu hỏi kiến thức chung. Câu kiến thức chung phải trả lời trực tiếp.
        - Không nói chắc chắn quá mức, không cam kết lợi nhuận, không thay thế luật sư/ngân hàng/công chứng/cơ quan nhà nước.
        - Không lặp lại cùng một nội dung hai lần trong một câu trả lời.
        - Không tự cắt câu bằng dấu "..."; nếu nội dung dài thì rút gọn có chọn lọc nhưng phải kết thúc trọn ý.

        PHÂN BIỆT 2 LOẠI CÂU HỎI:
        1. TÌM TIN BĐS TRONG WEBSITE:
           - Chỉ đề xuất tin khi đủ: mua/thuê + loại BĐS + khu vực cụ thể + ngân sách.
           - Chỉ dùng dữ liệu nội bộ/SQL do hệ thống đưa, không tự bịa tin, không lấy tin ngoài mạng để giả làm tin của website.
           - Nếu thiếu tiêu chí thì hỏi thêm ngắn gọn.

        2. KIẾN THỨC/TƯ VẤN CHUNG:
           - Pháp lý cơ bản, giao dịch, công chứng, đặt cọc, vay vốn, thuê nhà, đăng tin, dự án, kinh nghiệm thị trường phải được trả lời bằng kiến thức tổng quát.
           - Được tham khảo nguồn ngoài nếu hệ thống bật Google Search Grounding.
           - Nếu thông tin có thể thay đổi theo thời điểm như luật, thuế phí, lãi suất, quy hoạch, điều kiện vay, phải nói "cần kiểm tra lại tại nguồn/cơ quan có thẩm quyền tại thời điểm giao dịch".

        NHÓM KIẾN THỨC CẦN BAO PHỦ:
        - Vai trò và nguyên tắc an toàn của chatbot.
        - Tư vấn mua: nhu cầu, khu vực, ngân sách, pháp lý, mục đích ở/đầu tư/kinh doanh.
        - Tư vấn thuê: hợp đồng thuê, cọc, thời hạn, điện nước, kinh doanh, thú cưng, bàn giao.
        - Phân tích tin đang xem: giá, diện tích, vị trí, pháp lý, rủi ro, câu hỏi nên hỏi người bán.
        - Pháp lý cơ bản: sổ đỏ/sổ hồng, đất nông nghiệp, quy hoạch, tranh chấp, thế chấp, đồng sở hữu, thừa kế, giấy tay.
        - Giao dịch và công chứng: đặt cọc, công chứng, thuế phí, sang tên, thanh toán.
        - Vay vốn: tỷ lệ vay, áp lực trả nợ, thẩm định tài sản, lãi suất thả nổi.
        - Đăng tin: tiêu đề, mô tả, ảnh, định giá, không phóng đại.
        - Dự án: chủ đầu tư, pháp lý dự án, điều kiện mở bán, bảo lãnh, tiến độ, hợp đồng, phí quản lý.
        - Chăm sóc khách hàng: đặt lịch, tư vấn, báo cáo vi phạm, liên hệ hỗ trợ.
        - Kinh nghiệm thị trường: so sánh đất/nhà/căn hộ/mặt bằng, vị trí, thanh khoản, rủi ro giá rẻ.
        - Giới hạn an toàn: từ chối làm giả giấy tờ, lách luật, trốn thuế, che giấu tranh chấp, hack hệ thống, cá độ.

        CẤU TRÚC TRẢ LỜI NÊN DÙNG:
        - "Em hiểu câu hỏi của Anh/Chị là..."
        - "Trả lời nhanh: ..."
        - "Anh/Chị nên kiểm tra: 1... 2... 3..."
        - "Rủi ro cần tránh: ..."
        - "Bước tiếp theo: ..."
        """;

        private static string Welcome() => "Chào Anh/Chị! Em là Trợ lý AI BĐS Khánh Hòa. Em có thể hỗ trợ tìm mua/thuê, phân tích tin đang xem, pháp lý cơ bản, giao dịch công chứng, vay vốn, đăng tin, dự án và kinh nghiệm thị trường. Với nhu cầu tìm tin, em sẽ hỏi đủ thông tin rồi mới lọc dữ liệu nội bộ; với kiến thức chung, em sẽ trả lời theo checklist và có thể tham khảo nguồn ngoài nếu hệ thống bật tìm kiếm.";
        private static string UnsafeAnswer() => "Em không thể hỗ trợ làm giả giấy tờ, lách luật, trốn thuế, che giấu tranh chấp/quy hoạch/thế chấp, viết tin gian dối hoặc hack hệ thống. Em có thể hướng dẫn giao dịch hợp pháp và an toàn hơn.";
        private static string OffTopicAnswer(string n) => ContainsAny(n, "ngu", "tra loi lai") ? "Em xin lỗi vì câu trả lời trước chưa đúng ý. Anh/Chị gửi lại câu hỏi về bất động sản để em xử lý lại sát hơn." : "Em chủ yếu hỗ trợ bất động sản: mua, thuê, pháp lý, giao dịch, vay vốn, đăng tin, dự án và chăm sóc khách hàng.";

        private static string NeedSummary(Dictionary<string, string> slots, Scenario sc)
        {
            List<string> p = new();
            p.Add(slots.GetValueOrDefault("deal_type", sc.Name == "Rent" ? "Thuê" : "Mua").ToLower(Vi));
            if (slots.TryGetValue("property_type", out var t)) p.Add(t.ToLower(Vi));
            if (slots.TryGetValue("area_name", out var a)) p.Add("tại " + a);
            if (slots.TryGetValue("budget_max", out var b) && decimal.TryParse(b, NumberStyles.Any, CultureInfo.InvariantCulture, out var bd)) p.Add("dưới " + Price(bd));
            if (slots.TryGetValue("rent_max", out var r) && decimal.TryParse(r, NumberStyles.Any, CultureInfo.InvariantCulture, out var rd)) p.Add("dưới " + Price(rd) + "/tháng");
            return "Em hiểu nhu cầu ban đầu của Anh/Chị là " + string.Join(" ", p) + ".";
        }

        private static List<object> Cards(List<Property> props, Dictionary<string, string> slots, Scenario sc) => props
            .GroupBy(p => p.PropertyID)
            .Select(g => g.First())
            .Take(MaxCards)
            .Select(p => new { propertyID = p.PropertyID, title = p.Title, price = Price(p.Price), areaSize = p.AreaSize.HasValue ? p.AreaSize.Value.ToString("0.##", Vi) + " m²" : "Chưa cập nhật", areaName = p.Ward?.Area?.AreaName ?? "Khánh Hòa", wardName = p.Ward?.WardName ?? "", propertyTypeName = p.PropertyType?.TypeName ?? "Chưa cập nhật", image = string.IsNullOrWhiteSpace(p.MainImage) ? "/images/no-image.png" : p.MainImage, link = "/Property/Details/" + p.PropertyID, location = Location(p), reasonMatched = Reason(p, slots, sc) })
            .Cast<object>()
            .ToList();
        private static string Reason(Property p, Dictionary<string, string> slots, Scenario sc) { List<string> r = new(); if (slots.TryGetValue("area_name", out var a) && MatchArea(p, Normalize(a))) r.Add("đúng khu vực"); if (slots.TryGetValue("property_type", out var t) && MatchType(p, Normalize(t))) r.Add("đúng loại bất động sản"); if (slots.TryGetValue("budget_max", out var b) && decimal.TryParse(b, NumberStyles.Any, CultureInfo.InvariantCulture, out var bd) && p.Price.HasValue && p.Price.Value <= bd) r.Add("giá trong ngân sách"); return r.Any() ? string.Join(", ", r) + "." : "phù hợp một phần với tiêu chí đã nêu."; }
        private static string BuildPropertyContext(List<Property> props, Dictionary<string, string> slots, Scenario sc) { StringBuilder sb = new(); foreach (var p in props) sb.AppendLine($"ID {p.PropertyID}: {p.Title} | {Price(p.Price)} | {p.AreaSize:0.##}m² | {Location(p)} | /Property/Details/{p.PropertyID}"); return sb.ToString(); }
        private static string Location(Property p) => string.Join(", ", new[] { p.AddressDetail, p.Ward?.WardName, p.Ward?.Area?.AreaName }.Where(x => !string.IsNullOrWhiteSpace(x)));
        private static string Price(decimal? price) => !price.HasValue || price.Value <= 0 ? "Thỏa thuận" : Price(price.Value);
        private static string Price(decimal price) => price >= 1_000_000_000m ? (price / 1_000_000_000m).ToString("0.##", Vi) + " tỷ" : price >= 1_000_000m ? (price / 1_000_000m).ToString("0.##", Vi) + " triệu" : price.ToString("N0", Vi) + " đ";
        private static string FinalGuard(string msg, Scenario sc, List<Property> props, PageInfo page)
        {
            msg = CleanAiAnswer(msg);

            if (Unsafe(Normalize(msg)))
                return UnsafeAnswer();

            if ((sc.Name is "Buy" or "Rent") &&
                !props.Any() &&
                ContainsAny(Normalize(msg), "em tim thay", "em tim duoc", "tin phu hop ben duoi"))
            {
                return "Em chưa đủ dữ liệu hoặc chưa có kết quả SQL phù hợp nên không đề xuất tin. Anh/Chị vui lòng bổ sung loại BĐS, khu vực và ngân sách.";
            }

            // Không cắt câu trả lời bằng dấu "...".
            // Trước đây nếu msg > 6500 ký tự thì bị Trim + "...", làm người dùng tưởng lỗi giao diện.
            // Khung chat đã có thanh cuộn, nên để hiển thị toàn bộ nội dung trả về.
            return msg;
        }

        private static string Clean(string s)
        {
            string text = (s ?? "").Replace("```", "").Trim();
            text = Regex.Replace(text, @"\r\n", "\n");
            text = Regex.Replace(text, @"\n{4,}", "\n\n");
            return text.Trim();
        }

        private static string CleanAiAnswer(string? value)
        {
            string text = Clean(value ?? "");

            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            text = RemoveDuplicatedAnswerStart(text);

            // Dọn các trường hợp model tự kết thúc bằng nhiều dấu chấm do đang bị ép token.
            // Không xóa "..." ở giữa câu, chỉ xử lý đoạn cuối vô nghĩa.
            text = Regex.Replace(text, @"(\s*\.\s*){4,}$", ".", RegexOptions.Multiline).Trim();

            return text;
        }

        private static string RemoveDuplicatedAnswerStart(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string marker = "Em hiểu câu hỏi của Anh/Chị";
            int first = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

            if (first < 0)
                return text.Trim();

            int second = text.IndexOf(marker, first + marker.Length, StringComparison.OrdinalIgnoreCase);

            if (second < 0)
                return text.Trim();

            string firstPart = text.Substring(0, second).Trim();
            string secondPart = text.Substring(second).Trim();

            // Nếu Gemini trả lời 2 lần cùng cấu trúc, giữ phần sau vì thường là bản có grounding/nguồn tham khảo đầy đủ hơn.
            if (firstPart.Length > 700 && secondPart.Length > 700)
                return secondPart;

            return text.Trim();
        }

        private static void ApplyShortAnswer(string n, Dictionary<string, string> slots, List<AIChatMessage> history)
        {
            string last = Normalize(history.LastOrDefault(x => x.Role == "assistant")?.Content ?? "");

            // Bổ sung tiêu chí sau câu hỏi dẫn dắt: ngân sách, mục đích, pháp lý.
            if (ContainsAny(last, "ngan sach", "bao nhieu", "gia"))
                ExtractMoney(n, slots, new(SlotIs(slots, "deal_type", "Thuê") ? "Rent" : "Buy", "", true, true, false));

            string? purpose = Purpose(n);
            if (purpose != null) slots["purpose"] = purpose;

            string? legal = LegalNeed(n);
            if (legal != null) slots["legal_requirement"] = legal;

            string? road = RoadNeed(n);
            if (road != null) slots["road_requirement"] = road;

            string? amenities = Amenities(n);
            if (amenities != null) slots["amenities"] = amenities;
        }
        private static void ExtractMoney(string n, Dictionary<string, string> slots, Scenario sc)
        {
            bool rent = sc.Name == "Rent" || ContainsAny(n, "/thang", "moi thang", "gia thue");

            (decimal Min, decimal Max)? range = MoneyRange(n);
            if (range.HasValue && !rent)
            {
                slots["budget_min"] = range.Value.Min.ToString(CultureInfo.InvariantCulture);
                slots["budget_max"] = range.Value.Max.ToString(CultureInfo.InvariantCulture);
                return;
            }

            List<decimal> vals = MoneyValues(n);
            if (!vals.Any()) return;

            bool lower = ContainsAny(n, "tren", "tu", "it nhat", "toi thieu");
            bool upper = ContainsAny(n, "duoi", "toi da", "khong qua");

            if (rent)
            {
                slots["rent_max"] = vals.Max().ToString(CultureInfo.InvariantCulture);
                return;
            }

            if (vals.Count >= 2 && ContainsAny(n, "tu", "den", "toi", "khoang"))
            {
                slots["budget_min"] = vals.Min().ToString(CultureInfo.InvariantCulture);
                slots["budget_max"] = vals.Max().ToString(CultureInfo.InvariantCulture);
                return;
            }

            if (lower && !upper) slots["budget_min"] = vals.Max().ToString(CultureInfo.InvariantCulture);
            else slots["budget_max"] = vals.Max().ToString(CultureInfo.InvariantCulture);
        }

        private static (decimal Min, decimal Max)? MoneyRange(string n)
        {
            Match m = Regex.Match(n, @"(?<a>\d+(?:[\.,]\d+)?)\s*(?:-|den|toi)\s*(?<b>\d+(?:[\.,]\d+)?)\s*(?<unit>ty|ti|t\b|trieu|tr|cu)", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            if (!decimal.TryParse(m.Groups["a"].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal a)) return null;
            if (!decimal.TryParse(m.Groups["b"].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal b)) return null;
            string unit = m.Groups["unit"].Value.ToLowerInvariant();
            decimal factor = unit is "ty" or "ti" or "t" ? 1_000_000_000m : 1_000_000m;
            return (Math.Min(a, b) * factor, Math.Max(a, b) * factor);
        }

        private static List<decimal> MoneyValues(string n)
        {
            List<decimal> res = new();
            foreach (Match m in Regex.Matches(n, @"(?<a>\d+)\s*(?:ty|ti|t)\s*(?<b>\d)\b"))
                if (decimal.TryParse(m.Groups["a"].Value, out decimal a) && decimal.TryParse(m.Groups["b"].Value, out decimal b))
                    res.Add((a + b / 10m) * 1_000_000_000m);

            foreach (Match m in Regex.Matches(n, @"(?<num>\d+(?:[\.,]\d+)?)\s*(?<unit>ty|ti|t\b|trieu|tr|cu|k)", RegexOptions.IgnoreCase))
            {
                if (!decimal.TryParse(m.Groups["num"].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal num)) continue;
                string u = m.Groups["unit"].Value.ToLowerInvariant();
                res.Add(u is "ty" or "ti" or "t" ? num * 1_000_000_000m : u is "trieu" or "tr" or "cu" ? num * 1_000_000m : num * 1000m);
            }
            return res.Where(x => x > 0).Distinct().ToList();
        }

        private static void ExtractAreaSize(string n, Dictionary<string, string> slots) { var m = Regex.Match(n, @"(?<min>\d+(?:[\.,]\d+)?)\s*(?:m2|met|m)\s*(?:den|toi|-)\s*(?<max>\d+(?:[\.,]\d+)?)"); if (m.Success) { slots["area_min"] = Parse(m.Groups["min"].Value).ToString(CultureInfo.InvariantCulture); slots["area_max"] = Parse(m.Groups["max"].Value).ToString(CultureInfo.InvariantCulture); return; } var a = Regex.Match(n, @"(?<num>\d+(?:[\.,]\d+)?)\s*(?:m2|met vuong|met|m\b)"); if (a.Success) { decimal v = Parse(a.Groups["num"].Value); slots["area_min"] = Math.Max(1, v * .85m).ToString("0.##", CultureInfo.InvariantCulture); slots["area_max"] = (v * 1.15m).ToString("0.##", CultureInfo.InvariantCulture); } }
        private static decimal Parse(string s) { decimal.TryParse((s ?? "").Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v); return v; }
        private static decimal? DecimalSlot(Dictionary<string, string> slots, string key) => slots.TryGetValue(key, out var s) && decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
        private static bool HasBudget(Dictionary<string, string> slots) => slots.ContainsKey("budget_max") || slots.ContainsKey("budget_min") || slots.ContainsKey("rent_max");

        private static string? PropertyType(string n)
        {
            if (ContainsAny(n, "can ho", "chung cu")) return "Căn hộ";
            if (ContainsAny(n, "phong tro", "nha tro")) return "Phòng trọ";

            // Không dùng riêng từ "kinh doanh" để đổi loại hình thành Mặt bằng.
            // "kinh doanh" là MỤC ĐÍCH; chỉ khi có cụm rõ như mặt bằng/văn phòng/cửa hàng/shop... mới là loại hình.
            if (ContainsAny(n, "mat bang", "mat bang kinh doanh", "van phong", "cua hang", "shop", "shophouse", "showroom", "toa nha", "nha pho thuong mai"))
                return "Mặt bằng";

            if (ContainsAny(n, "biet thu", "villa")) return "Biệt thự";
            if (ContainsAny(n, "nha rieng", "nha pho", "nha nguyen can", "mua nha", "can nha", "co nha", "nha nao", "tim nha")) return "Nhà";
            if (ContainsAny(n, "dat", "lo dat", "dat nen", "dat tho cu")) return "Đất";
            return null;
        }

        private static string? AreaName(string n)
        {
            string normalized = Normalize(n);

            foreach (var kv in AreaMap())
            {
                string key = Normalize(kv.Key);

                // Alias cực ngắn như "nt" chỉ nhận khi là một từ riêng biệt.
                // Không dùng ContainsCompact cho alias ngắn vì dễ khớp bậy trong các chữ khác.
                if (key.Length <= 3)
                {
                    if (Regex.IsMatch(normalized, $@"(^|\s){Regex.Escape(key)}($|\s)"))
                        return kv.Value;
                    continue;
                }

                if (ContainsCompact(normalized, key)) return kv.Value;
            }
            return null;
        }

        private static Dictionary<string, string> AreaMap() => new(StringComparer.OrdinalIgnoreCase)
        {
            // Khánh Hòa cũ
            ["nha trang"] = "Nha Trang",
            ["nhatrang"] = "Nha Trang",
            ["nt"] = "Nha Trang",
            ["cam lam"] = "Cam Lâm",
            ["camlam"] = "Cam Lâm",
            ["cam ranh"] = "Cam Ranh",
            ["camranh"] = "Cam Ranh",
            ["ninh hoa"] = "Ninh Hòa",
            ["ninhhoa"] = "Ninh Hòa",
            ["dien khanh"] = "Diên Khánh",
            ["dienkhanh"] = "Diên Khánh",
            ["van ninh"] = "Vạn Ninh",
            ["vanninh"] = "Vạn Ninh",
            ["khanh vinh"] = "Khánh Vĩnh",
            ["khanhvinh"] = "Khánh Vĩnh",
            ["khanh son"] = "Khánh Sơn",
            ["khanhson"] = "Khánh Sơn",
            ["khanh hoa"] = "Khánh Hòa",

            // Khu vực Ninh Thuận cũ trong phạm vi Khánh Hòa mới theo dữ liệu đồ án
            ["phan rang"] = "Phan Rang-Tháp Chàm",
            ["phan rang thap cham"] = "Phan Rang-Tháp Chàm",
            ["prtc"] = "Phan Rang-Tháp Chàm",
            ["ninh hai"] = "Ninh Hải",
            ["ninhhai"] = "Ninh Hải",
            ["ninh phuoc"] = "Ninh Phước",
            ["ninhphuoc"] = "Ninh Phước",
            ["thuan bac"] = "Thuận Bắc",
            ["thuanbac"] = "Thuận Bắc",
            ["thuan nam"] = "Thuận Nam",
            ["thuannam"] = "Thuận Nam",
            ["ninh son"] = "Ninh Sơn",
            ["ninhson"] = "Ninh Sơn",
            ["bac ai"] = "Bác Ái",
            ["bacai"] = "Bác Ái",
            ["ninh chu"] = "Ninh Chữ",
            ["ninhchu"] = "Ninh Chữ",
            ["vinh hy"] = "Vĩnh Hy",
            ["vinhhy"] = "Vĩnh Hy",
            ["ca na"] = "Cà Ná",
            ["cana"] = "Cà Ná"
        };

        private static List<string> AreaAliases(string area)
        {
            string n = Normalize(area);
            List<string> aliases = new() { n };

            foreach (var kv in AreaMap())
            {
                string key = Normalize(kv.Key);
                string value = Normalize(kv.Value);

                bool isSameArea = ContainsCompact(n, key) || ContainsCompact(n, value);
                if (!isSameArea) continue;

                // Không thêm alias quá ngắn kiểu "nt" vào danh sách khớp khi người dùng nói "Nha Trang".
                // Nếu thêm "nt", rất dễ khớp bậy với chữ trong địa chỉ/mô tả và lôi tin Diên Khánh/Cam Lâm/Ninh Phước ra.
                if (key.Length >= 4 || n == key) aliases.Add(key);
                if (value.Length >= 4) aliases.Add(value);
            }

            if (ContainsAny(n, "phan rang", "phan rang thap cham", "prtc"))
                aliases.AddRange(new[] { "phan rang", "phan rang thap cham", "thap cham", "phuoc my", "dao long", "my hai", "my dong", "bao an", "kinh dinh" });

            if (ContainsAny(n, "ninh hai", "ninh chu", "vinh hy"))
                aliases.AddRange(new[] { "ninh hai", "ninh chu", "vinh hy", "khanh hai", "tri hai", "nhon hai", "thanh hai" });

            if (ContainsAny(n, "ninh phuoc"))
                aliases.AddRange(new[] { "ninh phuoc", "phuoc dan", "phuoc hai", "phuoc huu", "phuoc thuan", "phuoc son" });

            if (ContainsAny(n, "thuan nam", "ca na"))
                aliases.AddRange(new[] { "thuan nam", "ca na", "phuoc diem", "phuoc nam", "son hai" });

            if (ContainsAny(n, "thuan bac")) aliases.AddRange(new[] { "thuan bac", "loi hai", "cong hai", "phuoc chien" });
            if (ContainsAny(n, "ninh son")) aliases.AddRange(new[] { "ninh son", "tan son", "lam son", "quang son" });
            if (ContainsAny(n, "bac ai")) aliases.AddRange(new[] { "bac ai", "phuoc dai", "phuoc tan", "phuoc tien" });

            return aliases
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(Normalize)
                .Where(x => x.Length >= 4 || x == n)
                .Distinct()
                .ToList();
        }

        private static bool IsProvinceWideArea(string areaNeed)
        {
            return ContainsAny(areaNeed, "khanh hoa", "toan tinh", "toan khanh hoa", "tinh khanh hoa", "khanh hoa moi");
        }
        private static string? Purpose(string n)
        {
            bool live = ContainsAny(n, "o thuc", "de o", "xay nha", "gia dinh", "o lau dai");
            bool business = ContainsAny(n, "kinh doanh", "mo quan", "buon ban", "lam mat bang", "vua o vua kinh doanh");
            if (live && business) return "Ở kết hợp kinh doanh";
            if (live) return "Ở thực";
            if (ContainsAny(n, "dau tu", "giu tai san", "ban lai")) return "Đầu tư";
            if (business) return "Kinh doanh";
            return null;
        }
        private static string? LegalNeed(string n)
        {
            if (ContainsAny(n, "so rieng", "so do", "so hong", "phap ly ro", "co so", "co so day du", "so day du", "so ro rang", "giay to day du", "phap ly day du"))
                return "Ưu tiên sổ riêng/pháp lý rõ";
            if (ContainsAny(n, "tho cu", "dat o")) return "Ưu tiên đất ở/thổ cư";
            return null;
        }
        private static string? RoadNeed(string n) { if (ContainsAny(n, "o to", "oto", "xe hoi")) return "Đường ô tô"; if (ContainsAny(n, "mat tien")) return "Mặt tiền"; return null; }
        private static string? Amenities(string n) { List<string> a = new(); if (ContainsAny(n, "gan cho")) a.Add("gần chợ"); if (ContainsAny(n, "gan truong")) a.Add("gần trường"); if (ContainsAny(n, "gan bien")) a.Add("gần biển"); if (ContainsAny(n, "trung tam")) a.Add("gần trung tâm"); return a.Any() ? string.Join(", ", a) : null; }

        private static string? PageField(PageInfo page, string label)
        {
            if (string.IsNullOrWhiteSpace(page.ContextText) || string.IsNullOrWhiteSpace(label)) return null;

            string text = Regex.Replace(page.ContextText, @"\s+", " ").Trim();
            string[] labels =
            {
                "Tiêu đề", "Tiêu đề tin", "Loại giao dịch", "Loại bất động sản", "Loại BĐS",
                "Giá", "Diện tích", "Đơn giá", "Vị trí", "Khu vực", "Phường/Xã",
                "Dự án liên quan", "Phòng ngủ", "Phòng tắm", "Hướng nhà", "Pháp lý",
                "Tiện ích", "Tình trạng tin", "Ngày đăng", "Người đăng", "Chủ tin",
                "Người bán", "Môi giới", "Mô tả người đăng"
            };

            string lookAhead = string.Join("|", labels.Select(Regex.Escape));
            string pattern = Regex.Escape(label) + @"\s*:\s*(?<v>.*?)(?=\s+(?:" + lookAhead + @")\s*:|$)";
            Match m = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) return null;

            string value = Regex.Replace(m.Groups["v"].Value, @"\s+", " ").Trim();
            value = value.Trim(' ', '.', ';', '|');

            // Sửa các trường bị rút cụt do HTML hoặc dấu câu lạ.
            if (label.Equals("Giá", StringComparison.OrdinalIgnoreCase))
            {
                Match price = Regex.Match(value, @"\d+(?:[\.,]\d+)?\s*(?:tỷ|ty|triệu|tr|đ|dong)?", RegexOptions.IgnoreCase);
                if (price.Success) value = NormalizeDisplayMoney(price.Value, value);
            }
            if (label.Equals("Diện tích", StringComparison.OrdinalIgnoreCase))
            {
                Match area = Regex.Match(value, @"\d+(?:[\.,]\d+)?\s*(?:m²|m2|m|met vuong)?", RegexOptions.IgnoreCase);
                if (area.Success) value = area.Value.Contains("m") ? area.Value : area.Value + " m²";
            }

            return string.IsNullOrWhiteSpace(value) ? null : Trim(value, 350);
        }

        private static string CleanTitle(string? title, string fallback)
        {
            if (string.IsNullOrWhiteSpace(title)) return fallback;
            string t = title.Replace("- BDSKhanhHoa", "", StringComparison.OrdinalIgnoreCase).Trim();
            return string.IsNullOrWhiteSpace(t) ? fallback : t;
        }

        private static decimal? FirstMoneyAfter(string n, params string[] anchors)
        {
            string normalized = Normalize(n);
            foreach (string anchor in anchors)
            {
                int idx = normalized.IndexOf(Normalize(anchor), StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                string part = normalized[idx..];
                List<decimal> values = MoneyValues(part);
                if (values.Any()) return values.First();
            }
            return null;
        }

        private static PageInfo BuildPageInfo(ChatRequest req) { string type = string.IsNullOrWhiteSpace(req.PageType) ? "General" : req.PageType.Trim(); string url = req.PageUrl ?? ""; string title = req.PageTitle ?? ""; string ctx = StripHtml(req.PageContext ?? ""); string u = Normalize(url); if (type == "General" && u.Contains("/property/details")) type = "PropertyDetail"; if (type == "General" && u.Contains("/project/details")) type = "ProjectDetail"; return new PageInfo { PageType = type, PageUrl = url, PageTitle = title, ContextText = ctx }; }
        private static string StripHtml(string s) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(s ?? "", "<.*?>", " ")), @"\s+", " ").Trim();
        private static Dictionary<string, string> CleanSlots(Dictionary<string, string> slots) => slots.Where(x => !string.IsNullOrWhiteSpace(x.Value)).ToDictionary(x => x.Key, x => Trim(x.Value, 1000), StringComparer.OrdinalIgnoreCase);
        private static string Trim(string s, int max) => string.IsNullOrWhiteSpace(s) ? "" : s.Length <= max ? s : s[..max];
        // =====================================================================
        // ULTRA INTENT LAYER - MỞ RỘNG NHẬN DIỆN Ý NGƯỜI DÙNG THEO NHIỀU VÙNG/PHONG CÁCH NHẮN
        // =====================================================================
        // Ghi chú triển khai:
        // 1. Tầng này ưu tiên nhận diện theo ngữ nghĩa thực tế của người Việt khi hỏi BĐS:
        //    - Người miền Nam: "mua miếng đất", "kiếm căn", "thuê nguyên căn", "cọc sao cho chắc".
        //    - Người miền Trung/Khánh Hòa: "Nha Trang", "Ninh Hòa", "Diên Khánh", "Cam Lâm", "Cam Ranh", "Vạn Ninh".
        //    - Người gõ nhanh/teen code: "k", "ko", "khum", "dc", "đc", "nt", "ntrang", "1ty5", "800tr".
        //    - Người không nói rõ ý: "có căn nào ổn không", "tầm này mua được gì", "coi giúp tin này".
        // 2. Không vì nhận diện rộng mà tự lọc SQL bừa. Luật gốc vẫn giữ: muốn gợi ý tin phải đủ loại BĐS + khu vực cụ thể + ngân sách.
        // 3. Nếu câu hỏi là pháp lý/vay/quy trình/đăng tin/chăm sóc khách hàng thì đi route tư vấn, không chạy SQL.
        // 4. Nếu câu hỏi nhiều ý định thì tách MultiIntent để trả lời từng phần, tránh hiểu sai một câu dài.

        private static readonly string[] BuyNeedWords =
        {
            "toi muon mua", "em muon mua", "anh muon mua", "chi muon mua", "can mua", "muon mua", "tim mua", "kiem mua", "kiem giup",
            "mua giup", "mua nha", "mua dat", "mua can ho", "mua chung cu", "mua mat bang", "mua bds", "mua bat dong san",
            "co nha nao", "co dat nao", "co can nao", "co lo nao", "co mieng nao", "co bat dong san nao", "nha nao", "dat nao",
            "kiem can", "kiem lo", "kiem mieng dat", "kiem nha", "kiem dat", "kiem can ho", "kiem mat bang", "kiem bds",
            "toi can dat", "toi can nha", "toi can can ho", "toi can mat bang", "can lo dat", "can mieng dat",
            "muon kiem nha", "muon kiem dat", "muon kiem can", "muon tim nha", "muon tim dat", "muon tim can",
            "xem giup nha", "xem giup dat", "tu van mua", "tu van chon mua", "nen mua can nao", "nen mua dat nao",
            "co cai nao ban", "co ban nha", "co ban dat", "ban cho toi", "can xem nha ban", "can xem dat ban",
            "dat xay nha", "dat de o", "dat dau tu", "dat giu tien", "dat lam nha vuon", "dat nghi duong", "lo dat", "mieng dat", "nen dat",
            "nha de o", "nha dau tu", "nha pho", "nha rieng", "nha nguyen can de mua", "can ho de mua", "chung cu de mua"
        };

        private static readonly string[] RentNeedWords =
        {
            "toi muon thue", "em muon thue", "anh muon thue", "chi muon thue", "can thue", "muon thue", "tim thue", "kiem thue",
            "thue nha", "thue can ho", "thue chung cu", "thue phong", "thue phong tro", "thue tro", "thue nha tro", "thue mat bang", "thue van phong",
            "thue shop", "thue cua hang", "thue kho", "thue dat", "thue nguyen can", "thue dai han", "thue ngan han",
            "co phong nao", "co nha thue nao", "co can ho thue nao", "co mat bang thue nao", "co van phong nao thue",
            "kiem phong", "kiem tro", "kiem nha thue", "kiem can ho thue", "kiem mat bang thue", "can phong", "can nha thue",
            "o tro", "phong o", "nha o thue", "can cho thue", "nha cho thue", "mat bang cho thue", "van phong cho thue",
            "tim cho o", "can cho o", "tim phong gan", "tim nha gan", "tim can ho gan", "tim mat bang gan"
        };

        private static readonly string[] SearchListingWords =
        {
            "tim tin", "loc tin", "xem tin", "xem danh sach", "danh sach tin", "goi y tin", "de xuat tin", "cho toi xem", "cho em xem",
            "cho anh xem", "cho chi xem", "co tin nao", "co nha nao", "co dat nao", "co can nao", "co lo nao", "co mieng nao",
            "tim 1", "tim mot", "tim vai", "tim may", "loc giup", "goi y giup", "de xuat giup", "show tin", "show nha", "show dat",
            "hien tin", "hien danh sach", "lay tin", "lay danh sach", "co hang nao", "co san pham nao", "co cai nao phu hop",
            "tầm này mua được gì", "tam nay mua duoc gi", "gia nay mua duoc gi", "ngan sach nay mua duoc gi", "loc theo gia",
            "tim theo khu vuc", "loc theo khu vuc", "tim quanh day", "gan day co khong", "gan cho toi", "gan trung tam"
        };

        private static readonly string[] AnotherWords =
        {
            "tin nao khac", "cai nao khac", "can nao khac", "lo nao khac", "mieng nao khac", "nha nao khac", "dat nao khac",
            "xem them", "tim them", "goi y them", "de xuat them", "con nua khong", "con tin nao khong", "doi tin khac",
            "khac di", "co lua chon khac", "lua chon khac", "so sanh them", "cho them vai tin", "them may tin", "them lua chon",
            "gia thap hon", "re hon", "gan trung tam hon", "dien tich lon hon", "rong hon", "nho hon", "phap ly ro hon", "duong lon hon",
            "gan bien hon", "gan cho hon", "gan truong hon", "co so rieng hon", "co anh that hon"
        };

        private static readonly string[] RefinementWords =
        {
            "gia thap hon", "re hon", "mem hon", "bot gia", "duoi ngan sach", "gan trung tam hon", "gan bien", "gan cho", "gan truong",
            "dien tich lon hon", "dien tich nho hon", "rong hon", "nho hon", "phap ly ro", "so rieng", "so hong", "so do",
            "duong o to", "duong oto", "mat tien", "hem rong", "duong lon", "khong quy hoach", "co thang may", "co noi that",
            "co san vuon", "co cho dau xe", "co gara", "an ninh", "khu dan cu", "gan khu cong nghiep", "gan bien", "view bien",
            "dat o", "tho cu", "full tho cu", "co vay ngan hang", "ngan hang ho tro", "gia tot hon", "vi tri dep hon"
        };

        private static readonly string[] AppointmentWords =
        {
            "dat lich", "lich hen", "hen xem", "dat lich xem", "muon hen xem", "muon xem thuc te", "di xem nha", "di xem dat", "di coi nha", "di coi dat",
            "coi thuc te", "xem truc tiep", "cho xem nha", "cho xem dat", "hen chu nha", "hen chu dat", "hen moi gioi", "hen ngay mai", "hen cuoi tuan",
            "sang mai xem", "chieu nay xem", "toi nay xem", "cuoi tuan di xem", "co ai dan xem", "lien he xem nha", "lien he xem dat"
        };

        private static readonly string[] CurrentPropertyWords =
        {
            "tin nay", "bai nay", "nha nay", "dat nay", "lo nay", "mieng nay", "can nay", "bds nay", "bat dong san nay", "du an nay",
            "gia nay", "vi tri nay", "khu nay", "khu vuc nay", "cho nay", "dia chi nay", "phap ly nay", "chu nay", "nguoi dang nay",
            "dang xem", "toi dang xem", "em dang xem", "o trang nay", "tin dang xem", "bai dang xem", "nha dang xem", "dat dang xem",
            "co nen mua", "co nen thue", "co nen dat coc", "co nen hen xem", "co nen di xem", "co on khong", "co rui ro khong",
            "gia hop ly khong", "gia cao khong", "gia re khong", "co bi ao gia khong", "co dang nghi khong", "uy tin khong",
            "o dau", "khu vuc nao", "dia chi dau", "ai la chu", "chu tin", "nguoi dang", "moi gioi hay chu", "co so khong",
            "ban lai", "thanh khoan", "sau nay ban", "de ban lai", "xay nha", "cat nha", "de o", "de dau tu", "bao cao tin nay", "tin sai khong"
        };

        private static readonly string[] LegalWords =
        {
            "phap ly", "phap li", "so do", "sodo", "so hong", "so rieng", "so chung", "so dong so huu", "giay chung nhan", "giay to",
            "quy hoach", "dinh quy hoach", "vuong quy hoach", "treo quy hoach", "lo gioi", "ranh gioi", "tranh chap", "lan ranh",
            "the chap", "cam co", "ngan chan", "bi chan giao dich", "khong sang ten", "sang ten duoc khong", "cong chung duoc khong",
            "giay tay", "vi bang", "uy quyen", "thua ke", "dong so huu", "vo chong", "tai san chung", "hoan cong", "giay phep xay dung",
            "muc dich su dung dat", "dat o", "dat tho cu", "dat nong nghiep", "dat trong cay", "dat lua", "chuyen muc dich", "tach thua", "hop thua",
            "kiem tra gi", "kiem tra phap ly", "kiem tra so", "anh chup so", "ban photo so", "so gia", "so that", "co duoc xay", "duoc xay nha",
            "cap phep xay dung", "dat co duoc xay", "dat co so", "dat chua so", "dat chua len tho cu", "dat quy hoach cay xanh"
        };

        private static readonly string[] TransactionWords =
        {
            "quy trinh", "thu tuc", "cac buoc", "lam sao mua", "lam sao ban", "mua ban nhu the nao", "giao dich", "sang ten", "ra so",
            "cong chung", "van phong cong chung", "dat coc", "hop dong dat coc", "coc bao nhieu", "phat coc", "hoan coc", "giu coc",
            "thanh toan", "chuyen tien", "chuyen het tien", "giao tien", "giu lai tien", "nhan so moi chuyen tien", "ban giao", "ban giao nha",
            "thue thu nhap", "thue tncn", "le phi truoc ba", "phi cong chung", "phi sang ten", "chi phi mua ban", "chi phi", "thue phi",
            "thue khi ban lai", "ban lai co dong thue", "ben nao chiu thue", "ben mua chiu gi", "ben ban chiu gi", "ho so cong chung",
            "sau khi cong chung", "bao lau co so", "nhan so", "nhan so hong", "nhan so do", "nop ho so", "ke khai thue"
        };

        private static readonly string[] LoanWords =
        {
            "vay ngan hang", "vay mua", "vay mua nha", "vay mua dat", "vay xay nha", "tra gop", "lai suat", "lai suat vay", "lai tha noi",
            "thu nhap", "luong", "luong thang", "chung minh thu nhap", "ho so vay", "han muc vay", "vay duoc bao nhieu", "ngan hang co cho vay",
            "ngan hang co nhan the chap", "the chap ngan hang", "tham dinh", "gia tri tham dinh", "ty le vay", "vay 50", "vay 70", "vay 80",
            "vay 1 ty", "vay 2 ty", "trong 10 nam", "trong 15 nam", "trong 20 nam", "ap luc tra no", "tien tra hang thang",
            "no xau", "cic", "lich su tin dung", "tat toan truoc han", "phi tra no truoc han", "dung het tien tiet kiem", "nen vay",
            "co so do thi vay", "co so hong thi vay", "dat chua tho cu vay", "dat nong nghiep ngan hang"
        };

        private static readonly string[] PostingWords =
        {
            "dang tin", "dang bai", "viet tin", "viet bai", "viet tieu de", "viet mo ta", "soan tin", "soan bai", "toi muon ban",
            "can ban", "muon ban", "ban nha", "ban dat", "ban can ho", "ban mat bang", "cho thue nha", "cho thue dat", "cho thue can ho",
            "tin dang bi tu choi", "bi tu choi", "admin tu choi", "bi khoa tin", "khong duyet tin", "duyet tin", "goi dang tin", "tin vip",
            "dinh gia", "gia ban", "ban nhanh", "khong bi ep gia", "ep gia", "trinh bay tin", "de co khach", "de co nguoi hoi",
            "bao loi", "cam ket loi nhuan", "chac chan tang gia", "bao lai", "bao loi nhuan", "anh that", "anh bia", "anh dai dien",
            "dang may anh", "them anh", "xoa anh", "sua tin", "cap nhat tin", "an so dien thoai", "thong tin lien he"
        };

        private static readonly string[] ProjectWords =
        {
            "du an", "can ho du an", "dat nen du an", "nha pho du an", "chu dau tu", "phap ly du an", "hinh thanh trong tuong lai",
            "hop dong mua ban du an", "hop dong gop von", "dat coc du an", "bao lanh ngan hang", "tien do", "cham tien do",
            "mo ban", "giu cho", "booking", "phieu giu cho", "cam ket loi nhuan", "loi nhuan cam ket", "condotel", "shophouse du an",
            "ban giao", "so hong du an", "ra so du an", "du an co uy tin", "kiem tra chu dau tu", "du an ma"
        };

        private static readonly string[] SupportWords =
        {
            "tai khoan", "dang nhap", "dang ky", "quen mat khau", "doi mat khau", "xac thuc", "email", "otp", "ho so tai khoan",
            "thanh toan roi", "da thanh toan", "chua duoc cong luot", "khong duoc cong luot", "nap tien", "goi tin", "mua goi", "hoa don",
            "hotline", "lien he admin", "lien he ho tro", "ho tro", "cham soc khach hang", "bao loi he thong", "loi web", "khong bam duoc",
            "khong dang tin duoc", "khong upload anh duoc", "khong sua duoc", "khong xoa duoc", "khong xem duoc so dien thoai", "khong nhan thong bao", "cach su dung web", "huong dan su dung", "web nay dung sao", "xem tin le", "xem du an", "tin du an", "luu tin", "yeu cau tu van", "bo loc tin"
        };

        private static readonly string[] ComplaintWords =
        {
            "khieu nai", "to cao", "phan anh", "lua dao", "nghi lua dao", "tin gia", "tin ao", "tin sai", "sai gia", "sai vi tri",
            "anh gia", "anh khong dung", "bao cao tin", "bao cao", "vi pham", "report tin", "tin lua dao", "so dien thoai lua dao",
            "yeu cau chuyen coc", "doi coc som", "mat tien coc", "bi lua", "nguoi dang khong dung", "moi gioi mao danh", "chu nha gia",
            "tin da ban", "tin het hang", "tin trung lap", "spam", "dang nhieu tin giong nhau"
        };

        private static readonly string[] MarketWords =
        {
            "nen mua", "nen chon", "so sanh", "dat nen hay nha", "nha hay dat", "can ho hay nha", "mua dat hay mua nha", "gia re bat thuong",
            "co dang nghi", "co on khong", "co kha thi khong", "kha thi khong", "uu tien phap ly hay vi tri", "vi tri hay dien tich",
            "ban lai", "thanh khoan", "de ban lai", "sau nay ban", "giu tai san", "giu tien", "chong truot gia", "dau tu dai han",
            "ngan sach gioi han", "giam dien tich", "giam vi tri", "dat mat tien gia cao", "mat tien gia cao", "duong nho", "hem nho",
            "gia khu nay", "thi truong", "gia dat", "gia nha", "gia co cao khong", "co nen xuong tien", "co nen cho them", "thoi diem mua"
        };

        private static readonly string[] UnsafeWords =
        {
            "lam gia so do", "lam gia so hong", "lam so gia", "mua so gia", "hop dong gia", "hop dong hai gia", "ne thue", "tron thue", "lach thue",
            "giau tranh chap", "che giau tranh chap", "giau quy hoach", "noi sai phap ly", "noi qua len de lua", "viet tin gian doi",
            "lua nguoi mua", "lua nguoi ban", "noi qua len", "phong dai sai su that", "che giau thong tin", "lam hop dong gia", "hop dong gia de vay", "lam ho so gia", "hack", "lay cap tai khoan", "pha web", "spam tu dong", "crawl du lieu trai phep", "ca do"
        };

        private static readonly string[] OffTopicWords =
        {
            "hom nay an gi", "an gi ngon", "bong da", "game", "choi game", "phim", "nhac", "tinh yeu", "nguoi yeu", "may ngu", "chui", "ke chuyen cuoi",
            "thoi tiet", "xem boi", "tu vi", "xem tuoi", "coin", "crypto", "chung khoan", "mua dien thoai", "mua laptop"
        };

        private static readonly Dictionary<string, string> ExtraNormalizeMap = new(StringComparer.OrdinalIgnoreCase)
        {
            // Viết tắt chung
            { "bds", "bat dong san" }, { "bđs", "bat dong san" }, { "bds kh", "bat dong san khanh hoa" },
            { "sodo", "so do" }, { "sổđỏ", "so do" }, { "sohong", "so hong" }, { "sổhồng", "so hong" },
            { "phap li", "phap ly" }, { "phaply", "phap ly" }, { "pl", "phap ly" }, { "qh", "quy hoach" },
            { "dt", "dien tich" }, { "dtich", "dien tich" }, { "d.tich", "dien tich" }, { "gia ca", "gia" },
            { "nt", "nha trang" }, { "ntrang", "nha trang" }, { "nha trag", "nha trang" }, { "nhatrang", "nha trang" },
            { "dk", "dien khanh" }, { "dienkhanh", "dien khanh" }, { "d khanh", "dien khanh" },
            { "cl", "cam lam" }, { "camlam", "cam lam" }, { "cr", "cam ranh" }, { "camranh", "cam ranh" },
            { "nh", "ninh hoa" }, { "ninhhoa", "ninh hoa" }, { "vanninh", "van ninh" }, { "van ninh", "van ninh" },
            { "khanhvinh", "khanh vinh" }, { "khanhson", "khanh son" },
            { "phanrang", "phan rang" }, { "phan rang-thap cham", "phan rang thap cham" }, { "phanrangthapcham", "phan rang thap cham" },
            { "ninhhai", "ninh hai" }, { "ninhphuoc", "ninh phuoc" }, { "thuanbac", "thuan bac" }, { "thuannam", "thuan nam" },
            { "ninhson", "ninh son" }, { "bacai", "bac ai" }, { "ninhchu", "ninh chu" }, { "vinhhy", "vinh hy" }, { "cana", "ca na" },
            // Tiền/đơn vị
            { "1ty", "1 ty" }, { "1ty5", "1.5 ty" }, { "1t5", "1.5 ty" }, { "1 ti 5", "1.5 ty" }, { "1 tỷ 5", "1.5 ty" },
            { "2ty", "2 ty" }, { "2ty5", "2.5 ty" }, { "3ty", "3 ty" }, { "4ty", "4 ty" }, { "5ty", "5 ty" },
            { "500tr", "500 trieu" }, { "600tr", "600 trieu" }, { "700tr", "700 trieu" }, { "800tr", "800 trieu" }, { "900tr", "900 trieu" },
            { "7tr", "7 trieu" }, { "8tr", "8 trieu" }, { "10tr", "10 trieu" }, { "15tr", "15 trieu" }, { "20tr", "20 trieu" },
            { "tr/tháng", "trieu thang" }, { "tr/thang", "trieu thang" }, { "tr tháng", "trieu thang" }, { "tr thang", "trieu thang" },
            { "tháng", "thang" }, { "thang", "thang" },
            // Gõ nhanh/không dấu
            { "ko", "khong" }, { "k", "khong" }, { "khum", "khong" }, { "hong", "khong" }, { "hok", "khong" }, { "hông", "khong" },
            { "dc", "duoc" }, { "đc", "duoc" }, { "được", "duoc" }, { "duoc ko", "duoc khong" }, { "ok ko", "on khong" },
            { "mk", "minh" }, { "m", "minh" }, { "t", "toi" }, { "toi", "toi" }, { "tui", "toi" }, { "e", "em" },
            { "ae", "anh em" }, { "a/c", "anh chi" }, { "anh/chị", "anh chi" }, { "ac", "anh chi" },
            { "ib", "lien he" }, { "inbox", "lien he" }, { "lh", "lien he" }, { "sdt", "so dien thoai" },
            { "oto", "o to" }, { "ô tô", "o to" }, { "xe hoi", "o to" }, { "xe hơi", "o to" },
            { "mt", "mat tien" }, { "hẻm", "hem" }, { "hem oto", "hem o to" },
            // Loại hình
            { "cc", "chung cu" }, { "chungcư", "chung cu" }, { "canho", "can ho" }, { "căn hộ", "can ho" },
            { "nha pho", "nha pho" }, { "nhapho", "nha pho" }, { "nha rieng", "nha rieng" }, { "nha nguyen can", "nha nguyen can" },
            { "phongtro", "phong tro" }, { "nhatro", "nha tro" }, { "matbang", "mat bang" }, { "mbkd", "mat bang kinh doanh" },
            { "shophouse", "shop house" }, { "shop-house", "shop house" }, { "villa", "biet thu" }
        };

        private static string Normalize(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            string t = input.Trim().ToLowerInvariant();

            // Chuẩn hóa các kiểu gõ rất phổ biến trong chat: 1,5 tỷ -> 1.5 ty; 8,81 tỷ -> 8.81 ty.
            t = Regex.Replace(t, @"(?<=\d),(?=\d)", ".");
            t = t.Replace('đ', 'd').Normalize(NormalizationForm.FormD);

            StringBuilder sb = new();
            foreach (char c in t)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            t = sb.ToString().Normalize(NormalizationForm.FormC);

            // Giữ lại ký tự cần cho tiền/diện tích/khoảng giá, loại bỏ emoji/ký tự gây nhiễu.
            t = Regex.Replace(t, @"[\u200B-\u200D\uFEFF]", " ");
            t = Regex.Replace(t, @"[^a-z0-9\s\.,/\-<>=%]+", " ");
            t = Regex.Replace(t, @"\s+", " ").Trim();

            // Thay thế alias dài trước, tránh alias ngắn như "k" làm hỏng từ khác.
            foreach (var kv in ExtraNormalizeMap.OrderByDescending(x => x.Key.Length))
                t = Regex.Replace(t, $@"(^|\s){Regex.Escape(NormalizeKey(kv.Key))}(?=\s|$)", "$1" + kv.Value);

            // Chuẩn hóa tiền theo nhiều cách gõ của người dùng.
            t = Regex.Replace(t, @"(?<num>\d+(?:[\.,]\d+)?)\s*(ty|ti|tỷ|tỉ|bil|b)\b", "${num} ty");
            t = Regex.Replace(t, @"(?<num>\d+(?:[\.,]\d+)?)\s*(trieu|triệu|tr)\b", "${num} trieu");
            t = Regex.Replace(t, @"(?<num>\d+(?:[\.,]\d+)?)\s*(m2|m²|met vuong|met)\b", "${num} m2");
            t = Regex.Replace(t, @"(?<num>\d+(?:[\.,]\d+)?)\s*trieu\s*/?\s*thang", "${num} trieu thang");

            // Chuẩn hóa câu kiểu "dưới 1 tỷ rưỡi", "tầm 2 tỷ đổ lại".
            t = t.Replace("ruoi", ".5");
            t = t.Replace("do lai", "tro lai");
            t = t.Replace("tro xuong", "tro lai");
            t = Regex.Replace(t, @"\s+", " ").Trim();
            return t;
        }

        private static string NormalizeKey(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            string t = input.Trim().ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
            StringBuilder sb = new();
            foreach (char c in t)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            t = sb.ToString().Normalize(NormalizationForm.FormC);
            t = Regex.Replace(t, @"[^a-z0-9\s\.,/\-<>=%]+", " ");
            return Regex.Replace(t, @"\s+", " ").Trim();
        }

        private static bool ContainsAny(string text, params string[] keys)
        {
            string n = Normalize(text);
            if (string.IsNullOrWhiteSpace(n)) return false;
            string compactText = Regex.Replace(n, @"\s+", "");
            foreach (var key in keys)
            {
                string nk = Normalize(key);
                if (string.IsNullOrWhiteSpace(nk)) continue;
                if (Regex.IsMatch(n, $@"(^|\s){Regex.Escape(nk)}(?=\s|$|,|\.|-|/)", RegexOptions.IgnoreCase)) return true;
                if (n.Contains(nk, StringComparison.OrdinalIgnoreCase)) return true;
                string ck = Regex.Replace(nk, @"\s+", "");
                if (ck.Length >= 3 && compactText.Contains(ck, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool ContainsCompact(string text, string key)
        {
            string a = Regex.Replace(Normalize(text), @"\s+", "");
            string b = Regex.Replace(Normalize(key), @"\s+", "");
            return !string.IsNullOrWhiteSpace(b) && (a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase));
        }

        private static bool SlotIs(Dictionary<string, string> slots, string k, string v) =>
            slots.TryGetValue(k, out var cur) && cur.Contains(v, StringComparison.OrdinalIgnoreCase);

        private static bool IsSearchNeedContinuation(string n, AIChatSession session, Dictionary<string, string> slots)
        {
            bool activeSearchScenario = session.Scenario == "Buy" || session.Scenario == "Rent" ||
                                        SlotIs(slots, "deal_type", "Mua") || SlotIs(slots, "deal_type", "Thuê");
            if (!activeSearchScenario) return false;

            // Nếu người dùng chuyển hẳn sang pháp lý/vay/đăng tin/hỗ trợ thì không coi là bổ sung tiêu chí tìm tin.
            if (StandaloneLoan(n) || StandaloneLegal(n) || StandaloneTransaction(n) ||
                StandalonePosting(n) || StandaloneProject(n) || Complaint(n) || Appointment(n) || Support(n))
                return false;

            bool givesBudget = MoneyValues(n).Any() || MoneyRange(n).HasValue || ContainsAny(n, "duoi", "tren", "tam", "khoang", "ngan sach", "gia", "toi da", "max", "min", "tro lai");
            bool givesPurpose = Purpose(n) != null;
            bool givesLegal = LegalNeed(n) != null;
            bool givesType = PropertyType(n) != null;
            bool givesArea = AreaName(n) != null;
            bool givesRoad = RoadNeed(n) != null;
            bool givesAmenity = Amenities(n) != null;
            bool givesAreaSize = Regex.IsMatch(Normalize(n), @"\d+(?:[\.]\d+)?\s*(m2|met|m\b)", RegexOptions.IgnoreCase);
            bool shortAnswer = ContainsAny(n, "de o", "dau tu", "kinh doanh", "o thuc", "co so", "so rieng", "phap ly ro", "duong o to", "mat tien");

            return givesBudget || givesPurpose || givesLegal || givesType || givesArea || givesRoad || givesAmenity || givesAreaSize || shortAnswer || Refinement(n) || Another(n);
        }

        private static bool IsMultiIntentQuestion(string n)
        {
            int count = 0;
            if (HasPropertyNeedIntent(n)) count++;
            if (Legal(n)) count++;
            if (Loan(n)) count++;
            if (Transaction(n)) count++;
            if (Posting(n)) count++;
            if (Project(n)) count++;
            if (Market(n)) count++;
            if (Support(n) || Complaint(n)) count++;
            return count >= 2 && ContainsAny(n, "va", "và", "luon", "cung", "dong thoi", "nhung", "hoi", "voi", "kem", "ca", "nua", "roi", "sau do");
        }

        private static bool ExplicitListing(string n)
        {
            bool clearListing = ContainsAny(n, SearchListingWords);
            bool enoughTextSignal = (Buy(n) || Rent(n) || Search(n)) && PropertyType(n) != null && AreaName(n) != null &&
                                    (MoneyValues(n).Any() || MoneyRange(n).HasValue || ContainsAny(n, "duoi", "tren", "ngan sach", "gia", "khoang", "tam", "toi da", "tro lai"));
            return clearListing || enoughTextSignal;
        }

        private static bool BuySafety(string n) => ContainsAny(n,
            "khong biet mua nhu nao cho an toan", "mua nhu nao cho an toan", "mua dat an toan", "mua nha an toan",
            "can kiem tra gi khi mua", "lan dau mua dat", "lan dau mua nha", "so bi lua", "tranh bi lua", "kinh nghiem mua dat",
            "kinh nghiem mua nha", "mua dat can luu y gi", "mua nha can luu y gi", "di xem dat can hoi gi", "di xem nha can hoi gi",
            "moi mua lan dau", "khong ranh phap ly", "khong biet coi so", "huong dan mua an toan");

        private static bool Buy(string n)
        {
            bool hasBuySignal = ContainsAny(n, BuyNeedWords) || (ContainsAny(n, "mua", "can mua", "muon mua", "tim mua", "kiem mua") && PropertyType(n) != null);
            bool actuallyRentOrLoan = ContainsAny(n, "vay ngan hang", "thu nhap", "lai suat", "hop dong thue", "can thue", "cho thue", "muon thue", "tim thue");
            bool sellerPosting = ContainsAny(n, "toi muon ban", "can ban", "dang tin ban", "ban nha cua toi", "ban dat cua toi");
            return hasBuySignal && !actuallyRentOrLoan && !sellerPosting;
        }

        private static bool Rent(string n)
        {
            bool rentSignal = ContainsAny(n, RentNeedWords) || (ContainsAny(n, "thue", "can thue", "muon thue", "tim thue", "kiem thue") && PropertyType(n) != null);
            bool seller = ContainsAny(n, "cho thue nha cua toi", "dang tin cho thue", "toi muon cho thue", "can cho thue");
            return rentSignal && !seller && !ContainsAny(n, "toi muon ban");
        }

        private static bool Search(string n) => ContainsAny(n, SearchListingWords);
        private static bool Another(string n) => ContainsAny(n, AnotherWords);
        private static bool Refinement(string n) => ContainsAny(n, RefinementWords);
        private static bool Appointment(string n) => ContainsAny(n, AppointmentWords);
        private static bool CurrentProperty(string n) => ContainsAny(n, CurrentPropertyWords);
        private static bool Legal(string n) => ContainsAny(n, LegalWords);
        private static bool Transaction(string n) => ContainsAny(n, TransactionWords);
        private static bool Loan(string n) => ContainsAny(n, LoanWords);
        private static bool Posting(string n) => ContainsAny(n, PostingWords);
        private static bool Project(string n) => ContainsAny(n, ProjectWords);
        private static bool Support(string n) => ContainsAny(n, SupportWords);
        private static bool Complaint(string n) => ContainsAny(n, ComplaintWords);
        private static bool Market(string n) => ContainsAny(n, MarketWords);

        private static bool StandaloneLegal(string n)
        {
            bool legal = Legal(n) || BuildPermissionQuestion(n);
            bool strong = ContainsAny(n, "quy hoach", "giay tay", "anh chup so", "so do va so hong", "kiem tra", "duoc xay", "xay nha", "muc dich su dung dat", "tach thua", "sang ten duoc khong", "the chap", "tranh chap", "hoan cong", "so chung", "so rieng");
            bool notSearch = !ContainsAny(n, SearchListingWords) && !Buy(n) && !Rent(n);
            return legal && (strong || n.Length < 160) && notSearch;
        }

        private static bool StandaloneTransaction(string n)
        {
            bool strong = ContainsAny(n, "quy trinh", "thu tuc", "sang ten", "cong chung", "dat coc", "chuyen het tien", "thanh toan", "thue phi", "chi phi", "sau khi cong chung", "ben nao chiu thue");
            return Transaction(n) && strong && !ExplicitListing(n);
        }

        private static bool StandaloneLoan(string n)
        {
            bool strong = ContainsAny(n, "thu nhap", "luong", "vay duoc bao nhieu", "lai suat", "ngan hang co cho vay", "ho so vay", "ap luc", "nen vay", "vay 50", "vay 70", "vay 80", "vay 1 ty", "vay 2 ty", "trong 20 nam", "dung het tien tiet kiem", "vay xay nha", "cic", "no xau", "the chap");
            bool mixedBuyLoan = ContainsAny(n, "mua nha", "mua dat") && ContainsAny(n, "luong", "thu nhap", "vay", "ap luc", "lai suat");
            return Loan(n) && (strong || mixedBuyLoan) && !ExplicitListing(n);
        }

        private static bool StandalonePosting(string n)
        {
            bool strong = ContainsAny(n, "huong dan", "viet", "soan", "bi tu choi", "ban nhanh", "dinh gia", "ep gia", "trinh bay", "de co khach", "bao loi", "chac chan tang gia", "dang tin", "sua tin", "them anh", "xoa anh");
            return Posting(n) && strong;
        }

        private static bool StandaloneProject(string n)
        {
            bool strong = ContainsAny(n, "kiem tra", "rui ro", "bao lanh", "cam ket loi nhuan", "chu dau tu", "phap ly du an", "tien do", "giu cho", "booking", "hop dong mua ban");
            return Project(n) && strong && !ExplicitListing(n);
        }

        private static bool StandaloneMarket(string n)
        {
            bool strong = ContainsAny(n, "so sanh", "nen mua", "co dang nghi", "uu tien", "ban lai", "thanh khoan", "giu tai san", "nen chon", "kha thi", "co on khong", "hoi nguoi ban", "ngan sach gioi han", "giam dien tich", "giam vi tri", "dat mat tien", "duong nho", "hem nho", "thi truong", "thoi diem mua");
            return (Market(n) || FeasibilityAdviceQuestion(n)) && strong && !ExplicitListing(n);
        }

        private static bool Unsafe(string n) => ContainsAny(n, UnsafeWords);

        private static bool OffTopic(string n)
        {
            bool hasBdsSignal = ContainsAny(n,
                "bat dong san", "bds", "nha", "dat", "can ho", "chung cu", "phong tro", "mat bang", "van phong", "kinh doanh",
                "thue", "mua", "ban", "du an", "so do", "so hong", "phap ly", "quy hoach", "dat coc", "cong chung", "vay ngan hang",
                "nha trang", "khanh hoa", "dien khanh", "cam lam", "cam ranh", "ninh hoa", "van ninh", "khanh vinh", "khanh son",
                "phan rang", "ninh thuan", "ninh hai", "ninh phuoc", "thuan nam", "thuan bac", "ninh son", "bac ai");
            return !hasBdsSignal && ContainsAny(n, OffTopicWords);
        }

        // =====================================================================
        // BỘ MẪU CÂU TEST Ý ĐỊNH THỰC TẾ - GIỮ LẠI TRONG CODE ĐỂ SAU NÀY DỄ BẢO TRÌ
        // =====================================================================
        // BUY/Search:
        // - "Tui muốn kiếm miếng đất Nha Trang dưới 2 tỷ có sổ riêng"
        // - "Có căn nào Cam Lâm tầm 1 tỷ 5 không em"
        // - "Kiếm giúp anh nhà Ninh Hòa dưới 3ty đường oto"
        // - "Tầm 800tr mua được đất khu nào ở Khánh Hòa"
        // - "Mình cần mua căn hộ gần biển Nha Trang khoảng 2-3 tỷ"
        // - "co lo dat nao dien khanh duoi 1ty khong"
        // - "mua đất để xây nhà, khu Cam Ranh, khoảng 1.2 tỷ"
        // - "tôi muốn mua mặt bằng kinh doanh ở Nha Trang dưới 5 tỷ"
        // - "cần căn nhà vừa ở vừa kinh doanh khu trung tâm"
        // - "kiếm căn nào pháp lý rõ hơn, giá mềm hơn"
        // RENT/Search:
        // - "Cho em thuê phòng trọ Nha Trang dưới 3tr/tháng"
        // - "Tìm nhà nguyên căn Cam Ranh khoảng 8 triệu"
        // - "Có mặt bằng nào thuê kinh doanh ở Ninh Hòa không"
        // - "Kiếm văn phòng nhỏ gần trung tâm Nha Trang"
        // - "Cần thuê căn hộ có nội thất, dưới 10tr"
        // FOLLOW-UP/Continuation:
        // - User: "Tôi muốn mua đất ở Nha Trang" -> Bot hỏi thêm
        // - User: "Dưới 2 tỷ, có sổ, để ở" -> vẫn giữ scenario Buy, không rơi General
        // - User: "Rẻ hơn chút" -> refinement, không reset slot
        // - User: "Còn tin nào khác không" -> Another, giữ nhu cầu trước
        // PROPERTY DETAIL:
        // - "Tin này có nên mua không"
        // - "Giá này có hợp lý không"
        // - "Đất này ở khu vực nào"
        // - "Ai là chủ tin, uy tín không"
        // - "Có nên đặt cọc tin này không"
        // - "Tin này có rủi ro pháp lý gì"
        // LEGAL:
        // - "Sổ đỏ và sổ hồng khác nhau sao"
        // - "Đất nông nghiệp có xây nhà được không"
        // - "Chỉ xem ảnh chụp sổ có cọc được không"
        // - "Đất đang thế chấp ngân hàng mua được không"
        // - "Mua giấy tay có sao không"
        // - "Kiểm tra quy hoạch ở đâu"
        // TRANSACTION:
        // - "Đặt cọc cần ghi điều khoản gì"
        // - "Quy trình sang tên nhà đất như nào"
        // - "Công chứng cần chuẩn bị giấy tờ gì"
        // - "Sau khi công chứng làm gì tiếp"
        // - "Thuế phí khi bán lại tính sao"
        // LOAN:
        // - "Lương 20tr vay mua nhà được bao nhiêu"
        // - "Mua đất có sổ đỏ ngân hàng có cho vay không"
        // - "Nên vay 50% hay 70%"
        // - "Lãi suất thả nổi rủi ro gì"
        // - "Đất chưa có thổ cư ngân hàng có nhận thế chấp không"
        // POSTING:
        // - "Muốn bán nhanh nhưng không bị ép giá thì viết tin sao"
        // - "Có nên ghi bao lời chắc chắn tăng giá không"
        // - "Tin đăng bị từ chối sửa sao"
        // - "Viết giúp 3 tiêu đề bán đất"
        // - "Đăng ảnh thế nào cho khách tin"
        // PROJECT:
        // - "Dự án hình thành trong tương lai cần kiểm tra gì"
        // - "Chủ đầu tư này uy tín không"
        // - "Cam kết lợi nhuận có đáng tin không"
        // - "Booking giữ chỗ dự án có rủi ro không"
        // SUPPORT/CARE:
        // - "Tôi thanh toán rồi chưa được cộng lượt"
        // - "Không đăng tin được"
        // - "Muốn báo cáo tin lừa đảo"
        // - "Số điện thoại người đăng không đúng"
        // UNSAFE:
        // - "Làm hợp đồng hai giá để né thuế"
        // - "Cách giấu quy hoạch khi bán"
        // - "Viết tin sai pháp lý cho dễ bán"
        // OFFTOPIC:
        // - "Hôm nay ăn gì"
        // - "Tư vấn tình yêu"
        // - "Chơi game gì vui"
        // =====================================================================

        private static List<string> RepliesForRoute(Route r, string s) => r == Route.Clarify ? new List<string> { "Mua để ở", "Mua đầu tư", "Dưới 1 tỷ", "Cần sổ riêng" } : Replies(s);
        private static List<string> Replies(string s) => s switch
        {
            "Buy" => new() { "Lọc giá thấp hơn", "Ưu tiên pháp lý rõ", "Mở rộng khu vực", "Mua đất cần kiểm tra gì?" },
            "BuyAdvice" => new() { "Mua đất cần kiểm tra gì?", "Ưu tiên pháp lý hay vị trí?", "Có nên vay ngân hàng?", "Lọc tin phù hợp" },
            "Rent" => new() { "Lọc dưới ngân sách", "Gần trung tâm", "Có nội thất", "Hợp đồng thuê cần lưu ý gì?" },
            "RentAdvice" => new() { "Hợp đồng thuê cần lưu ý gì?", "Cọc bao nhiêu là hợp lý?", "Thuê để kinh doanh cần gì?", "Tìm tin thuê" },
            "Legal" => new() { "Mua đất cần giấy tờ gì?", "Kiểm tra quy hoạch ở đâu?", "Có nên đặt cọc không?" },
            "PropertyDetail" => new() { "Tin này có nên mua không?", "Mua để đầu tư được không?", "Giá này hợp lý không?", "Pháp lý cần kiểm tra gì?" },
            "ProjectDetail" => new() { "Dự án này có nên mua không?", "Pháp lý dự án cần kiểm tra gì?", "Chủ đầu tư uy tín không?", "Có nên booking không?" },
            "Care" => new() { "Hướng dẫn tìm tin", "Hướng dẫn xem dự án", "Hướng dẫn đăng tin", "Cách báo cáo vi phạm" },
            _ => new() { "Tôi muốn mua đất", "Tôi muốn thuê nhà", "Mua đất cần kiểm tra gì?", "Hướng dẫn sử dụng web" }
        };

        private enum Route { Clarify, Search, PageAnalysis, Direct, AI, Refuse, OffTopic }
        private sealed class Scenario { public Scenario(string name, string intent, bool guide, bool search, bool needHuman) { Name = name; Intent = intent; ShouldGuide = guide; ShouldSearch = search; NeedHuman = needHuman; } public string Name { get; } public string Intent { get; } public bool ShouldGuide { get; } public bool ShouldSearch { get; } public bool NeedHuman { get; } }
        private sealed class SlotPlan { public List<string> Missing { get; set; } = new(); }
        private sealed class PageInfo { public string PageType { get; set; } = "General"; public string PageUrl { get; set; } = ""; public string PageTitle { get; set; } = ""; public string ContextText { get; set; } = ""; public bool IsPropertyDetail => PageType == "PropertyDetail"; public bool IsProjectDetail => PageType == "ProjectDetail"; public bool HasUsefulContext => !string.IsNullOrWhiteSpace(ContextText) && ContainsAny(ContextText, "gia", "dien tich", "vi tri", "phap ly", "loai", "tieu de", "du an", "chu dau tu", "khu vuc", "trang thai", "mo ta"); }
    }
}
