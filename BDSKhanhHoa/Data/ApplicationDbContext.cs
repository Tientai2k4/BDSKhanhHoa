using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Models;
using System.Data;
using Microsoft.Data.SqlClient;

namespace BDSKhanhHoa.Data
{
    public class ApplicationDbContext : DbContext
    {
        private readonly IConfiguration _configuration;

        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, IConfiguration configuration)
            : base(options)
        {
            _configuration = configuration;
        }

        // --- CÁC BẢNG CỐT LÕI ---
        public DbSet<User> Users { get; set; }
        public DbSet<Property> Properties { get; set; }
        public DbSet<Area> Areas { get; set; }
        public DbSet<Ward> Wards { get; set; }
        public DbSet<PropertyType> PropertyTypes { get; set; }
        public DbSet<Blog> Blogs { get; set; }
        public DbSet<ContactMessage> ContactMessages { get; set; }
        public DbSet<Favorite> Favorites { get; set; }
        public DbSet<PropertyFeature> PropertyFeatures { get; set; }
        public DbSet<AuditLog> AuditLogs { get; set; }
        public DbSet<Transaction> Transactions { get; set; }
        public DbSet<UserViolation> UserViolations { get; set; }
        public DbSet<Banner> Banners { get; set; }
        public DbSet<PostServicePackage> PostServicePackages { get; set; }
        public DbSet<PropertyImage> PropertyImages { get; set; }
        public DbSet<PropertyReport> PropertyReports { get; set; }
        public DbSet<Project> Projects { get; set; }

        // --- BẢNG CHATBOT AI ---
        public DbSet<AIKnowledgeArticle> AIKnowledgeArticles { get; set; }
        public DbSet<AIChatSession> AIChatSessions { get; set; }
        public DbSet<AIChatMessage> AIChatMessages { get; set; }

        // --- BẢNG TƯƠNG TÁC DETAILS ---
        public DbSet<Comment> Comments { get; set; }
        public DbSet<Consultation> Consultations { get; set; }
        public DbSet<Appointment> Appointments { get; set; }
        public DbSet<Voucher> Vouchers { get; set; }
        public DbSet<UserMessage> UserMessages { get; set; }
        public DbSet<Role> Roles { get; set; }
        public DbSet<ProjectLead> ProjectLeads { get; set; }
        public DbSet<StaticPage> StaticPages { get; set; }

        public DbSet<BusinessProfile> BusinessProfiles { get; set; }
        public DbSet<Notification> Notifications { get; set; }
        public DbSet<ConversationReport> ConversationReports { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // =========================================================
            // 1. CẤU HÌNH KHÓA CHÍNH CÁC BẢNG CÓ CHẮC TÊN KHÓA
            // Không cấu hình ConsultationID và BankAccountID vì model của bạn không có 2 property này.
            // =========================================================

            modelBuilder.Entity<User>().HasKey(u => u.UserID);
            modelBuilder.Entity<Property>().HasKey(p => p.PropertyID);
            modelBuilder.Entity<Area>().HasKey(a => a.AreaID);
            modelBuilder.Entity<Ward>().HasKey(w => w.WardID);
            modelBuilder.Entity<PropertyType>().HasKey(pt => pt.TypeID);
            modelBuilder.Entity<ContactMessage>().HasKey(c => c.ContactID);
            modelBuilder.Entity<Blog>().HasKey(b => b.BlogID);
            modelBuilder.Entity<Notification>().HasKey(n => n.NotificationID);
            modelBuilder.Entity<AuditLog>().HasKey(al => al.LogID);
            modelBuilder.Entity<Transaction>().HasKey(t => t.TransactionID);
            modelBuilder.Entity<UserViolation>().HasKey(uv => uv.ViolationID);
            modelBuilder.Entity<PropertyImage>().HasKey(pi => pi.ImageID);
            modelBuilder.Entity<Banner>().HasKey(b => b.BannerID);
            modelBuilder.Entity<PostServicePackage>().HasKey(p => p.PackageID);
            modelBuilder.Entity<PropertyReport>().HasKey(p => p.ReportID);
            modelBuilder.Entity<Project>().HasKey(p => p.ProjectID);
            modelBuilder.Entity<Comment>().HasKey(c => c.CommentID);
            modelBuilder.Entity<Appointment>().HasKey(a => a.AppointmentID);
            modelBuilder.Entity<Voucher>().HasKey(v => v.VoucherID);
            modelBuilder.Entity<UserMessage>().HasKey(u => u.MessageID);
            modelBuilder.Entity<Role>().HasKey(r => r.RoleID);
            modelBuilder.Entity<ProjectLead>().HasKey(p => p.LeadID);
            modelBuilder.Entity<StaticPage>().HasKey(s => s.PageID);
            modelBuilder.Entity<BusinessProfile>().HasKey(b => b.BusinessProfileID);
            modelBuilder.Entity<ConversationReport>().HasKey(c => c.ReportID);

            // =========================================================
            // 2. FIX DECIMAL
            // =========================================================

            modelBuilder.Entity<PostServicePackage>()
                .Property(p => p.Price)
                .HasPrecision(18, 2);

            modelBuilder.Entity<Voucher>()
                .Property(v => v.DiscountPercent)
                .HasPrecision(5, 2);

            modelBuilder.Entity<Voucher>()
                .Property(v => v.MaxDiscountAmount)
                .HasPrecision(18, 2);

            modelBuilder.Entity<Property>()
                .Property(p => p.Price)
                .HasColumnType("decimal(18,2)");

            modelBuilder.Entity<Property>()
                .Property(p => p.AreaSize)
                .HasColumnType("decimal(18,2)");

            modelBuilder.Entity<Transaction>()
                .Property(t => t.Amount)
                .HasColumnType("decimal(18,2)");

            // =========================================================
            // 3. CẤU HÌNH QUAN HỆ PROPERTY
            // =========================================================

            modelBuilder.Entity<Property>()
                .HasOne(p => p.User)
                .WithMany()
                .HasForeignKey(p => p.UserID)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<PropertyType>()
                .HasOne<PropertyType>()
                .WithMany()
                .HasForeignKey(pt => pt.ParentID)
                .OnDelete(DeleteBehavior.Restrict);

            // =========================================================
            // 4. CẤU HÌNH QUAN HỆ LOG / GIAO DỊCH / THÔNG BÁO
            // =========================================================

            modelBuilder.Entity<AuditLog>()
                .HasOne(al => al.User)
                .WithMany()
                .HasForeignKey(al => al.UserID)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Transaction>()
                .HasOne(t => t.User)
                .WithMany()
                .HasForeignKey(t => t.UserID)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<UserViolation>()
                .HasOne(uv => uv.User)
                .WithMany()
                .HasForeignKey(uv => uv.UserID)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Notification>()
                .HasOne(n => n.User)
                .WithMany()
                .HasForeignKey(n => n.UserID)
                .OnDelete(DeleteBehavior.Cascade);

            // =========================================================
            // 5. CẤU HÌNH APPOINTMENT
            // =========================================================

            modelBuilder.Entity<Appointment>()
                .HasOne(a => a.Buyer)
                .WithMany()
                .HasForeignKey(a => a.BuyerID)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Appointment>()
                .HasOne(a => a.Seller)
                .WithMany()
                .HasForeignKey(a => a.SellerID)
                .OnDelete(DeleteBehavior.Restrict);

            // =========================================================
            // 6. CẤU HÌNH COMMENT
            // =========================================================

            modelBuilder.Entity<Comment>()
                .HasOne(c => c.User)
                .WithMany()
                .HasForeignKey(c => c.UserID)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Comment>()
                .HasOne(c => c.ParentComment)
                .WithMany(c => c.Replies)
                .HasForeignKey(c => c.ParentID)
                .OnDelete(DeleteBehavior.Restrict);

            // =========================================================
            // 7. CẤU HÌNH CHATBOT AI
            // Không dùng Cascade để tránh lỗi multiple cascade paths trên SQL Server.
            // =========================================================

            modelBuilder.Entity<AIKnowledgeArticle>(entity =>
            {
                entity.ToTable("AIKnowledgeArticles");

                entity.HasKey(e => e.ArticleID);

                entity.Property(e => e.Title)
                    .HasMaxLength(255)
                    .IsRequired();

                entity.Property(e => e.Category)
                    .HasMaxLength(80)
                    .IsRequired();

                entity.Property(e => e.Content)
                    .HasColumnType("nvarchar(max)")
                    .IsRequired();

                entity.Property(e => e.IsPublished)
                    .HasDefaultValue(true);

                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("SYSDATETIME()");

                entity.Property(e => e.UpdatedAt)
                    .HasDefaultValueSql("SYSDATETIME()");

                entity.HasIndex(e => new { e.Category, e.IsPublished, e.UpdatedAt })
                    .HasDatabaseName("IX_AIKnowledgeArticles_Category_Published");
            });

            modelBuilder.Entity<AIChatSession>(entity =>
            {
                entity.ToTable("AIChatSessions");

                entity.HasKey(e => e.SessionID);

                entity.Property(e => e.SessionKey)
                    .HasMaxLength(100)
                    .IsRequired();

                entity.Property(e => e.Scenario)
                    .HasMaxLength(80);

                entity.Property(e => e.Stage)
                    .HasMaxLength(80);

                entity.Property(e => e.PageType)
                    .HasMaxLength(80);

                entity.Property(e => e.PageUrl)
                    .HasMaxLength(500);

                entity.Property(e => e.PageTitle)
                    .HasMaxLength(500);

                entity.Property(e => e.LastIntent)
                    .HasMaxLength(80);

                entity.Property(e => e.CollectedDataJson)
                    .HasColumnType("nvarchar(max)");

                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("SYSDATETIME()");

                entity.Property(e => e.UpdatedAt)
                    .HasDefaultValueSql("SYSDATETIME()");

                entity.HasIndex(e => e.SessionKey)
                    .IsUnique()
                    .HasDatabaseName("IX_AIChatSessions_SessionKey");

                entity.HasIndex(e => e.UpdatedAt)
                    .HasDatabaseName("IX_AIChatSessions_UpdatedAt");

                entity.HasOne(e => e.User)
                    .WithMany()
                    .HasForeignKey(e => e.UserID)
                    .OnDelete(DeleteBehavior.NoAction);

                entity.HasMany(e => e.Messages)
                    .WithOne(e => e.Session)
                    .HasForeignKey(e => e.SessionID)
                    .OnDelete(DeleteBehavior.NoAction);

              
            });

            modelBuilder.Entity<AIChatMessage>(entity =>
            {
                entity.ToTable("AIChatMessages");

                entity.HasKey(e => e.MessageID);

                entity.Property(e => e.Role)
                    .HasMaxLength(20)
                    .IsRequired();

                entity.Property(e => e.Content)
                    .HasColumnType("nvarchar(max)")
                    .IsRequired();

                entity.Property(e => e.Intent)
                    .HasMaxLength(80);

                entity.Property(e => e.ToolTrace)
                    .HasColumnType("nvarchar(max)");

                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("SYSDATETIME()");

                entity.HasIndex(e => new { e.SessionID, e.CreatedAt })
                    .HasDatabaseName("IX_AIChatMessages_SessionID_CreatedAt");

                entity.HasOne(e => e.Session)
                    .WithMany(e => e.Messages)
                    .HasForeignKey(e => e.SessionID)
                    .OnDelete(DeleteBehavior.NoAction);
            });

            modelBuilder.Entity<AIChatFeedback>(entity =>
            {
                entity.ToTable("AIChatFeedbacks");

                entity.HasKey(e => e.FeedbackID);

                entity.Property(e => e.Rating)
                    .IsRequired();

                entity.Property(e => e.Comment)
                    .HasColumnType("nvarchar(max)");

                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("SYSDATETIME()");

                entity.HasIndex(e => e.SessionID)
                    .HasDatabaseName("IX_AIChatFeedbacks_SessionID");

             

                entity.HasOne(e => e.Message)
                    .WithMany()
                    .HasForeignKey(e => e.MessageID)
                    .OnDelete(DeleteBehavior.NoAction);
            });
        }

        public IDbConnection CreateConnection()
        {
            return new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        }
    }
}